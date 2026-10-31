using System.Net;
using System.Text.Json;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Core.UseCases;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Functions.Functions;

/// <summary>GET /api/settings/notifications/audit — change history for monitoring.AlertChannelSetting (never includes secret values). Gated by X-Internal-Api-Secret - same reasoning as GetAlertChannelSettingsFunction.</summary>
public sealed class GetAlertChannelSettingsAuditFunction(AlertChannelSettingsService settingsService, IOptions<MonitoringOptions> monitoringOptions, ILogger<GetAlertChannelSettingsAuditFunction> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Function("GetAlertChannelSettingsAudit")]
    public async Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "settings/notifications/audit")] HttpRequestData req,
        CancellationToken ct)
    {
        var requiredSecret = monitoringOptions.Value.InternalApiSecret;
        if (!SettingsWriteSecretGuard.HasMatchingSecret(req, requiredSecret, SettingsWriteSecretGuard.InternalApiSecretHeaderName))
        {
            return await WriteJsonAsync(req, HttpStatusCode.Unauthorized, new { error = "Missing or invalid X-Internal-Api-Secret header. Monitoring:InternalApiSecret must be configured for this endpoint to accept requests." });
        }

        try
        {
            var audit = await settingsService.GetAuditAsync(ct);
            return await WriteJsonAsync(req, HttpStatusCode.OK, audit);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GetAlertChannelSettingsAudit failed");
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
