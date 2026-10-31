using JourneyMonitor.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace JourneyMonitor.Infrastructure.Notifications;

/// <summary>
/// Fans out to every enabled email provider in a fixed priority order (Smtp, then Azure
/// Communication Services, then SendGrid) as a fallback chain - decision from
/// docs/product/wi-238036-threshold-research-and-wi-package-2026-09-29.md: more than one
/// provider can be configured/enabled at once, not an exclusive choice, so a provider outage
/// doesn't silently drop every alert. Only notifies critical-severity requests; anything lower
/// stays dashboard-only, which is how the previous "page"-only behaviour survives the move to a
/// source-agnostic contract.
/// </summary>
public sealed class EmailAlertNotifier(
    IAlertChannelSettingsStore settingsStore,
    IEnumerable<IEmailSender> senders,
    ILogger<EmailAlertNotifier> logger) : INotificationChannel
{
    public string Channel => NotificationChannel.Email;

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
            logger.LogWarning(ex, "[EmailAlertNotifier] could not load channel settings - skipping email for {SourceType} {SourceId}.", request.SourceType, request.SourceId);
            return (false, null, ex.Message);
        }

        if (settings is null || !settings.EmailEnabled) return (false, null, null);

        var recipients = AlertChannelSettingsRecipients.Parse(settings.EmailRecipientsCsv);
        if (recipients.Count == 0) return (false, null, null);

        // Only attempt a provider the operator actually enabled/configured - otherwise every
        // disabled provider shows up in the audit history as a "failed" send that was never
        // really tried.
        var enabledSenders = senders.Where(s => s.IsEnabled(settings)).ToList();
        if (enabledSenders.Count == 0)
        {
            logger.LogWarning("[EmailAlertNotifier] email is enabled but no provider is configured - no email sent for {SourceType} {SourceId}.", request.SourceType, request.SourceId);
            return (false, null, "Email is enabled but no provider is configured.");
        }

        // The audit log now records one row per channel rather than per provider, so the errors of
        // the whole fallback chain are aggregated here instead of being lost.
        var failures = new List<string>();

        foreach (var sender in enabledSenders)
        {
            var providerName = sender.GetType().Name;
            var (success, error) = await sender.SendAsync(recipients, request.Subject, request.Body, ct);
            if (success)
            {
                logger.LogInformation("[EmailAlertNotifier] {SourceType} {SourceId} emailed via {Sender}.", request.SourceType, request.SourceId, providerName);
                return (true, providerName, null);
            }
            logger.LogWarning("[EmailAlertNotifier] {Sender} failed for {SourceType} {SourceId}: {Error}", providerName, request.SourceType, request.SourceId, error);
            failures.Add($"{providerName}: {error ?? "unknown error"}");
        }

        logger.LogWarning("[EmailAlertNotifier] every enabled email provider failed for {SourceType} {SourceId} - no email sent.", request.SourceType, request.SourceId);
        return (false, null, string.Join("; ", failures));
    }
}

internal static class AlertChannelSettingsRecipients
{
    internal static IReadOnlyList<string> Parse(string? csv) =>
        string.IsNullOrWhiteSpace(csv)
            ? []
            : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
