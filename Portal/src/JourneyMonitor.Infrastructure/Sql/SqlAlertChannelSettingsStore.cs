using System.Data;
using System.Security.Cryptography;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Options;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Dapper;

namespace JourneyMonitor.Infrastructure.Sql;

/// <summary>
/// Each of the 4 secrets (SmtpPassword/AcsConnectionString/SendGridApiKey/TeamsWebhookUrl) gets
/// its own <see cref="IDataProtector"/> purpose string so rotating/compromising one does not
/// affect the others - same reasoning as <see cref="SqlLlmClientSettingsStore"/>, just applied 4
/// times over.
/// </summary>
public sealed class SqlAlertChannelSettingsStore(
    IOptions<JourneyStoreOptions> storeOptions,
    IDataProtectionProvider dataProtectionProvider,
    ILogger<SqlAlertChannelSettingsStore> logger) : IAlertChannelSettingsStore
{
    private readonly IDataProtector _smtpProtector = dataProtectionProvider.CreateProtector("JourneyMonitor.AlertChannelSetting.SmtpPassword");
    private readonly IDataProtector _acsProtector = dataProtectionProvider.CreateProtector("JourneyMonitor.AlertChannelSetting.AcsConnectionString");
    private readonly IDataProtector _sendGridProtector = dataProtectionProvider.CreateProtector("JourneyMonitor.AlertChannelSetting.SendGridApiKey");
    private readonly IDataProtector _teamsProtector = dataProtectionProvider.CreateProtector("JourneyMonitor.AlertChannelSetting.TeamsWebhookUrl");

    public async Task<AlertChannelSetting?> GetAsync(CancellationToken cancellationToken)
    {
        await using var connection = OpenConnection();
        var row = await connection.QuerySingleOrDefaultAsync<SettingRow>(new CommandDefinition(
            "monitoring.usp_GetAlertChannelSetting",
            commandType: CommandType.StoredProcedure,
            cancellationToken: cancellationToken));

        if (row is null) return null;

        return new AlertChannelSetting(
            row.EmailEnabled, row.EmailRecipientsCsv,
            row.SmtpEnabled, row.SmtpHost, row.SmtpPort, row.SmtpUsername, Unprotect(_smtpProtector, "SmtpPassword", row.SmtpPasswordProtected), row.SmtpUseSsl, row.SmtpFromAddress,
            row.AcsEnabled, Unprotect(_acsProtector, "AcsConnectionString", row.AcsConnectionStringProtected), row.AcsFromAddress,
            row.SendGridEnabled, Unprotect(_sendGridProtector, "SendGridApiKey", row.SendGridApiKeyProtected), row.SendGridFromAddress,
            row.TeamsEnabled, Unprotect(_teamsProtector, "TeamsWebhookUrl", row.TeamsWebhookUrlProtected),
            row.UpdatedAt, row.UpdatedBy);
    }

    public async Task UpsertAsync(AlertChannelSettingsUpdateRequest request, string changedBy, CancellationToken cancellationToken)
    {
        var parameters = new DynamicParameters();
        parameters.Add("EmailEnabled", request.EmailEnabled, DbType.Boolean);
        parameters.Add("EmailRecipientsCsv", request.EmailRecipientsCsv, DbType.String, size: 2000);
        parameters.Add("SmtpEnabled", request.SmtpEnabled, DbType.Boolean);
        parameters.Add("SmtpHost", request.SmtpHost, DbType.String, size: 256);
        parameters.Add("SmtpPort", request.SmtpPort, DbType.Int32);
        parameters.Add("SmtpUsername", request.SmtpUsername, DbType.String, size: 256);
        parameters.Add("ShouldUpdateSmtpPassword", request.SmtpPassword is not null, DbType.Boolean);
        parameters.Add("SmtpPasswordProtected", string.IsNullOrEmpty(request.SmtpPassword) ? null : _smtpProtector.Protect(request.SmtpPassword));
        parameters.Add("SmtpUseSsl", request.SmtpUseSsl, DbType.Boolean);
        parameters.Add("SmtpFromAddress", request.SmtpFromAddress, DbType.String, size: 256);
        parameters.Add("AcsEnabled", request.AcsEnabled, DbType.Boolean);
        parameters.Add("ShouldUpdateAcsConnection", request.AcsConnectionString is not null, DbType.Boolean);
        parameters.Add("AcsConnectionStringProtected", string.IsNullOrEmpty(request.AcsConnectionString) ? null : _acsProtector.Protect(request.AcsConnectionString));
        parameters.Add("AcsFromAddress", request.AcsFromAddress, DbType.String, size: 256);
        parameters.Add("SendGridEnabled", request.SendGridEnabled, DbType.Boolean);
        parameters.Add("ShouldUpdateSendGridApiKey", request.SendGridApiKey is not null, DbType.Boolean);
        parameters.Add("SendGridApiKeyProtected", string.IsNullOrEmpty(request.SendGridApiKey) ? null : _sendGridProtector.Protect(request.SendGridApiKey));
        parameters.Add("SendGridFromAddress", request.SendGridFromAddress, DbType.String, size: 256);
        parameters.Add("TeamsEnabled", request.TeamsEnabled, DbType.Boolean);
        parameters.Add("ShouldUpdateTeamsWebhook", request.TeamsWebhookUrl is not null, DbType.Boolean);
        parameters.Add("TeamsWebhookUrlProtected", string.IsNullOrEmpty(request.TeamsWebhookUrl) ? null : _teamsProtector.Protect(request.TeamsWebhookUrl));
        parameters.Add("ChangedBy", changedBy, DbType.String, size: 128);

        await using var connection = OpenConnection();
        await connection.ExecuteAsync(new CommandDefinition(
            "monitoring.usp_UpsertAlertChannelSetting",
            parameters,
            commandType: CommandType.StoredProcedure,
            cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<AlertChannelSettingAuditEntry>> GetAuditAsync(CancellationToken cancellationToken)
    {
        await using var connection = OpenConnection();
        var rows = await connection.QueryAsync<AuditRow>(new CommandDefinition(
            "monitoring.usp_GetAlertChannelSettingAudit",
            commandType: CommandType.StoredProcedure,
            cancellationToken: cancellationToken));

        return rows.Select(r => new AlertChannelSettingAuditEntry(
            r.EmailEnabled, r.SmtpEnabled, r.SmtpPasswordChanged, r.AcsEnabled, r.AcsConnectionStringChanged,
            r.SendGridEnabled, r.SendGridApiKeyChanged, r.TeamsEnabled, r.TeamsWebhookChanged, r.ChangedAt, r.ChangedBy)).ToList();
    }

    /// <summary>Never throws: an undecryptable value (key ring rotated/lost) is treated as "not configured" rather than breaking every read - same pattern as SqlLlmClientSettingsStore.UnprotectAsync (no legacy-plaintext migration needed here since these columns only ever stored encrypted values). Logs a warning so a silently-disabled channel after a redeploy/key-ring mismatch is diagnosable instead of invisible.</summary>
    private string? Unprotect(IDataProtector protector, string fieldName, string? storedValue)
    {
        if (string.IsNullOrEmpty(storedValue)) return null;
        try
        {
            return protector.Unprotect(storedValue);
        }
        catch (CryptographicException ex)
        {
            logger.LogWarning(ex, "[SqlAlertChannelSettingsStore] could not decrypt the stored {Field} (Data Protection key ring unavailable/rotated) - treating as unconfigured.", fieldName);
            return null;
        }
    }

    private SqlConnection OpenConnection()
    {
        var options = storeOptions.Value;
        if (!options.IsConfigured)
        {
            throw new InvalidOperationException($"{JourneyStoreOptions.SectionName}:ConnectionString is not configured.");
        }
        return new SqlConnection(options.ConnectionString);
    }

    private sealed record SettingRow(
        bool EmailEnabled, string? EmailRecipientsCsv,
        bool SmtpEnabled, string? SmtpHost, int? SmtpPort, string? SmtpUsername, string? SmtpPasswordProtected, bool SmtpUseSsl, string? SmtpFromAddress,
        bool AcsEnabled, string? AcsConnectionStringProtected, string? AcsFromAddress,
        bool SendGridEnabled, string? SendGridApiKeyProtected, string? SendGridFromAddress,
        bool TeamsEnabled, string? TeamsWebhookUrlProtected,
        DateTimeOffset? UpdatedAt, string? UpdatedBy);

    private sealed record AuditRow(
        bool EmailEnabled, bool SmtpEnabled, bool SmtpPasswordChanged, bool AcsEnabled, bool AcsConnectionStringChanged,
        bool SendGridEnabled, bool SendGridApiKeyChanged, bool TeamsEnabled, bool TeamsWebhookChanged,
        DateTimeOffset ChangedAt, string ChangedBy);
}
