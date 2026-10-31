using JourneyMonitor.Core.Options;

namespace JourneyMonitor.Core.Abstractions;

/// <summary>
/// The platform's LLM client configuration, editable from the Settings tab instead of an
/// environment-specific value (Ai:GitHubToken / Foundry:*) that has to be configured by hand in
/// every new environment (docker-compose, QA Container App, ...). Every field is nullable and
/// means "no override stored yet" - callers fall back to <see cref="AiOptions"/> /
/// <see cref="FoundryOptions"/>, same nullable-fallback pattern as <see cref="LatencySpikeSetting"/>.
///
/// Only the original four stay positional: every existing call site keeps constructing a valid
/// GitHub Copilot configuration without naming a single new field.
/// </summary>
public sealed record LlmClientSetting(
    string? GitHubToken,
    string? Model,
    DateTimeOffset? UpdatedAt,
    string? UpdatedBy)
{
    /// <summary>Global provider (see <see cref="LlmProviders"/>). Null = fall back to configuration.</summary>
    public string? Provider { get; init; }

    /// <summary>
    /// Per-agent overrides. Null means "inherit <see cref="Provider"/>" - deliberately not collapsed
    /// to the global value on read, so the Settings tab can tell "inherits" apart from "pinned to the
    /// same value", which behave differently the next time the global changes.
    /// </summary>
    public string? DoctorProvider { get; init; }

    public string? ScoutProvider { get; init; }

    public string? InspectorProvider { get; init; }

    public string? FoundryEndpoint { get; init; }

    /// <summary>See <see cref="FoundryAuthModes"/>.</summary>
    public string? FoundryAuthMode { get; init; }

    /// <summary>Encrypted at rest by the store, exactly like <see cref="GitHubToken"/>. Unused under managed identity.</summary>
    public string? FoundryApiKey { get; init; }

    public string? FoundryChatDeployment { get; init; }

    public string? FoundryEmbeddingDeployment { get; init; }
}

/// <summary>
/// One write to <see cref="ILlmClientSettingsStore"/>. Both secret fields follow the
/// "null = leave unchanged, empty string = clear explicitly" rule, so saving an unrelated field can
/// never wipe a credential as a side effect. Every other null simply means "unchanged".
/// </summary>
public sealed record LlmClientSettingUpdate
{
    /// <summary>Null leaves the stored token untouched; empty string clears it.</summary>
    public string? GitHubToken { get; init; }

    public string? Model { get; init; }

    public string? Provider { get; init; }

    public string? DoctorProvider { get; init; }

    public string? ScoutProvider { get; init; }

    public string? InspectorProvider { get; init; }

    public string? FoundryEndpoint { get; init; }

    public string? FoundryAuthMode { get; init; }

    /// <summary>Null leaves the stored key untouched; empty string clears it.</summary>
    public string? FoundryApiKey { get; init; }

    public string? FoundryChatDeployment { get; init; }

    public string? FoundryEmbeddingDeployment { get; init; }
}

/// <summary>
/// One historical change, kept forever for audit purposes. Deliberately does NOT store either raw
/// secret (past or present) - only whether it changed - so a compromised database never leaks a
/// history of every credential this setting ever held, just the current one. Non-secret fields
/// ARE recorded: "who pointed production at which model" is the question this table exists to answer.
/// </summary>
public sealed record LlmClientSettingAuditEntry(
    bool TokenChanged,
    string? Model,
    DateTimeOffset ChangedAt,
    string ChangedBy)
{
    public bool FoundryApiKeyChanged { get; init; }

    public string? Provider { get; init; }

    public string? DoctorProvider { get; init; }

    public string? ScoutProvider { get; init; }

    public string? InspectorProvider { get; init; }

    public string? FoundryEndpoint { get; init; }

    public string? FoundryAuthMode { get; init; }

    public string? FoundryChatDeployment { get; init; }

    public string? FoundryEmbeddingDeployment { get; init; }
}

/// <summary>
/// Durable, single-row override store for the platform's LLM client (see
/// <c>monitoring.LlmClientSetting</c> / <c>monitoring.LlmClientSettingAudit</c>). Backed by the
/// same SQL database as <see cref="IJourneyStore"/> - requires <c>JourneyStore:ConnectionString</c>
/// to be configured, otherwise callers must fall back to <see cref="AiOptions"/> /
/// <see cref="FoundryOptions"/> instead of throwing. Implementations must encrypt both
/// <see cref="LlmClientSetting.GitHubToken"/> and <see cref="LlmClientSetting.FoundryApiKey"/> at
/// rest with a key kept outside this database (see SqlLlmClientSettingsStore) - a masked API
/// response alone does not stop someone who can read the database/backups directly.
/// </summary>
public interface ILlmClientSettingsStore
{
    /// <summary>Null when no override has ever been saved.</summary>
    Task<LlmClientSetting?> GetAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Upserts the single stored row and appends an audit row in the same transaction.
    /// See <see cref="LlmClientSettingUpdate"/> for the null-versus-empty-string rule that keeps a
    /// credential from being cleared as a side effect of changing something else.
    /// </summary>
    Task UpsertAsync(LlmClientSettingUpdate update, string changedBy, CancellationToken cancellationToken);

    Task<IReadOnlyList<LlmClientSettingAuditEntry>> GetAuditAsync(CancellationToken cancellationToken);
}
