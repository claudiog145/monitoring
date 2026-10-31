using System.Text;
using System.Text.Json;
using JourneyMonitor.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace JourneyMonitor.Infrastructure.Notifications;

/// <summary>
/// Posts to a Microsoft Teams Incoming Webhook (the channel mechanism decided in the
/// monitoring-maturity-implementation-plan) using the legacy Office 365 Connector "MessageCard"
/// JSON schema - still the format Incoming Webhook connectors accept, no SDK needed for a single
/// POST. Only notifies critical-severity requests; anything lower stays dashboard-only, which is
/// how the previous "page"-only behaviour survives the move to a source-agnostic contract.
/// </summary>
public sealed class TeamsAlertNotifier(HttpClient http, IAlertChannelSettingsStore settingsStore, ILogger<TeamsAlertNotifier> logger) : INotificationChannel
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string Channel => NotificationChannel.Teams;

    public async Task<(bool Delivered, string? Provider, string? Error)> SendAsync(NotificationRequest request, CancellationToken ct = default)
    {
        if (request.Severity != NotificationSeverity.Critical) return (false, null, null);

        AlertChannelSetting? settings;
        try
        {
            settings = await settingsStore.GetAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[TeamsAlertNotifier] could not load channel settings - skipping Teams alert for {SourceType} {SourceId}.", request.SourceType, request.SourceId);
            return (false, null, ex.Message);
        }

        if (settings is null || !settings.TeamsEnabled || string.IsNullOrWhiteSpace(settings.TeamsWebhookUrl)) return (false, null, null);

        var payload = new
        {
            @type = "MessageCard",
            @context = "http://schema.org/extensions",
            summary = request.Subject,
            themeColor = "D93025",
            title = request.Subject,
            text = request.Body.Replace("\n", "\n\n"),
        };

        try
        {
            using var content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");
            using var response = await http.PostAsync(settings.TeamsWebhookUrl, content, ct);
            if (response.IsSuccessStatusCode)
            {
                logger.LogInformation("[TeamsAlertNotifier] {SourceType} {SourceId} posted to Teams.", request.SourceType, request.SourceId);
                return (true, Channel, null);
            }

            logger.LogWarning("[TeamsAlertNotifier] Teams webhook returned {Status} for {SourceType} {SourceId}.", response.StatusCode, request.SourceType, request.SourceId);
            return (false, null, $"Teams webhook returned {(int)response.StatusCode}.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // Genuine caller cancellation - an HttpClient timeout (which surfaces as OperationCanceledException with ct NOT canceled) falls through below instead.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[TeamsAlertNotifier] send failed for {SourceType} {SourceId}.", request.SourceType, request.SourceId);
            return (false, null, ex.Message);
        }
    }
}
