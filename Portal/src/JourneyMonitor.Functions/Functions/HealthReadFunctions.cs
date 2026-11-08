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
/// Spec 1000 read-only health endpoints the portal renders: pillar summary, recent anomalies and
/// the bucket series of one KPI. Anonymous reads, like every other read in this Functions app.
/// </summary>
public sealed class HealthReadFunctions(
    IPillarHealthReader pillarHealthReader,
    IKpiAnomalyStore anomalyStore,
    IKpiBucketStore bucketStore,
    IKpiDefinitionStore definitionStore,
    TimeProvider time,
    ILogger<HealthReadFunctions> logger,
    KpiProfileResolver? profileResolver = null)
{
    private readonly KpiProfileResolver _profiles = profileResolver ?? new KpiProfileResolver();

    public const int DefaultAnomaliesTop = 100;
    public const int DefaultBucketHours = 48;
    public const int MaxBucketHours = 720;

    [Function("GetPillarHealth")]
    public async Task<HttpResponseData> GetPillarHealthAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "health/pillars")] HttpRequestData req,
        CancellationToken ct)
    {
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        var domain = NullIfBlank(query["domain"]);
        try
        {
            var scope = await _profiles.ResolveReadAsync(query["profileKey"], ct);
            if (!scope.IsValid) return await Spec1000Http.BadRequestAsync(req, [scope.Error!]);

            var pillars = await pillarHealthReader.GetAsync(domain, scope.ProfileKey, ct);
            return await Spec1000Http.WriteJsonAsync(req, HttpStatusCode.OK, pillars.Select(p => p with { ProfileKey = scope.ProfileKey ?? KpiProfiles.All }).ToList());
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GetPillarHealth failed unexpectedly");
            return await Spec1000Http.WriteJsonAsync(req, HttpStatusCode.InternalServerError, new { error = "Unexpected server error." });
        }
    }

    [Function("GetKpiAnomalies")]
    public async Task<HttpResponseData> GetAnomaliesAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "health/anomalies")] HttpRequestData req,
        CancellationToken ct)
    {
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        var errors = new List<string>();

        KpiPillar? pillar = null;
        var pillarText = NullIfBlank(query["pillar"]);
        if (pillarText is not null)
        {
            if (KpiPillarNames.TryParseDisplay(pillarText, out var parsed)) pillar = parsed;
            else errors.Add("pillar must be Business Health, Application Health or Infrastructure Health.");
        }

        if (!Spec1000Http.TryReadIntInRange(query["top"], DefaultAnomaliesTop, 1, Spec1000Http.MaxTop, out var top))
        {
            errors.Add($"top must be an integer between 1 and {Spec1000Http.MaxTop}.");
        }

        if (errors.Count > 0) return await Spec1000Http.BadRequestAsync(req, errors);

        try
        {
            var scope = await _profiles.ResolveReadAsync(query["profileKey"], ct);
            if (!scope.IsValid) return await Spec1000Http.BadRequestAsync(req, [scope.Error!]);

            var anomalies = await anomalyStore.GetRecentAsync(pillar, NullIfBlank(query["domain"]), top, scope.ProfileKey, ct);
            return await Spec1000Http.WriteJsonAsync(req, HttpStatusCode.OK, anomalies);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GetKpiAnomalies failed unexpectedly");
            return await Spec1000Http.WriteJsonAsync(req, HttpStatusCode.InternalServerError, new { error = "Unexpected server error." });
        }
    }

    [Function("GetKpiBuckets")]
    public async Task<HttpResponseData> GetBucketsAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "health/kpis/{id:int}/buckets")] HttpRequestData req,
        int id,
        CancellationToken ct)
    {
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        if (!Spec1000Http.TryReadIntInRange(query["hours"], DefaultBucketHours, 1, MaxBucketHours, out var hours))
        {
            return await Spec1000Http.BadRequestAsync(req, [$"hours must be an integer between 1 and {MaxBucketHours}."]);
        }

        try
        {
            var scope = await _profiles.ResolveReadAsync(query["profileKey"], ct);
            if (!scope.IsValid) return await Spec1000Http.BadRequestAsync(req, [scope.Error!]);

            var definition = await definitionStore.GetAsync(id, ct);
            if (definition is null)
            {
                return await Spec1000Http.WriteJsonAsync(req, HttpStatusCode.NotFound, new { error = $"KPI definition '{id}' not found." });
            }

            // Contract v6 §5: another company's KPI does not exist for this one.
            if (!new ProfileReadScope(scope.ProfileKey).Includes(definition.ProfileKey))
            {
                return await Spec1000Http.WriteJsonAsync(req, HttpStatusCode.NotFound, new { error = $"KPI definition '{id}' not found for profile '{scope.ProfileKey}'." });
            }

            var now = time.GetUtcNow();
            var buckets = await bucketStore.GetRangeAsync(id, now.AddHours(-hours), now, ct);
            return await Spec1000Http.WriteJsonAsync(req, HttpStatusCode.OK, buckets);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GetKpiBuckets failed unexpectedly for KPI {Id}", id);
            return await Spec1000Http.WriteJsonAsync(req, HttpStatusCode.InternalServerError, new { error = "Unexpected server error." });
        }
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
