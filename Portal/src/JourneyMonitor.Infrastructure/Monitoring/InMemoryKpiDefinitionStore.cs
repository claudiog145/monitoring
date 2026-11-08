using System.Text.Json;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;

namespace JourneyMonitor.Infrastructure.Monitoring;

/// <summary>
/// Process-local KPI definitions with the same semantics as SqlKpiDefinitionStore: id 0 inserts with
/// the next id, any other id updates (unknown id throws), (ProfileKey, Pillar, Kpi, Domain, Stage, Grain) is unique and
/// every upsert appends an audit snapshot shaped like the SQL FOR JSON row.
/// </summary>
public sealed class InMemoryKpiDefinitionStore(TimeProvider? timeProvider = null) : IKpiDefinitionStore
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly SortedDictionary<int, KpiDefinition> _definitions = [];
    private readonly List<KpiDefinitionAuditEntry> _audit = [];
    private int _nextId;
    private long _nextAuditId;

    public Task<IReadOnlyList<KpiDefinition>> GetAllAsync(CancellationToken ct) => GetAllAsync(null, ct);

    public Task<IReadOnlyList<KpiDefinition>> GetAllAsync(string? profileKey = null, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(KpiProfileFilter.Apply(_definitions.Values.ToList(), profileKey));
        }
    }

    public Task<IReadOnlyList<KpiDefinition>> GetEnabledAsync(KpiPillar? pillar, CancellationToken ct) => GetEnabledAsync(pillar, null, ct);

    public Task<IReadOnlyList<KpiDefinition>> GetEnabledAsync(KpiPillar? pillar, string? profileKey = null, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(KpiProfileFilter.Apply(_definitions.Values
                .Where(d => d.Enabled && (pillar is null || d.Pillar == pillar))
                .ToList(), profileKey));
        }
    }

    public Task<KpiDefinition?> GetAsync(int kpiDefinitionId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_definitions.GetValueOrDefault(kpiDefinitionId));
        }
    }

    public Task<KpiDefinition> UpsertAsync(KpiDefinition definition, string changedBy, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        lock (_gate)
        {
            var id = definition.KpiDefinitionId;
            if (id != 0 && !_definitions.ContainsKey(id))
            {
                throw new InvalidOperationException($"KpiDefinition {id} not found.");
            }

            var stage = definition.Stage ?? "";
            var profileKey = definition.ProfileKey.Trim();
            var duplicate = _definitions.Values.Any(d =>
                d.KpiDefinitionId != id
                && string.Equals(d.ProfileKey, profileKey, StringComparison.OrdinalIgnoreCase)
                && d.Pillar == definition.Pillar
                && string.Equals(d.Kpi, definition.Kpi, StringComparison.OrdinalIgnoreCase)
                && string.Equals(d.Domain, definition.Domain, StringComparison.OrdinalIgnoreCase)
                && string.Equals(d.Stage, stage, StringComparison.OrdinalIgnoreCase)
                && d.Grain == definition.Grain);
            if (duplicate)
            {
                throw new InvalidOperationException(
                    $"A KpiDefinition for ({profileKey}, {definition.Pillar}, {definition.Kpi}, {definition.Domain}, '{stage}', {definition.Grain}) already exists.");
            }

            if (id == 0)
            {
                id = ++_nextId;
            }

            var now = _time.GetUtcNow();
            var stored = definition with { KpiDefinitionId = id, Stage = stage, ProfileKey = profileKey, UpdatedAt = now, UpdatedBy = changedBy };
            _definitions[id] = stored;
            _audit.Add(new KpiDefinitionAuditEntry(++_nextAuditId, id, Snapshot(stored), now, changedBy));
            return Task.FromResult(stored);
        }
    }

    public Task<IReadOnlyList<KpiDefinitionAuditEntry>> GetAuditAsync(int kpiDefinitionId, int top, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<KpiDefinitionAuditEntry>>(_audit
                .Where(a => a.KpiDefinitionId == kpiDefinitionId)
                .OrderByDescending(a => a.ChangedAt)
                .ThenByDescending(a => a.AuditId)
                .Take(Math.Max(top, 0))
                .ToList());
        }
    }

    // Same column names as the SQL table so audit consumers see one shape regardless of the backing store.
    private static string Snapshot(KpiDefinition d) => JsonSerializer.Serialize(new
    {
        d.KpiDefinitionId,
        Pillar = d.Pillar.ToString(),
        d.Kpi,
        d.Domain,
        d.Stage,
        d.SourceKey,
        Direction = d.Direction.ToString(),
        Aggregation = d.Aggregation.ToString(),
        EvaluationFrequencySec = (int)d.EvaluationFrequency.TotalSeconds,
        BucketSizeSec = (int)d.BucketSize.TotalSeconds,
        LookbackWindowSec = (int)d.LookbackWindow.TotalSeconds,
        d.SampleSize,
        d.MinObservationsPerBucket,
        DetectionMethod = d.DetectionMethod.ToString(),
        d.MethodParamsJson,
        d.ConsecutiveCount,
        d.CooldownMinutes,
        d.Enabled,
        d.UpdatedAt,
        d.UpdatedBy,
        d.ProfileKey,
        Grain = d.Grain.ToString().ToLowerInvariant(),
        d.MaxEntitiesPerBucket,
    });
}
