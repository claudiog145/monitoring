using System.Text.RegularExpressions;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Options;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Core.UseCases;

/// <summary>Masked view for the Settings tab - never carries a real secret, only whether one is configured.</summary>
public sealed record AlertChannelSettingView(
    bool EmailEnabled,
    string? EmailRecipientsCsv,
    bool SmtpEnabled,
    string? SmtpHost,
    int? SmtpPort,
    string? SmtpUsername,
    bool SmtpHasPassword,
    bool SmtpUseSsl,
    string? SmtpFromAddress,
    bool AcsEnabled,
    bool AcsHasConnectionString,
    string? AcsFromAddress,
    bool SendGridEnabled,
    bool SendGridHasApiKey,
    string? SendGridFromAddress,
    bool TeamsEnabled,
    bool TeamsHasWebhook,
    DateTimeOffset? UpdatedAt,
    string? UpdatedBy);

/// <summary>
/// Backs the Settings tab's "Notifications" section - toggle and configure the Email (3
/// selectable providers, tried as a fallback chain) and Teams webhook channels used by
/// <see cref="INotificationChannel"/> implementations when an SLO burn-rate Finding reaches "page"
/// severity. Any portal user may change it (no authorization check, same product decision as
/// every other Settings section here) - who/when is recorded via
/// <see cref="IAlertChannelSettingsStore.GetAuditAsync"/>, never the secret values.
/// </summary>
public sealed class AlertChannelSettingsService(IAlertChannelSettingsStore store, IOptions<JourneyStoreOptions> storeOptions)
{
    private static readonly Regex EmailPattern = new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled);

    public async Task<AlertChannelSettingView> GetAsync(CancellationToken ct)
    {
        var stored = storeOptions.Value.IsConfigured ? await TryGetStoredAsync(ct) : null;

        return new AlertChannelSettingView(
            EmailEnabled: stored?.EmailEnabled ?? false,
            EmailRecipientsCsv: stored?.EmailRecipientsCsv,
            SmtpEnabled: stored?.SmtpEnabled ?? false,
            SmtpHost: stored?.SmtpHost,
            SmtpPort: stored?.SmtpPort,
            SmtpUsername: stored?.SmtpUsername,
            SmtpHasPassword: !string.IsNullOrEmpty(stored?.SmtpPassword),
            SmtpUseSsl: stored?.SmtpUseSsl ?? true,
            SmtpFromAddress: stored?.SmtpFromAddress,
            AcsEnabled: stored?.AcsEnabled ?? false,
            AcsHasConnectionString: !string.IsNullOrEmpty(stored?.AcsConnectionString),
            AcsFromAddress: stored?.AcsFromAddress,
            SendGridEnabled: stored?.SendGridEnabled ?? false,
            SendGridHasApiKey: !string.IsNullOrEmpty(stored?.SendGridApiKey),
            SendGridFromAddress: stored?.SendGridFromAddress,
            TeamsEnabled: stored?.TeamsEnabled ?? false,
            TeamsHasWebhook: !string.IsNullOrEmpty(stored?.TeamsWebhookUrl),
            UpdatedAt: stored?.UpdatedAt,
            UpdatedBy: stored?.UpdatedBy);
    }

    private async Task<AlertChannelSetting?> TryGetStoredAsync(CancellationToken ct)
    {
        try
        {
            return await store.GetAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// <paramref name="clearSmtpPassword"/>/<paramref name="clearAcsConnectionString"/>/
    /// <paramref name="clearSendGridApiKey"/>/<paramref name="clearTeamsWebhookUrl"/> are the only
    /// way to explicitly wipe each secret - passing null for the matching value parameter always
    /// means "leave unchanged", never "clear", so a client can never blank a secret out by
    /// omitting a field (same convention as LlmClientSettingsService.UpsertAsync).
    /// </summary>
    public async Task<(int StatusCode, object Result)> UpsertAsync(
        bool emailEnabled, string? emailRecipientsCsv,
        bool smtpEnabled, string? smtpHost, int? smtpPort, string? smtpUsername, string? smtpPassword, bool clearSmtpPassword, bool smtpUseSsl, string? smtpFromAddress,
        bool acsEnabled, string? acsConnectionString, bool clearAcsConnectionString, string? acsFromAddress,
        bool sendGridEnabled, string? sendGridApiKey, bool clearSendGridApiKey, string? sendGridFromAddress,
        bool teamsEnabled, string? teamsWebhookUrl, bool clearTeamsWebhookUrl,
        string? changedBy, CancellationToken ct)
    {
        if (emailEnabled)
        {
            var recipients = ParseRecipients(emailRecipientsCsv);
            if (recipients.Count == 0)
            {
                return (400, new { error = "emailRecipientsCsv must contain at least one valid address (comma-separated) when emailEnabled is true." });
            }
            var invalid = recipients.Where(r => !EmailPattern.IsMatch(r)).ToList();
            if (invalid.Count > 0)
            {
                return (400, new { error = $"Invalid email address(es): {string.Join(", ", invalid)}." });
            }
            if (!smtpEnabled && !acsEnabled && !sendGridEnabled)
            {
                return (400, new { error = "At least one email provider (SMTP, Azure Communication Services, or SendGrid) must be enabled when emailEnabled is true." });
            }
        }
        if (smtpPort is < 1 or > 65535)
        {
            return (400, new { error = "smtpPort must be between 1 and 65535." });
        }
        if (teamsEnabled && string.IsNullOrWhiteSpace(teamsWebhookUrl) && clearTeamsWebhookUrl)
        {
            return (400, new { error = "teamsEnabled cannot be true while clearing the webhook URL - disable it first." });
        }
        if (!string.IsNullOrWhiteSpace(teamsWebhookUrl) && !teamsWebhookUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return (400, new { error = "teamsWebhookUrl must start with https://." });
        }

        var who = string.IsNullOrWhiteSpace(changedBy) ? "anonymous" : changedBy.Trim();

        var request = new AlertChannelSettingsUpdateRequest(
            emailEnabled, string.IsNullOrWhiteSpace(emailRecipientsCsv) ? null : emailRecipientsCsv.Trim(),
            smtpEnabled, string.IsNullOrWhiteSpace(smtpHost) ? null : smtpHost.Trim(), smtpPort, string.IsNullOrWhiteSpace(smtpUsername) ? null : smtpUsername.Trim(),
            clearSmtpPassword ? "" : smtpPassword, smtpUseSsl, string.IsNullOrWhiteSpace(smtpFromAddress) ? null : smtpFromAddress.Trim(),
            acsEnabled, clearAcsConnectionString ? "" : acsConnectionString, string.IsNullOrWhiteSpace(acsFromAddress) ? null : acsFromAddress.Trim(),
            sendGridEnabled, clearSendGridApiKey ? "" : sendGridApiKey, string.IsNullOrWhiteSpace(sendGridFromAddress) ? null : sendGridFromAddress.Trim(),
            teamsEnabled, clearTeamsWebhookUrl ? "" : teamsWebhookUrl);

        await store.UpsertAsync(request, who, ct);

        return (200, await GetAsync(ct));
    }

    public async Task<IReadOnlyList<AlertChannelSettingAuditEntry>> GetAuditAsync(CancellationToken ct)
    {
        if (!storeOptions.Value.IsConfigured) return [];
        try
        {
            return await store.GetAuditAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return [];
        }
    }

    internal static IReadOnlyList<string> ParseRecipients(string? csv) =>
        string.IsNullOrWhiteSpace(csv)
            ? []
            : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
