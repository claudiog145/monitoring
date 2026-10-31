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
/// PUT /api/settings/slo — upserts one (domain, stage, signalType) SLO target. Same fail-closed
/// X-Settings-Write-Secret gate as <see cref="UpsertLatencySpikeSettingFunction"/> - see that
/// class's doc comment for the full reasoning.
/// </summary>
public sealed class UpsertSloDefinitionFunction(
    SloDefinitionsService settingsService,
    IOptions<MonitoringOptions> monitoringOptions,
    ILogger<UpsertSloDefinitionFunction> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Function("UpsertSloDefinition")]
    public async Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "settings/slo")] HttpRequestData req,
        CancellationToken ct)
    {
        var requiredSecret = monitoringOptions.Value.SettingsWriteSecret;
        if (!SettingsWriteSecretGuard.HasMatchingWriteSecret(req, requiredSecret))
        {
            return await WriteJsonAsync(req, HttpStatusCode.Unauthorized, new { error = "Missing or invalid X-Settings-Write-Secret header. Monitoring:SettingsWriteSecret must be configured for this endpoint to accept writes." });
        }

        try
        {
            var request = await JsonSerializer.DeserializeAsync<UpsertRequestBody>(req.Body, JsonOptions, ct);
            if (request is null || string.IsNullOrWhiteSpace(request.Domain) || string.IsNullOrWhiteSpace(request.SignalType))
            {
                return await WriteJsonAsync(req, HttpStatusCode.BadRequest, new { error = "Request body must include domain and signalType." });
            }

            var (statusCode, result) = await settingsService.UpsertAsync(
                request.Domain,
                request.Stage,
                request.SignalType,
                request.TargetThresholdMs,
                request.TargetPercentage ?? 99.0m,
                request.WindowDays ?? 28,
                request.Enabled ?? true,
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
            logger.LogError(ex, "UpsertSloDefinition failed");
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
        [JsonPropertyName("domain")]
        public string? Domain { get; set; }

        [JsonPropertyName("stage")]
        public string? Stage { get; set; }

        [JsonPropertyName("signalType")]
        public string? SignalType { get; set; }

        [JsonPropertyName("targetThresholdMs")]
        public int? TargetThresholdMs { get; set; }

        [JsonPropertyName("targetPercentage")]
        public decimal? TargetPercentage { get; set; }

        [JsonPropertyName("windowDays")]
        public int? WindowDays { get; set; }

        [JsonPropertyName("enabled")]
        public bool? Enabled { get; set; }

        [JsonPropertyName("changedBy")]
        public string? ChangedBy { get; set; }
    }
}
