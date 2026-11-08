using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Infrastructure.Sql;

namespace JourneyMonitor.Infrastructure.Monitoring;

/// <summary>
/// Process-local anomalies with incremental AnomalyIds. Like usp_GetRecentKpiAnomalies (an inner join
/// with KpiDefinition), GetRecentAsync resolves pillar/domain through the definition store and drops
/// anomalies whose definition is unknown. Reads are per series (the v3 overload is the domain aggregate,
/// EntityKey ""), filtered before the top is applied.
/// </summary>
public sealed class InMemoryKpiAnomalyStore(IKpiDefinitionStore definitions) : IKpiAnomalyStore
{
    private readonly IKpiDefinitionStore _definitions = definitions ?? throw new ArgumentNullException(nameof(definitions));
    private readonly object _gate = new();
    private readonly List<KpiAnomaly> _anomalies = [];
    private long _nextId;

    public Task<KpiAnomaly> AddAsync(KpiAnomaly anomaly, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(anomaly);
        KpiValueGuard.EnsureEntityKey(anomaly.EntityKey, nameof(anomaly));
        lock (_gate)
        {
            var stored = anomaly with { AnomalyId = ++_nextId, EntityKey = KpiValueGuard.NormalizeEntityKey(anomaly.EntityKey) };
            _anomalies.Add(stored);
            return Task.FromResult(stored);
        }
    }

    public Task AttachInvestigationAsync(long anomalyId, Guid investigationId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var index = _anomalies.FindIndex(a => a.AnomalyId == anomalyId);
            if (index >= 0)
            {
                _anomalies[index] = _anomalies[index] with { InvestigationId = investigationId };
            }
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<KpiAnomaly>> GetRecentAsync(KpiPillar? pillar, string? domain, int top, string? profileKey = null, CancellationToken ct = default) =>
        GetRecentAsync(pillar, domain, top, profileKey, null, ct);

    public async Task<IReadOnlyList<KpiAnomaly>> GetRecentAsync(KpiPillar? pillar, string? domain, int top, string? profileKey, string? entityKey, CancellationToken ct = default)
    {
        var definitions = (await _definitions.GetAllAsync(profileKey, ct)).ToDictionary(d => d.KpiDefinitionId);
        var key = KpiValueGuard.NormalizeEntityKey(entityKey);

        List<KpiAnomaly> snapshot;
        lock (_gate)
        {
            snapshot = [.. _anomalies];
        }

        return snapshot
            .Where(a => string.Equals(a.EntityKey, key, StringComparison.OrdinalIgnoreCase)
                && definitions.TryGetValue(a.KpiDefinitionId, out var d)
                && (pillar is null || d.Pillar == pillar)
                && (domain is null || string.Equals(d.Domain, domain, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(a => a.DetectedAt)
            .ThenByDescending(a => a.AnomalyId)
            .Take(Math.Max(top, 0))
            .ToList();
    }
}
