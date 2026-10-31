using System.Collections.Concurrent;
using JourneyMonitor.Core.Abstractions;

namespace JourneyMonitor.Infrastructure.Monitoring;

/// <summary>
/// Process-local spend limits for a clone with no database - see InMemoryFindingsRepository
/// remarks. Honest limitation: a restart puts every agent back on its configured defaults, which
/// is why the SQL store is what the portal actually runs on.
/// </summary>
public sealed class InMemoryAgentBudgetStore : IAgentBudgetStore
{
    private readonly ConcurrentDictionary<string, AgentBudgetSetting> _byMonkeyKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, List<AgentBudgetSettingAudit>> _audit = new(StringComparer.OrdinalIgnoreCase);
    private long _nextAuditId = 1;

    public Task<IReadOnlyList<AgentBudgetSetting>> GetAllAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AgentBudgetSetting>>(
            _byMonkeyKey.Values.OrderBy(s => s.MonkeyKey, StringComparer.Ordinal).ToList());

    public Task<AgentBudgetSetting> UpsertAsync(AgentBudgetSetting setting, string changedBy, CancellationToken cancellationToken)
    {
        var stored = setting with { UpdatedAt = DateTimeOffset.UtcNow, UpdatedBy = changedBy };
        _byMonkeyKey[setting.MonkeyKey] = stored;

        var history = _audit.GetOrAdd(setting.MonkeyKey, _ => []);
        lock (history)
        {
            history.Add(new AgentBudgetSettingAudit(
                Interlocked.Increment(ref _nextAuditId), stored.MonkeyKey, stored.MaxRunsPerDay,
                stored.DebateModels, stored.DebateRounds, stored.MaxHelpHops,
                stored.SkipDebateWhenContributorsBelow, stored.UpdatedAt, changedBy));
        }

        return Task.FromResult(stored);
    }

    public Task<IReadOnlyList<AgentBudgetSettingAudit>> GetAuditAsync(string monkeyKey, int top, CancellationToken cancellationToken)
    {
        if (!_audit.TryGetValue(monkeyKey, out var history)) return Task.FromResult<IReadOnlyList<AgentBudgetSettingAudit>>([]);

        lock (history)
        {
            return Task.FromResult<IReadOnlyList<AgentBudgetSettingAudit>>(
                history.OrderByDescending(a => a.ChangedAt).ThenByDescending(a => a.AuditId).Take(top).ToList());
        }
    }
}
