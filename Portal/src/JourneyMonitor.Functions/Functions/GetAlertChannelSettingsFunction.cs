using System.Net;
using System.Text.Json;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Core.UseCases;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Functions.Functions;

/// <summary>GET /api/settings/notifications — the Settings tab's "Notifications" section. Never returns a real secret, only whether one is configured (see AlertChannelSettingsService). Gated by X-Internal-Api-Secret (unlike most Settings reads) because the response still carries recipient email addresses plus SMTP host/username/sender metadata.</summary>
public sealed class GetAlertChannelSettingsFunction(AlertChannelSettingsService settingsService, IOptions<MonitoringOptions> monitoringOptions, ILogger<GetAlertChannelSettingsFunction> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Function("GetAlertChannelSettings")]
    public async Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "settings/notifications")] HttpRequestData req,
        CancellationToken ct)
    {
        var requiredSecret = monitoringOptions.Value.InternalApiSecret;
        if (!SettingsWriteSecretGuard.HasMatchingSecret(req, requiredSecret, SettingsWriteSecretGuard.InternalApiSecretHeaderName))
        {
            return await WriteJsonAsync(req, HttpStatusCode.Unauthorized, new { error = "Missing or invalid X-Internal-Api-Secret header. Monitoring:InternalApiSecret must be configured for this endpoint to accept requests." });
        }

        try
        {
            var settings = await settingsService.GetAsync(ct);
            return await WriteJsonAsync(req, HttpStatusCode.OK, settings);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GetAlertChannelSettings failed");
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
