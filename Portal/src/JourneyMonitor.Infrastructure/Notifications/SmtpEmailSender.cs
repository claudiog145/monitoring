using System.Net.Mail;
using JourneyMonitor.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace JourneyMonitor.Infrastructure.Notifications;

/// <summary>
/// Plain SMTP relay (Office365/Exchange/etc.) - no extra NuGet dependency, uses the .NET
/// built-in <see cref="SmtpClient"/>. Resolves its configuration fresh on every send (not cached
/// at construction) so a change saved via the Settings tab takes effect without restarting the
/// process, same pattern as GitHubCopilotLlmClient.
/// </summary>
public sealed class SmtpEmailSender(IAlertChannelSettingsStore settingsStore, ILogger<SmtpEmailSender> logger) : IEmailSender
{
    public bool IsEnabled(AlertChannelSetting settings) =>
        settings.SmtpEnabled && !string.IsNullOrWhiteSpace(settings.SmtpHost) && !string.IsNullOrWhiteSpace(settings.SmtpFromAddress);

    public async Task<(bool Success, string? Error)> SendAsync(IReadOnlyList<string> recipients, string subject, string body, CancellationToken ct = default)
    {
        var settings = await settingsStore.GetAsync(ct);
        if (settings is null || !settings.SmtpEnabled || string.IsNullOrWhiteSpace(settings.SmtpHost) || string.IsNullOrWhiteSpace(settings.SmtpFromAddress))
        {
            return (false, "SMTP is not enabled/configured.");
        }

        try
        {
            using var client = new SmtpClient(settings.SmtpHost, settings.SmtpPort ?? 587)
            {
                EnableSsl = settings.SmtpUseSsl,
            };
            if (!string.IsNullOrWhiteSpace(settings.SmtpUsername))
            {
                client.Credentials = new System.Net.NetworkCredential(settings.SmtpUsername, settings.SmtpPassword ?? "");
            }

            using var message = new MailMessage
            {
                From = new MailAddress(settings.SmtpFromAddress),
                Subject = subject,
                Body = body,
            };
            foreach (var recipient in recipients) message.To.Add(recipient);

            await client.SendMailAsync(message, ct);
            return (true, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // Genuine caller cancellation - a transport timeout (TaskCanceledException with ct NOT canceled) falls through to the catch-all below instead.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[SmtpEmailSender] send failed via {Host}.", settings.SmtpHost);
            return (false, ex.Message);
        }
    }
}
