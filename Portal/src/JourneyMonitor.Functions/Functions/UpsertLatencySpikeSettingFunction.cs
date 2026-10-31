using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Core.UseCases;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Functions.Functions;

/// <summary>
/// PUT /api/settings/latency/{domain} — any portal user can change a domain's Latency Spike
/// Monkey threshold/occurrence/window override; every change is recorded in
/// monitoring.LatencySpikeSettingAudit via <see cref="LatencySpikeSettingsService"/>. No
/// authorization check on WHO can change a setting by design (see docs/sessions - "cualquier
/// usuario" was an explicit product decision), but ChangedBy is always recorded.
///
/// <see cref="AuthorizationLevel.Anonymous"/> here matches every other endpoint in this Functions
/// app (see Program.cs's "Extension point: in Azure, attach a Managed Identity-issued token...
/// instead of calling the Function anonymously" comment) - real per-request auth for the whole
/// API surface is out of scope here. What this endpoint does add, because it is the one mutating
/// write to an audit trail: the caller MUST present <see cref="MonitoringOptions.SettingsWriteSecret"/>
/// via the X-Settings-Write-Secret header (the Web proxy always attaches it, see
/// JourneyMonitorApiClient) - unlike every other optional secret in this repo, this one fails
/// CLOSED, not open: if it is not configured, every write is rejected rather than left wide open.
/// docker-compose sets a local-only default for both services so `docker compose up` keeps
/// working out of the box; anyone running the Function directly (func start/Visual Studio, no
/// docker-compose) must configure it explicitly before this endpoint will accept any write.
/// </summary>
public sealed class UpsertLatencySpikeSettingFunction(
    LatencySpikeSettingsService settingsService,
    IOptions<MonitoringOptions> monitoringOptions,
    ILogger<UpsertLatencySpikeSettingFunction> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Function("UpsertLatencySpikeSetting")]
    public async Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "settings/latency/{domain}")] HttpRequestData req,
        string domain,
        CancellationToken ct)
    {
        var requiredSecret = monitoringOptions.Value.SettingsWriteSecret;
        if (!SettingsWriteSecretGuard.HasMatchingWriteSecret(req, requiredSecret))
        {
            // Fail closed: a missing/blank requiredSecret must reject every write, not skip the
            // check - see the class doc comment.
            return await WriteJsonAsync(req, HttpStatusCode.Unauthorized, new { error = "Missing or invalid X-Settings-Write-Secret header. Monitoring:SettingsWriteSecret must be configured for this endpoint to accept writes." });
        }

        try
        {
            var request = await JsonSerializer.DeserializeAsync<UpsertRequestBody>(req.Body, JsonOptions, ct);
            if (request is null)
            {
                return await WriteJsonAsync(req, HttpStatusCode.BadRequest, new { error = "Request body must be valid JSON." });
            }

            var (statusCode, result) = await settingsService.UpsertAsync(
                domain,
                request.SpikeThresholdMs,
                request.SpikeOccurrenceThreshold,
                request.SpikeWindowMinutes,
                request.ChangedBy,
                ct);

            return await WriteJsonAsync(req, (HttpStatusCode)statusCode, result);
        }
        catch (JsonException)
        {
            return await WriteJsonAsync(req, HttpStatusCode.BadRequest, new { error = "Request body must be valid JSON." });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "UpsertLatencySpikeSetting failed for domain {Domain}", domain);
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

    private sealed class UpsertRequestBody
    {
        [JsonPropertyName("spikeThresholdMs")]
        public int? SpikeThresholdMs { get; set; }

        [JsonPropertyName("spikeOccurrenceThreshold")]
        public int? SpikeOccurrenceThreshold { get; set; }

        [JsonPropertyName("spikeWindowMinutes")]
        public int? SpikeWindowMinutes { get; set; }

        [JsonPropertyName("changedBy")]
        public string? ChangedBy { get; set; }
    }
}
