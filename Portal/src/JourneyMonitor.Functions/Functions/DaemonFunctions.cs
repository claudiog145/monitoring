using System.Globalization;
using System.Net;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring.Daemons;
using JourneyMonitor.Core.Monitoring.Kpi;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace JourneyMonitor.Functions.Functions;

/// <summary>
/// Spec 1000 daemon monitoring reads (contracts-daemons §5 "API"): one status card per daemon and the
/// activity console with afterId paging. Anonymous, read-only.
/// </summary>
public sealed class DaemonFunctions(
    IDaemonStatusReader statusReader,
    IDaemonActivityLog activityLog,
    ILogger<DaemonFunctions> logger,
    KpiProfileResolver? profileResolver = null,
    IKpiDefinitionStore? kpiDefinitions = null)
{
    public const int DefaultTop = 200;
    public const int MaxTop = 1000;

    [Function("GetDaemons")]
    public async Task<HttpResponseData> GetDaemonsAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "daemons")] HttpRequestData req,
        CancellationToken ct)
    {
        try
        {
            // Contract v6 §5 (MC7): the card texts are the requested company's; health stays the daemon's.
            var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
            var (scope, scopeError) = await ProfileReadScope.ResolveAsync(profileResolver, query["profileKey"], ct);
            if (scope is null) return await Spec1000Http.BadRequestAsync(req, [scopeError!]);

            var statuses = await statusReader.GetAsync(ct);
            if (!scope.IsEverything)
            {
                var owners = await ProfileDaemonActivityReader.LoadKpiOwnersAsync(kpiDefinitions, ct);
                statuses = await ProfileDaemonStatusScoper.ScopeAsync(statuses, activityLog, scope, owners, ct);
            }
            return await Spec1000Http.WriteJsonAsync(req, HttpStatusCode.OK, statuses);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GetDaemons failed unexpectedly");
            return await Spec1000Http.WriteJsonAsync(req, HttpStatusCode.InternalServerError, new { error = "Unexpected server error." });
        }
    }

    [Function("GetDaemonActivity")]
    public async Task<HttpResponseData> GetDaemonActivityAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "daemons/activity")] HttpRequestData req,
        CancellationToken ct)
    {
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        var errors = new List<string>();

        long afterId = 0;
        var afterRaw = query["afterId"];
        if (!string.IsNullOrWhiteSpace(afterRaw)
            && (!long.TryParse(afterRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out afterId) || afterId < 0))
        {
            errors.Add("afterId must be an integer >= 0.");
        }

        if (!Spec1000Http.TryReadIntInRange(query["top"], DefaultTop, 1, MaxTop, out var top))
        {
            errors.Add($"top must be an integer between 1 and {MaxTop}.");
        }

        string? daemonKey = null;
        var daemonRaw = query["daemon"];
        if (!string.IsNullOrWhiteSpace(daemonRaw))
        {
            daemonKey = DaemonKeys.All.FirstOrDefault(k => string.Equals(k, daemonRaw.Trim(), StringComparison.OrdinalIgnoreCase));
            if (daemonKey is null) errors.Add($"daemon must be one of: {string.Join(", ", DaemonKeys.All)}.");
        }

        DaemonActivityLevel? minLevel = null;
        var levelRaw = query["level"];
        if (!string.IsNullOrWhiteSpace(levelRaw))
        {
            if (Enum.TryParse<DaemonActivityLevel>(levelRaw.Trim(), ignoreCase: true, out var parsed)
                && Enum.IsDefined(parsed)
                && !int.TryParse(levelRaw.Trim(), out _))
            {
                minLevel = parsed;
            }
            else
            {
                errors.Add("level must be Info, Warning or Error.");
            }
        }

        if (errors.Count > 0) return await Spec1000Http.BadRequestAsync(req, errors);

        try
        {
            // Contract v6 §5: missing profileKey → the active company, "*" → every company; global entries always show.
            var (scope, scopeError) = await ProfileReadScope.ResolveAsync(profileResolver, query["profileKey"], ct);
            if (scope is null) return await Spec1000Http.BadRequestAsync(req, [scopeError!]);

            var owners = scope.IsEverything ? null : await ProfileDaemonActivityReader.LoadKpiOwnersAsync(kpiDefinitions, ct);
            var page = await ProfileDaemonActivityReader.GetAsync(activityLog, afterId, daemonKey, minLevel, top, scope, kpiOwners: owners, ct: ct);
            return await Spec1000Http.WriteJsonAsync(req, HttpStatusCode.OK, page);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GetDaemonActivity failed unexpectedly");
            return await Spec1000Http.WriteJsonAsync(req, HttpStatusCode.InternalServerError, new { error = "Unexpected server error." });
        }
    }
}
