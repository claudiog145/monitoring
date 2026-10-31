using System.Net;
using System.Text.Json;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Core.UseCases;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Functions.Functions;

public sealed class GetDashboardMetricsFunction(
    GetDashboardMetricsService getDashboardMetricsService,
    IOptions<JourneyStoreOptions> storeOptions,
    ILogger<GetDashboardMetricsFunction> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Function("GetDashboardMetrics")]
    public async Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "analytics/metrics")] HttpRequestData req,
        CancellationToken cancellationToken)
    {
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        if (!storeOptions.Value.IsConfigured)
        {
            var unavailable = req.CreateResponse(HttpStatusCode.ServiceUnavailable);
            unavailable.Headers.Add("Content-Type", "application/json; charset=utf-8");
            await unavailable.WriteStringAsync(JsonSerializer.Serialize(new
            {
                source = "error",
                error = $"{JourneyStoreOptions.SectionName}:ConnectionString is not configured.",
            }, JsonOptions));
            return unavailable;
        }

        try
        {
            var (statusCode, result) = await getDashboardMetricsService.GetAsync(
                query["domain"], query["chart"], query["win"], query["env"], query["club"], cancellationToken);
            var response = req.CreateResponse((HttpStatusCode)statusCode);
            response.Headers.Add("Content-Type", "application/json; charset=utf-8");
            response.Headers.Add("Cache-Control", "no-store");
            await response.WriteStringAsync(JsonSerializer.Serialize(result, JsonOptions));
            return response;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GetDashboardMetrics failed");
            var response = req.CreateResponse(HttpStatusCode.InternalServerError);
            response.Headers.Add("Content-Type", "application/json; charset=utf-8");
            await response.WriteStringAsync(JsonSerializer.Serialize(new { source = "error", error = "Unexpected server error." }, JsonOptions));
            return response;
        }
    }
}
