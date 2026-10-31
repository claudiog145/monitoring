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
/// Both credentials (the GitHub token and the Foundry API key) are encrypted at rest with
/// <see cref="IDataProtector"/> (key ring lives outside SQL - see the comment in
/// Functions/Program.cs) so a compromised database backup alone does not also hand over a usable
/// credential. Masking in the UI/audit (see LlmClientSettingsService) only protects what's
/// displayed; this protects what's actually stored.
///
/// Each secret gets its OWN protector purpose: a payload encrypted for one can never be decrypted
/// as the other, so a bug that crosses the two fields fails loudly instead of leaking.
/// </summary>
public sealed class SqlLlmClientSettingsStore(
    IOptions<JourneyStoreOptions> storeOptions,
    IDataProtectionProvider dataProtectionProvider,
    ILogger<SqlLlmClientSettingsStore> logger) : ILlmClientSettingsStore
{
    private readonly IDataProtector _protector = dataProtectionProvider.CreateProtector("JourneyMonitor.LlmClientSetting.GitHubToken");
    private readonly IDataProtector _foundryKeyProtector = dataProtectionProvider.CreateProtector("JourneyMonitor.LlmClientSetting.FoundryApiKey");

    public async Task<LlmClientSetting?> GetAsync(CancellationToken cancellationToken)
    {
        await using var connection = OpenConnection();
        var row = await connection.QuerySingleOrDefaultAsync<LlmClientSettingRow>(new CommandDefinition(
            "monitoring.usp_GetLlmClientSetting",
            commandType: CommandType.StoredProcedure,
            cancellationToken: cancellationToken));

        if (row is null) return null;

        var token = await UnprotectAsync(row.GitHubToken, cancellationToken);

        return new LlmClientSetting(token, row.Model, row.UpdatedAt, row.UpdatedBy)
        {
            Provider = row.Provider,
            DoctorProvider = row.DoctorProvider,
            ScoutProvider = row.ScoutProvider,
            InspectorProvider = row.InspectorProvider,
            FoundryEndpoint = row.FoundryEndpoint,
            FoundryAuthMode = row.FoundryAuthMode,
            FoundryApiKey = UnprotectFoundryKey(row.FoundryApiKey),
            FoundryChatDeployment = row.FoundryChatDeployment,
            FoundryEmbeddingDeployment = row.FoundryEmbeddingDeployment,
        };
    }

    public async Task UpsertAsync(LlmClientSettingUpdate update, string changedBy, CancellationToken cancellationToken)
    {
        // null means "leave unchanged" at the abstraction boundary (see LlmClientSettingUpdate);
        // an empty string is the only way to explicitly clear a field, stored as SQL NULL.
        var protectedToken = string.IsNullOrEmpty(update.GitHubToken) ? null : _protector.Protect(update.GitHubToken);
        var protectedKey = string.IsNullOrEmpty(update.FoundryApiKey) ? null : _foundryKeyProtector.Protect(update.FoundryApiKey);

        var parameters = new DynamicParameters();
        parameters.Add("ShouldUpdateToken", update.GitHubToken is not null, DbType.Boolean);
        parameters.Add("GitHubToken", protectedToken, DbType.String, size: 1000);
        parameters.Add("ShouldUpdateModel", update.Model is not null, DbType.Boolean);
        parameters.Add("Model", NullIfBlank(update.Model), DbType.String, size: 64);
        parameters.Add("ShouldUpdateProviders", HasAnyProviderField(update), DbType.Boolean);
        parameters.Add("Provider", NullIfBlank(update.Provider), DbType.String, size: 32);
        parameters.Add("DoctorProvider", NullIfBlank(update.DoctorProvider), DbType.String, size: 32);
        parameters.Add("ScoutProvider", NullIfBlank(update.ScoutProvider), DbType.String, size: 32);
        parameters.Add("InspectorProvider", NullIfBlank(update.InspectorProvider), DbType.String, size: 32);
        parameters.Add("ShouldUpdateFoundry", HasAnyFoundryField(update), DbType.Boolean);
        parameters.Add("FoundryEndpoint", NullIfBlank(update.FoundryEndpoint), DbType.String, size: 400);
        parameters.Add("FoundryAuthMode", NullIfBlank(update.FoundryAuthMode), DbType.String, size: 32);
        parameters.Add("FoundryChatDeployment", NullIfBlank(update.FoundryChatDeployment), DbType.String, size: 128);
        parameters.Add("FoundryEmbeddingDeployment", NullIfBlank(update.FoundryEmbeddingDeployment), DbType.String, size: 128);
        parameters.Add("ShouldUpdateFoundryApiKey", update.FoundryApiKey is not null, DbType.Boolean);
        parameters.Add("FoundryApiKey", protectedKey, DbType.String, size: 1000);
        parameters.Add("ChangedBy", changedBy, DbType.String, size: 128);

        await using var connection = OpenConnection();
        await connection.ExecuteAsync(new CommandDefinition(
            "monitoring.usp_UpsertLlmClientSetting",
            parameters,
            commandType: CommandType.StoredProcedure,
            cancellationToken: cancellationToken));
    }

    /// <summary>
    /// A write that touches no provider field at all must not blank the stored ones - that is how
    /// saving only a token would silently move every agent back to the default provider.
    /// </summary>
    private static bool HasAnyProviderField(LlmClientSettingUpdate update) =>
        update.Provider is not null ||
        update.DoctorProvider is not null ||
        update.ScoutProvider is not null ||
        update.InspectorProvider is not null;

    private static bool HasAnyFoundryField(LlmClientSettingUpdate update) =>
        update.FoundryEndpoint is not null ||
        update.FoundryAuthMode is not null ||
        update.FoundryChatDeployment is not null ||
        update.FoundryEmbeddingDeployment is not null;

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public async Task<IReadOnlyList<LlmClientSettingAuditEntry>> GetAuditAsync(CancellationToken cancellationToken)
    {
        await using var connection = OpenConnection();
        var rows = await connection.QueryAsync<LlmClientSettingAuditRow>(new CommandDefinition(
            "monitoring.usp_GetLlmClientSettingAudit",
            commandType: CommandType.StoredProcedure,
            cancellationToken: cancellationToken));

        return rows.Select(r => new LlmClientSettingAuditEntry(r.TokenChanged, r.Model, r.ChangedAt, r.ChangedBy)
        {
            FoundryApiKeyChanged = r.FoundryApiKeyChanged,
            Provider = r.Provider,
            DoctorProvider = r.DoctorProvider,
            ScoutProvider = r.ScoutProvider,
            InspectorProvider = r.InspectorProvider,
            FoundryEndpoint = r.FoundryEndpoint,
            FoundryAuthMode = r.FoundryAuthMode,
            FoundryChatDeployment = r.FoundryChatDeployment,
            FoundryEmbeddingDeployment = r.FoundryEmbeddingDeployment,
        }).ToList();
    }

    /// <summary>
    /// Unlike the GitHub token there is no legacy plaintext era for this field, so an undecryptable
    /// value means exactly one thing: the key ring that wrote it is gone. Treated as "not
    /// configured" (falls back to Foundry:ApiKey) rather than breaking every call.
    /// </summary>
    private string? UnprotectFoundryKey(string? storedValue)
    {
        if (string.IsNullOrEmpty(storedValue)) return null;

        try
        {
            return _foundryKeyProtector.Unprotect(storedValue);
        }
        catch (CryptographicException ex)
        {
            logger.LogWarning(ex, "Could not decrypt the stored Foundry API key (Data Protection key ring unavailable/rotated) - treating as unconfigured.");
            return null;
        }
    }

    /// <summary>
    /// Two distinct cases, neither of which throws back to the caller:
    /// - A legacy plaintext "gho_..." token saved before encryption was added (recognizable by
    ///   that prefix, which a Data-Protection payload never has) - returned as-is AND
    ///   re-encrypted in place via <see cref="UpsertAsync"/> so this self-healing migration only
    ///   has to run once per row (recorded in the audit trail as <see cref="MigrationChangedBy"/>
    ///   rather than treating the already-saved credential as unconfigured after upgrade).
    /// - A genuinely undecryptable value (e.g. the Data Protection key ring was lost/rotated) -
    ///   treated as "no token configured yet" (falls back to Ai:GitHubToken, see
    ///   LlmClientSettingsResolver) rather than breaking every Doctor Monkey call or the Settings
    ///   tab GET.
    /// </summary>
    private const string MigrationChangedBy = "system:token-encryption-migration";

    private async Task<string?> UnprotectAsync(string? storedValue, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(storedValue)) return null;

        if (storedValue.StartsWith("gho_", StringComparison.Ordinal))
        {
            logger.LogWarning("Found a legacy plaintext GitHub token in monitoring.LlmClientSetting (saved before encryption was added) - re-encrypting it in place.");
            try
            {
                await UpsertAsync(new LlmClientSettingUpdate { GitHubToken = storedValue }, MigrationChangedBy, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Best-effort - a failure here must not lose the ability to read the (still
                // valid, still plaintext) token this call already has in hand; simply retried on
                // the next read.
                logger.LogWarning(ex, "Failed to re-encrypt the legacy plaintext GitHub token - it remains plaintext until the next successful attempt.");
            }
            return storedValue;
        }

        try
        {
            return _protector.Unprotect(storedValue);
        }
        catch (CryptographicException ex)
        {
            logger.LogWarning(ex, "Could not decrypt the stored LLM client GitHub token (Data Protection key ring unavailable/rotated) - treating as unconfigured.");
            return null;
        }
    }

    private SqlConnection OpenConnection()
    {
        var options = storeOptions.Value;
        if (!options.IsConfigured)
        {
            throw new InvalidOperationException(
                $"{JourneyStoreOptions.SectionName}:ConnectionString is not configured.");
        }
        return new SqlConnection(options.ConnectionString);
    }

    private sealed record LlmClientSettingRow(
        string? GitHubToken,
        string? Model,
        string? Provider,
        string? DoctorProvider,
        string? ScoutProvider,
        string? InspectorProvider,
        string? FoundryEndpoint,
        string? FoundryAuthMode,
        string? FoundryApiKey,
        string? FoundryChatDeployment,
        string? FoundryEmbeddingDeployment,
        DateTimeOffset? UpdatedAt,
        string? UpdatedBy);

    private sealed record LlmClientSettingAuditRow(
        bool TokenChanged,
        bool FoundryApiKeyChanged,
        string? Model,
        string? Provider,
        string? DoctorProvider,
        string? ScoutProvider,
        string? InspectorProvider,
        string? FoundryEndpoint,
        string? FoundryAuthMode,
        string? FoundryChatDeployment,
        string? FoundryEmbeddingDeployment,
        DateTimeOffset ChangedAt,
        string ChangedBy);
}
