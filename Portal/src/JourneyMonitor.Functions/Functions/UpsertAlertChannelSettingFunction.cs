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
/// PUT /api/settings/notifications — configures the Email (3 selectable providers) and Teams
/// notification channels. Same fail-closed X-Settings-Write-Secret gate as
/// <see cref="UpsertLatencySpikeSettingFunction"/> - if anything this is MORE sensitive than a
/// threshold (real credentials), never looser.
/// </summary>
public sealed class UpsertAlertChannelSettingFunction(
    AlertChannelSettingsService settingsService,
    IOptions<MonitoringOptions> monitoringOptions,
    ILogger<UpsertAlertChannelSettingFunction> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Function("UpsertAlertChannelSetting")]
    public async Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "settings/notifications")] HttpRequestData req,
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
            if (request is null)
            {
                return await WriteJsonAsync(req, HttpStatusCode.BadRequest, new { error = "Request body must be valid JSON." });
            }

            var (statusCode, result) = await settingsService.UpsertAsync(
                request.EmailEnabled, request.EmailRecipientsCsv,
                request.SmtpEnabled, request.SmtpHost, request.SmtpPort, request.SmtpUsername, request.SmtpPassword, request.ClearSmtpPassword, request.SmtpUseSsl ?? true, request.SmtpFromAddress,
                request.AcsEnabled, request.AcsConnectionString, request.ClearAcsConnectionString, request.AcsFromAddress,
                request.SendGridEnabled, request.SendGridApiKey, request.ClearSendGridApiKey, request.SendGridFromAddress,
                request.TeamsEnabled, request.TeamsWebhookUrl, request.ClearTeamsWebhookUrl,
                request.ChangedBy, ct);

            return await WriteJsonAsync(req, (HttpStatusCode)statusCode, result);
        }
        catch (JsonException)
        {
            return await WriteJsonAsync(req, HttpStatusCode.BadRequest, new { error = "Request body must be valid JSON." });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "UpsertAlertChannelSetting failed");
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
        [JsonPropertyName("emailEnabled")] public bool EmailEnabled { get; set; }
        [JsonPropertyName("emailRecipientsCsv")] public string? EmailRecipientsCsv { get; set; }

        [JsonPropertyName("smtpEnabled")] public bool SmtpEnabled { get; set; }
        [JsonPropertyName("smtpHost")] public string? SmtpHost { get; set; }
        [JsonPropertyName("smtpPort")] public int? SmtpPort { get; set; }
        [JsonPropertyName("smtpUsername")] public string? SmtpUsername { get; set; }
        [JsonPropertyName("smtpPassword")] public string? SmtpPassword { get; set; }
        [JsonPropertyName("clearSmtpPassword")] public bool ClearSmtpPassword { get; set; }
        [JsonPropertyName("smtpUseSsl")] public bool? SmtpUseSsl { get; set; }
        [JsonPropertyName("smtpFromAddress")] public string? SmtpFromAddress { get; set; }

        [JsonPropertyName("acsEnabled")] public bool AcsEnabled { get; set; }
        [JsonPropertyName("acsConnectionString")] public string? AcsConnectionString { get; set; }
        [JsonPropertyName("clearAcsConnectionString")] public bool ClearAcsConnectionString { get; set; }
        [JsonPropertyName("acsFromAddress")] public string? AcsFromAddress { get; set; }

        [JsonPropertyName("sendGridEnabled")] public bool SendGridEnabled { get; set; }
        [JsonPropertyName("sendGridApiKey")] public string? SendGridApiKey { get; set; }
        [JsonPropertyName("clearSendGridApiKey")] public bool ClearSendGridApiKey { get; set; }
        [JsonPropertyName("sendGridFromAddress")] public string? SendGridFromAddress { get; set; }

        [JsonPropertyName("teamsEnabled")] public bool TeamsEnabled { get; set; }
        [JsonPropertyName("teamsWebhookUrl")] public string? TeamsWebhookUrl { get; set; }
        [JsonPropertyName("clearTeamsWebhookUrl")] public bool ClearTeamsWebhookUrl { get; set; }

        [JsonPropertyName("changedBy")] public string? ChangedBy { get; set; }
    }
}
