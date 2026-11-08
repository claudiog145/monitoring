using System.Net;
using System.Text.Json;
using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Monitoring.Kpi;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Core.UseCases;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Functions.Functions;

/// <summary>
/// GET /api/hotels/business-health — Hotels Business Health for the Reliability Watch card.
/// Contract v6 §5: incumbent only; another company gets <c>available:false, applicable:false</c>.
/// Missing profileKey → the active company, "*" → every company.
/// </summary>
public sealed class GetHotelBusinessHealthFunction(
    GetHotelBusinessHealthService getHotelBusinessHealth,
    IOptions<MonitoringOptions> monitoringOptions,
    ILogger<GetHotelBusinessHealthFunction> logger,
    KpiProfileResolver? profileResolver = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Function("GetHotelBusinessHealth")]
    public async Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "hotels/business-health")] HttpRequestData req)
    {
        if (!SettingsWriteSecretGuard.HasMatchingSecret(req, monitoringOptions.Value.InternalApiSecret, SettingsWriteSecretGuard.InternalApiSecretHeaderName))
            return await WriteJsonAsync(req, HttpStatusCode.Unauthorized, new { error = "Missing or invalid X-Internal-Api-Secret header." });
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        try
        {
            var (scope, error) = await ProfileReadScope.ResolveAsync(profileResolver, query["profileKey"], req.FunctionContext.CancellationToken);
            if (scope is null) return await WriteJsonAsync(req, HttpStatusCode.BadRequest, new { source = "error", error });
            if (!scope.IncludesIncumbent)
            {
                return await WriteJsonAsync(req, HttpStatusCode.OK, new
                {
                    available = false,
                    applicable = false,
                    source = "not-applicable",
                    profileKey = scope.Label,
                    message = "Hotels business health only exists for the incumbent company.",
                });
            }

            var result = await getHotelBusinessHealth.GetAsync(
                query["environment"] ?? query["env"],
                query["domain"],
                query["club"],
                req.FunctionContext.CancellationToken);
            if (result is null)
                return await WriteJsonAsync(req, HttpStatusCode.OK, new { available = false, source = "unavailable" });
            return await WriteJsonAsync(req, HttpStatusCode.OK, result);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "GetHotelBusinessHealth failed unexpectedly.");
            return await WriteJsonAsync(req, HttpStatusCode.InternalServerError, new { source = "error" });
        }
    }

    private static async Task<HttpResponseData> WriteJsonAsync(
        HttpRequestData req,
        HttpStatusCode statusCode,
        object payload)
    {
        var response = req.CreateResponse(statusCode);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        response.Headers.Add("Cache-Control", "no-store");
        await response.WriteStringAsync(JsonSerializer.Serialize(payload, JsonOptions));
        return response;
    }
}
