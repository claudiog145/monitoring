namespace JourneyMonitor.Core.Abstractions;

/// <summary>
/// Effective notification channel configuration, decrypted for internal use by the channels
/// (<see cref="INotificationChannel"/> implementations) - never returned to the Settings tab as-is, see
/// <c>AlertChannelSettingsService</c> for the masked view. More than one channel/provider can be
/// enabled at once by design (Claudio's decision, 2026-09-30): Email supports 3 selectable
/// providers tried in order (Smtp, then Azure Communication Services, then SendGrid) as a
/// fallback chain, not an exclusive choice.
/// </summary>
public sealed record AlertChannelSetting(
    bool EmailEnabled,
    string? EmailRecipientsCsv,
    bool SmtpEnabled,
    string? SmtpHost,
    int? SmtpPort,
    string? SmtpUsername,
    string? SmtpPassword,
    bool SmtpUseSsl,
    string? SmtpFromAddress,
    bool AcsEnabled,
    string? AcsConnectionString,
    string? AcsFromAddress,
    bool SendGridEnabled,
    string? SendGridApiKey,
    string? SendGridFromAddress,
    bool TeamsEnabled,
    string? TeamsWebhookUrl,
    DateTimeOffset? UpdatedAt,
    string? UpdatedBy);

/// <summary>
/// One historical change. Deliberately carries none of the 4 secrets' values (past or present) -
/// only whether each one changed - same reasoning as <c>LlmClientSettingAuditEntry</c>.
/// </summary>
public sealed record AlertChannelSettingAuditEntry(
    bool EmailEnabled,
    bool SmtpEnabled,
    bool SmtpPasswordChanged,
    bool AcsEnabled,
    bool AcsConnectionStringChanged,
    bool SendGridEnabled,
    bool SendGridApiKeyChanged,
    bool TeamsEnabled,
    bool TeamsWebhookChanged,
    DateTimeOffset ChangedAt,
    string ChangedBy);

/// <summary>
/// Request shape for <see cref="IAlertChannelSettingsStore.UpsertAsync"/>. Non-secret fields
/// (enabled flags, host/port/username/from-address, the CSV recipient list) are always applied as
/// given - only the 4 secret fields (<see cref="SmtpPassword"/>/<see cref="AcsConnectionString"/>/
/// <see cref="SendGridApiKey"/>/<see cref="TeamsWebhookUrl"/>) follow the
/// null-means-unchanged/empty-string-means-clear convention already used by
/// <see cref="ILlmClientSettingsStore"/>.
/// </summary>
public sealed record AlertChannelSettingsUpdateRequest(
    bool EmailEnabled,
    string? EmailRecipientsCsv,
    bool SmtpEnabled,
    string? SmtpHost,
    int? SmtpPort,
    string? SmtpUsername,
    string? SmtpPassword,
    bool SmtpUseSsl,
    string? SmtpFromAddress,
    bool AcsEnabled,
    string? AcsConnectionString,
    string? AcsFromAddress,
    bool SendGridEnabled,
    string? SendGridApiKey,
    string? SendGridFromAddress,
    bool TeamsEnabled,
    string? TeamsWebhookUrl);

/// <summary>
/// Durable, single-row notification channel store (see <c>monitoring.AlertChannelSetting</c> /
/// <c>monitoring.AlertChannelSettingAudit</c>). Every secret is encrypted at rest with
/// <see cref="Microsoft.AspNetCore.DataProtection.IDataProtector"/> before reaching SQL - see
/// SqlAlertChannelSettingsStore - same pattern as <see cref="ILlmClientSettingsStore"/>.
/// </summary>
public interface IAlertChannelSettingsStore
{
    /// <summary>Null when no row has ever been saved (every channel effectively disabled).</summary>
    Task<AlertChannelSetting?> GetAsync(CancellationToken cancellationToken);

    Task UpsertAsync(AlertChannelSettingsUpdateRequest request, string changedBy, CancellationToken cancellationToken);

    Task<IReadOnlyList<AlertChannelSettingAuditEntry>> GetAuditAsync(CancellationToken cancellationToken);
}
