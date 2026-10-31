namespace JourneyMonitor.Core.Abstractions;

/// <summary>
/// One agent's pause switch. Absent from the store means "never paused" - there is no row with
/// <see cref="IsPaused"/> false unless somebody explicitly resumed it, so absence and false mean
/// the same thing to every reader.
/// </summary>
public sealed record AgentPauseSetting(
    string MonkeyKey,
    bool IsPaused,
    DateTimeOffset UpdatedAt,
    string UpdatedBy);

/// <summary>
/// Durable pause state for the Simian Army agents (see <c>monitoring.AgentPauseSetting</c> /
/// <c>monitoring.AgentPauseSettingAudit</c>). Backed by the same SQL database as
/// <see cref="IJourneyStore"/>; when that is not configured the in-memory implementation is
/// registered instead, so pausing still works for the lifetime of the process.
/// </summary>
public interface IAgentPauseStore
{
    /// <summary>Every agent with a stored pause state, newest value only (no history).</summary>
    Task<IReadOnlyList<AgentPauseSetting>> GetAllAsync(CancellationToken cancellationToken);

    /// <summary>Null when this agent's switch has never been touched.</summary>
    Task<AgentPauseSetting?> GetAsync(string monkeyKey, CancellationToken cancellationToken);

    /// <summary>Sets the switch and appends an audit row in the same transaction; returns the stored state.</summary>
    Task<AgentPauseSetting> UpsertAsync(string monkeyKey, bool isPaused, string changedBy, CancellationToken cancellationToken);
}
