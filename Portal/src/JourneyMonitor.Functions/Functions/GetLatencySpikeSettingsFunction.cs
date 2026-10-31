using System.Net;
using System.Text.Json;
using JourneyMonitor.Core.UseCases;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace JourneyMonitor.Functions.Functions;

/// <summary>
/// GET /api/settings/latency — the Settings tab's data source: every domain's Latency Spike
/// Monkey threshold, both the raw override (null if never set) and the effective value actually
/// used by the detectors (see <see cref="LatencySpikeSettingsService"/>).
/// </summary>
public sealed class GetLatencySpikeSettingsFunction(
    LatencySpikeSettingsService settingsService,
    ILogger<GetLatencySpikeSettingsFunction> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Function("GetLatencySpikeSettings")]
    public async Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "settings/latency")] HttpRequestData req,
        CancellationToken ct)
    {
        try
        {
            var settings = await settingsService.GetAllAsync(ct);
            return await WriteJsonAsync(req, HttpStatusCode.OK, settings);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GetLatencySpikeSettings failed");
            return await WriteJsonAsync(req, HttpStatusCode.InternalServerError, new { error = "Unexpected server error." });
        }
    }

    private static async Task<HttpResponseData> WriteJsonAsync(HttpRequestData req, HttpStatusCode statusCode, object body)
    {
        var response = req.CreateResponse(statusCode);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        response.Headers.Add("Cache-Control", "no-store");
        await response.WriteStringAsync(JsonSerializer.Serialize(body, JsonOptions));
        return response;
    }
}
