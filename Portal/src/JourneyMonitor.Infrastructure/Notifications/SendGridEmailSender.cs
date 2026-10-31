using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using JourneyMonitor.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace JourneyMonitor.Infrastructure.Notifications;

/// <summary>
/// Calls the SendGrid REST API (https://api.sendgrid.com/v3/mail/send) directly with an API key,
/// same "no SDK, call the HTTP API directly" pattern already used by GitHubCopilotLlmClient -
/// avoids an extra NuGet dependency for a single POST call.
/// </summary>
public sealed class SendGridEmailSender(HttpClient http, IAlertChannelSettingsStore settingsStore, ILogger<SendGridEmailSender> logger) : IEmailSender
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public bool IsEnabled(AlertChannelSetting settings) =>
        settings.SendGridEnabled && !string.IsNullOrWhiteSpace(settings.SendGridApiKey) && !string.IsNullOrWhiteSpace(settings.SendGridFromAddress);

    public async Task<(bool Success, string? Error)> SendAsync(IReadOnlyList<string> recipients, string subject, string body, CancellationToken ct = default)
    {
        var settings = await settingsStore.GetAsync(ct);
        if (settings is null || !settings.SendGridEnabled || string.IsNullOrWhiteSpace(settings.SendGridApiKey) || string.IsNullOrWhiteSpace(settings.SendGridFromAddress))
        {
            return (false, "SendGrid is not enabled/configured.");
        }

        try
        {
            var payload = new
            {
                personalizations = new[] { new { to = recipients.Select(r => new { email = r }).ToArray() } },
                from = new { email = settings.SendGridFromAddress },
                subject,
                content = new[] { new { type = "text/plain", value = body } },
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.sendgrid.com/v3/mail/send");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.SendGridApiKey);
            request.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");

            using var response = await http.SendAsync(request, ct);
            if (response.IsSuccessStatusCode) return (true, null);

            var errorBody = await response.Content.ReadAsStringAsync(ct);
            logger.LogWarning("[SendGridEmailSender] send failed: {Status} {Body}", response.StatusCode, errorBody);
            return (false, $"SendGrid returned {(int)response.StatusCode}.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // Genuine caller cancellation - an HttpClient timeout (which surfaces as OperationCanceledException with ct NOT canceled) falls through below instead.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[SendGridEmailSender] send failed.");
            return (false, ex.Message);
        }
    }
}
