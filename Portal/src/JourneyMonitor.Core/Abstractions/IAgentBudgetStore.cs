namespace JourneyMonitor.Core.Abstractions;

/// <summary>
/// One agent's stored spend limits. Every value is nullable and null means "inherit the configured
/// default" - that is what makes writing one field safe: it does not pin the others to whatever
/// the defaults were the day the row was written, and clearing a field back to null is how a dial
/// is reverted to configuration.
///
/// The model and provider are deliberately NOT here; those live in
/// <c>monitoring.LlmClientSetting</c>. Nor is an on/off switch - that is the pause. Nor the
/// per-call token ceiling or tool rounds: those are read inside the specialists, which this does
/// not reach, and a dial nobody applies is worse than one that does not exist.
/// </summary>
public sealed record AgentBudgetSetting(
    string MonkeyKey,
    int? MaxRunsPerDay,
    string? DebateModels,
    int? DebateRounds,
    int? MaxHelpHops,
    int? SkipDebateWhenContributorsBelow,
    DateTimeOffset UpdatedAt,
    string UpdatedBy)
{
    public static AgentBudgetSetting Empty(string monkeyKey) =>
        new(monkeyKey, null, null, null, null, null, DateTimeOffset.MinValue, "");

    /// <summary>True when nothing is overridden, i.e. this agent runs entirely on its configured defaults.</summary>
    public bool IsEmpty =>
        MaxRunsPerDay is null && string.IsNullOrWhiteSpace(DebateModels) &&
        DebateRounds is null && MaxHelpHops is null && SkipDebateWhenContributorsBelow is null;
}

/// <summary>One historical value of an agent's limits, append-only.</summary>
public sealed record AgentBudgetSettingAudit(
    long AuditId,
    string MonkeyKey,
    int? MaxRunsPerDay,
    string? DebateModels,
    int? DebateRounds,
    int? MaxHelpHops,
    int? SkipDebateWhenContributorsBelow,
    DateTimeOffset ChangedAt,
    string ChangedBy);

/// <summary>
/// Durable per-agent spend limits (see <c>monitoring.AgentBudgetSetting</c> /
/// <c>monitoring.AgentBudgetSettingAudit</c>). Backed by the same SQL database as
/// <see cref="IJourneyStore"/>; when that is not configured the in-memory implementation is
/// registered instead, so the dials still work for the lifetime of the process.
/// </summary>
public interface IAgentBudgetStore
{
    /// <summary>Every agent with stored limits, current values only (no history).</summary>
    Task<IReadOnlyList<AgentBudgetSetting>> GetAllAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Replaces this agent's limits wholesale and appends an audit row in the same transaction.
    /// A whole-row write, not a per-field merge: sending null for a field is how it is reverted.
    /// </summary>
    Task<AgentBudgetSetting> UpsertAsync(AgentBudgetSetting setting, string changedBy, CancellationToken cancellationToken);

    /// <summary>Most recent change first.</summary>
    Task<IReadOnlyList<AgentBudgetSettingAudit>> GetAuditAsync(string monkeyKey, int top, CancellationToken cancellationToken);
}
