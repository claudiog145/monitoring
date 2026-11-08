using System.Net;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring.Kpi;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace JourneyMonitor.Functions.Functions;

/// <summary>
/// Contract v4 §4: GET /api/scouts/{board}/shadow?hours=&amp;profileKey=&amp;top= - the rules-vs-method comparison of
/// one scout. Anonymous read like the other Scout reads (no X-Internal-Api-Secret): counts per agreement and the
/// latest rows, newest first. profileKey defaults to the active profile; "*" means every profile.
/// </summary>
public sealed class ScoutShadowFunctions(
    IScoutShadowStore store,
    ILogger<ScoutShadowFunctions> logger,
    KpiProfileResolver? profileResolver = null)
{
    private readonly KpiProfileResolver _profiles = profileResolver ?? new KpiProfileResolver();

    public const int DefaultHours = 24;
    /// <summary>Shadow rows are retained 8 days.</summary>
    public const int MaxHours = 192;
    public const int DefaultTop = 50;
    public const int MaxTop = 500;

    [Function("GetScoutShadow")]
    public async Task<HttpResponseData> GetShadowAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "scouts/{board}/shadow")] HttpRequestData req,
        string board,
        CancellationToken ct)
    {
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        var errors = new List<string>();

        var boardKey = ScoutBoard.All.FirstOrDefault(b => string.Equals(b, (board ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
        if (boardKey is null) errors.Add($"board must be one of {string.Join(", ", ScoutBoard.All)}.");
        if (!Spec1000Http.TryReadIntInRange(query["hours"], DefaultHours, 1, MaxHours, out var hours))
        {
            errors.Add($"hours must be an integer between 1 and {MaxHours}.");
        }
        if (!Spec1000Http.TryReadIntInRange(query["top"], DefaultTop, 1, MaxTop, out var top))
        {
            errors.Add($"top must be an integer between 1 and {MaxTop}.");
        }
        if (errors.Count > 0) return await Spec1000Http.BadRequestAsync(req, errors);

        try
        {
            var scope = await _profiles.ResolveReadAsync(query["profileKey"], ct);
            if (!scope.IsValid) return await Spec1000Http.BadRequestAsync(req, [scope.Error!]);

            var counts = await store.GetCountsAsync(boardKey!, hours, scope.ProfileKey, ct);
            var rows = await store.GetRecentAsync(boardKey!, hours, scope.ProfileKey, ct);

            return await Spec1000Http.WriteJsonAsync(req, HttpStatusCode.OK, new
            {
                board = boardKey,
                profileKey = scope.ProfileKey ?? KpiProfiles.All,
                hours,
                counts = new
                {
                    both = counts.Both,
                    rulesOnly = counts.RulesOnly,
                    methodOnly = counts.MethodOnly,
                    total = counts.Total,
                },
                rows = rows.Take(top).Select(r => new
                {
                    runId = r.RunId,
                    profileKey = r.ProfileKey,
                    entityKey = r.EntityKey,
                    stage = r.Stage,
                    bucketStart = r.BucketStart,
                    ruleRank = r.RuleRank,
                    methodVerdict = r.MethodVerdict,
                    kpiAnomalyId = r.KpiAnomalyId,
                    agreement = r.Agreement,
                }).ToList(),
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GetScoutShadow failed for board {Board}.", boardKey);
            return await Spec1000Http.WriteJsonAsync(req, HttpStatusCode.InternalServerError, new { error = "Unexpected server error." });
        }
    }
}
