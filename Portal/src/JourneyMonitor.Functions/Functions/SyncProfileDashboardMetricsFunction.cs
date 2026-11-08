using System.Net;
using System.Text.Json;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Core.UseCases;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Functions.Functions;

/// <summary>
/// Contract v6 §4.4: every 15 minutes, P95 per step and exceptions per step of every service of every
/// enabled company land in dashboard.MetricPoint under that company's key. POST runs it on demand
/// (all companies, or <c>?profileKey=</c>).
/// </summary>
public sealed class SyncProfileDashboardMetricsFunction(
    IOptions<ProfileAnalyticsOptions> options,
    IOptions<JourneyStoreOptions> storeOptions,
    ILogger<SyncProfileDashboardMetricsFunction> logger,
    SyncProfileDashboardMetricsService? syncService = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Function("SyncProfileDashboardMetrics")]
    public async Task RunAsync([TimerTrigger("0 */15 * * * *")] TimerInfo timer, CancellationToken cancellationToken)
    {
        if (syncService is null || !options.Value.Enabled || !storeOptions.Value.IsConfigured) return;
        try
        {
            var passes = await syncService.SyncAllAsync(cancellationToken);
            logger.LogInformation(
                "[ProfileDashboardSync] {Stored}/{Total} charts stored, {Points} points",
                passes.Count(p => p.Stored), passes.Count, passes.Sum(p => p.PointCount));
            foreach (var skipped in passes.Where(p => !p.Stored).GroupBy(p => (p.ProfileKey, p.ServiceKey)))
            {
                logger.LogWarning("[ProfileDashboardSync] {Profile}/{Service} skipped: {Reason}",
                    skipped.Key.ProfileKey, skipped.Key.ServiceKey, skipped.First().SkipReason);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "[ProfileDashboardSync] run failed");
        }
    }

    [Function("SyncProfileDashboardMetricsNow")]
    public async Task<HttpResponseData> RunNowAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "analytics/metrics/sync")] HttpRequestData req,
        CancellationToken cancellationToken)
    {
        if (syncService is null || !storeOptions.Value.IsConfigured)
        {
            return await WriteAsync(req, HttpStatusCode.ServiceUnavailable,
                new { error = $"{JourneyStoreOptions.SectionName}:ConnectionString is not configured." });
        }

        var profileKey = System.Web.HttpUtility.ParseQueryString(req.Url.Query)["profileKey"];
        try
        {
            var passes = string.IsNullOrWhiteSpace(profileKey)
                ? await syncService.SyncAllAsync(cancellationToken)
                : await syncService.SyncProfileAsync(profileKey.Trim(), cancellationToken);
            return await WriteAsync(req, HttpStatusCode.OK, new { passes });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "SyncProfileDashboardMetricsNow failed");
            return await WriteAsync(req, HttpStatusCode.InternalServerError, new { error = "Unexpected server error." });
        }
    }

    private static async Task<HttpResponseData> WriteAsync(HttpRequestData req, HttpStatusCode status, object payload)
    {
        var response = req.CreateResponse(status);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        response.Headers.Add("Cache-Control", "no-store");
        await response.WriteStringAsync(JsonSerializer.Serialize(payload, JsonOptions));
        return response;
    }
}
