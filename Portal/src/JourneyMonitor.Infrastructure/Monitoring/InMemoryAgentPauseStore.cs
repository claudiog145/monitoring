using System.Collections.Concurrent;
using JourneyMonitor.Core.Abstractions;

namespace JourneyMonitor.Infrastructure.Monitoring;

/// <summary>
/// Process-local pause state for a clone with no database - see InMemoryFindingsRepository
/// remarks. Honest limitation: a restart resumes every paused agent, which is why the SQL store
/// is what the portal actually runs on.
/// </summary>
public sealed class InMemoryAgentPauseStore : IAgentPauseStore
{
    private readonly ConcurrentDictionary<string, AgentPauseSetting> _byMonkeyKey = new(StringComparer.OrdinalIgnoreCase);

    public Task<IReadOnlyList<AgentPauseSetting>> GetAllAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AgentPauseSetting>>(_byMonkeyKey.Values.OrderBy(p => p.MonkeyKey, StringComparer.Ordinal).ToList());

    public Task<AgentPauseSetting?> GetAsync(string monkeyKey, CancellationToken cancellationToken) =>
        Task.FromResult(_byMonkeyKey.TryGetValue(monkeyKey, out var setting) ? setting : null);

    public Task<AgentPauseSetting> UpsertAsync(string monkeyKey, bool isPaused, string changedBy, CancellationToken cancellationToken)
    {
        var setting = new AgentPauseSetting(monkeyKey, isPaused, DateTimeOffset.UtcNow, changedBy);
        _byMonkeyKey[monkeyKey] = setting;
        return Task.FromResult(setting);
    }
}
