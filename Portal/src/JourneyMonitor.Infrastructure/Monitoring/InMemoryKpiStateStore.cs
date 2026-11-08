using System.Collections.Concurrent;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Infrastructure.Sql;

namespace JourneyMonitor.Infrastructure.Monitoring;

/// <summary>
/// Process-local detection state keyed by (KpiDefinitionId, EntityKey), EntityKey case-insensitive like
/// the SQL column; a series never saved reads as KpiDetectionState.Initial(id, entityKey).
/// </summary>
public sealed class InMemoryKpiStateStore : IKpiStateStore
{
    private readonly ConcurrentDictionary<(int, string), KpiDetectionState> _states = new(new StateKeyComparer());

    public Task<KpiDetectionState> GetAsync(int kpiDefinitionId, CancellationToken ct = default) =>
        GetAsync(kpiDefinitionId, null, ct);

    public Task<KpiDetectionState> GetAsync(int kpiDefinitionId, string? entityKey, CancellationToken ct = default)
    {
        var key = KpiValueGuard.NormalizeEntityKey(entityKey);
        return Task.FromResult(_states.TryGetValue((kpiDefinitionId, key), out var state)
            ? state
            : KpiDetectionState.Initial(kpiDefinitionId, key));
    }

    public Task<IReadOnlyList<KpiDetectionState>> GetEntityStatesAsync(int kpiDefinitionId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<KpiDetectionState>>(_states
            .Where(kv => kv.Key.Item1 == kpiDefinitionId && kv.Key.Item2.Length > 0)
            .Select(kv => kv.Value)
            .OrderBy(s => s.EntityKey, StringComparer.OrdinalIgnoreCase)
            .ToList());

    public Task SaveAsync(KpiDetectionState state, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        KpiValueGuard.EnsureEntityKey(state.EntityKey, nameof(state));
        var stored = state with { EntityKey = KpiValueGuard.NormalizeEntityKey(state.EntityKey) };
        _states[(stored.KpiDefinitionId, stored.EntityKey)] = stored;
        return Task.CompletedTask;
    }

    private sealed class StateKeyComparer : IEqualityComparer<(int, string)>
    {
        public bool Equals((int, string) x, (int, string) y) =>
            x.Item1 == y.Item1 && StringComparer.OrdinalIgnoreCase.Equals(x.Item2, y.Item2);

        public int GetHashCode((int, string) obj) =>
            HashCode.Combine(obj.Item1, StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Item2));
    }
}
