using Azure;
using Azure.Communication.Email;
using JourneyMonitor.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace JourneyMonitor.Infrastructure.Notifications;

/// <summary>
/// Azure Communication Services Email - uses the official SDK (unlike Smtp/SendGrid) because its
/// auth scheme (HMAC-signed requests derived from the connection string's access key) is
/// impractical and risky to reimplement by hand; the SDK already handles it correctly.
/// </summary>
public sealed class AcsEmailSender(IAlertChannelSettingsStore settingsStore, ILogger<AcsEmailSender> logger) : IEmailSender
{
    public bool IsEnabled(AlertChannelSetting settings) =>
        settings.AcsEnabled && !string.IsNullOrWhiteSpace(settings.AcsConnectionString) && !string.IsNullOrWhiteSpace(settings.AcsFromAddress);

    public async Task<(bool Success, string? Error)> SendAsync(IReadOnlyList<string> recipients, string subject, string body, CancellationToken ct = default)
    {
        var settings = await settingsStore.GetAsync(ct);
        if (settings is null || !settings.AcsEnabled || string.IsNullOrWhiteSpace(settings.AcsConnectionString) || string.IsNullOrWhiteSpace(settings.AcsFromAddress))
        {
            return (false, "Azure Communication Services Email is not enabled/configured.");
        }

        try
        {
            var client = new EmailClient(settings.AcsConnectionString);
            var message = new EmailMessage(
                settings.AcsFromAddress,
                new EmailRecipients(recipients.Select(r => new EmailAddress(r))),
                new EmailContent(subject) { PlainText = body });

            var operation = await client.SendAsync(WaitUntil.Completed, message, ct);
            return operation.HasCompleted && operation.Value.Status == EmailSendStatus.Succeeded
                ? (true, (string?)null)
                : (false, $"ACS Email status: {operation.Value?.Status}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // Genuine caller cancellation - an SDK-internal timeout (which can also surface as OperationCanceledException with ct NOT canceled) falls through below instead.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[AcsEmailSender] send failed.");
            return (false, ex.Message);
        }
    }
}
