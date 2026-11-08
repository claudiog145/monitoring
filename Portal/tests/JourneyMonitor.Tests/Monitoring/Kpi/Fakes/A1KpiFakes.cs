using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;

namespace JourneyMonitor.Tests.Monitoring.Kpi.Fakes;

/// <summary>PillarHealthMonitorService uses a static per-pillar run lock, so its tests must not run in parallel with each other.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class A1MonitorRunLockCollection
{
    public const string Name = "A1 pillar health monitor run lock";
}

public sealed class A1FakeTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
}

public sealed class A1InMemoryDefinitionStore : IKpiDefinitionStore
{
    public List<KpiDefinition> Definitions { get; } = [];
    public int Calls { get; private set; }

    public Task<IReadOnlyList<KpiDefinition>> GetAllAsync(CancellationToken ct = default)
    {
        Calls++;
        return Task.FromResult<IReadOnlyList<KpiDefinition>>(Definitions.ToList());
    }

    public Task<IReadOnlyList<KpiDefinition>> GetEnabledAsync(KpiPillar? pillar, CancellationToken ct = default)
    {
        Calls++;
        var result = (IReadOnlyList<KpiDefinition>)Definitions.Where(d => d.Enabled && (pillar is null || d.Pillar == pillar)).ToList();
        return BeforeGetEnabled is null ? Task.FromResult(result) : BlockThenReturn(pillar, result);
    }

    /// <summary>Optional hook awaited before GetEnabledAsync returns (lets a test hold a run inside the monitor).</summary>
    public Func<KpiPillar?, Task>? BeforeGetEnabled { get; set; }

    private async Task<IReadOnlyList<KpiDefinition>> BlockThenReturn(KpiPillar? pillar, IReadOnlyList<KpiDefinition> result)
    {
        await BeforeGetEnabled!(pillar);
        return result;
    }

    public Task<KpiDefinition?> GetAsync(int kpiDefinitionId, CancellationToken ct = default)
    {
        Calls++;
        return Task.FromResult(Definitions.FirstOrDefault(d => d.KpiDefinitionId == kpiDefinitionId));
    }

    public Task<KpiDefinition> UpsertAsync(KpiDefinition definition, string changedBy, CancellationToken ct = default)
    {
        Calls++;
        Definitions.RemoveAll(d => d.KpiDefinitionId == definition.KpiDefinitionId);
        Definitions.Add(definition);
        return Task.FromResult(definition);
    }

    public Task<IReadOnlyList<KpiDefinitionAuditEntry>> GetAuditAsync(int kpiDefinitionId, int top, CancellationToken ct = default)
    {
        Calls++;
        return Task.FromResult<IReadOnlyList<KpiDefinitionAuditEntry>>([]);
    }
}

public sealed class A1InMemoryBucketStore : IKpiBucketStore
{
    public List<KpiBucket> Buckets { get; } = [];
    public List<(int Id, DateTimeOffset From, DateTimeOffset To)> RangeRequests { get; } = [];
    public int Calls { get; private set; }
    public HashSet<int> ThrowFor { get; } = [];

    public Task UpsertAsync(IReadOnlyList<KpiBucket> buckets, CancellationToken ct = default)
    {
        Calls++;
        foreach (var bucket in buckets)
        {
            Buckets.RemoveAll(b => b.KpiDefinitionId == bucket.KpiDefinitionId && b.BucketStart == bucket.BucketStart);
            Buckets.Add(bucket);
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<KpiBucket>> GetRangeAsync(int kpiDefinitionId, DateTimeOffset fromInclusive, DateTimeOffset toExclusive, CancellationToken ct = default)
    {
        Calls++;
        if (ThrowFor.Contains(kpiDefinitionId)) throw new InvalidOperationException("bucket store unavailable");
        RangeRequests.Add((kpiDefinitionId, fromInclusive, toExclusive));
        return Task.FromResult<IReadOnlyList<KpiBucket>>(Buckets
            .Where(b => b.KpiDefinitionId == kpiDefinitionId && b.BucketStart >= fromInclusive && b.BucketStart < toExclusive)
            .OrderBy(b => b.BucketStart)
            .ToList());
    }
}

public sealed class A1InMemoryStateStore : IKpiStateStore
{
    public Dictionary<int, KpiDetectionState> States { get; } = [];
    public List<KpiDetectionState> Saved { get; } = [];
    public int Calls { get; private set; }

    public Task<KpiDetectionState> GetAsync(int kpiDefinitionId, CancellationToken ct = default)
    {
        Calls++;
        var state = States.TryGetValue(kpiDefinitionId, out var s) ? s : KpiDetectionState.Initial(kpiDefinitionId);
        AfterGet?.Invoke(kpiDefinitionId);
        return Task.FromResult(state);
    }

    /// <summary>Optional hook run after each read (lets a test simulate a concurrent run saving state in between).</summary>
    public Action<int>? AfterGet { get; set; }

    public Task SaveAsync(KpiDetectionState state, CancellationToken ct = default)
    {
        Calls++;
        ct.ThrowIfCancellationRequested();
        States[state.KpiDefinitionId] = state;
        Saved.Add(state);
        return Task.CompletedTask;
    }
}

public sealed class A1InMemoryAnomalyStore : IKpiAnomalyStore
{
    private long _nextId = 100;
    public List<KpiAnomaly> Anomalies { get; } = [];
    public int Calls { get; private set; }

    public Task<KpiAnomaly> AddAsync(KpiAnomaly anomaly, CancellationToken ct = default)
    {
        Calls++;
        var stored = anomaly with { AnomalyId = ++_nextId };
        Anomalies.Add(stored);
        return Task.FromResult(stored);
    }

    public Task AttachInvestigationAsync(long anomalyId, Guid investigationId, CancellationToken ct = default)
    {
        Calls++;
        ct.ThrowIfCancellationRequested();
        var index = Anomalies.FindIndex(a => a.AnomalyId == anomalyId);
        if (index >= 0) Anomalies[index] = Anomalies[index] with { InvestigationId = investigationId };
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<KpiAnomaly>> GetRecentAsync(KpiPillar? pillar, string? domain, int top, string? profileKey = null, CancellationToken ct = default)
    {
        Calls++;
        return Task.FromResult<IReadOnlyList<KpiAnomaly>>(Anomalies.OrderByDescending(a => a.DetectedAt).Take(top).ToList());
    }
}

public sealed class A1InMemoryWatermarkStore : IIngestionWatermarkStore
{
    public Dictionary<int, DateTimeOffset> Watermarks { get; } = [];
    public int Calls { get; private set; }

    public Task<DateTimeOffset?> GetAsync(int kpiDefinitionId, CancellationToken ct = default)
    {
        Calls++;
        return Task.FromResult<DateTimeOffset?>(Watermarks.TryGetValue(kpiDefinitionId, out var w) ? w : null);
    }

    public Task SetAsync(int kpiDefinitionId, DateTimeOffset watermark, CancellationToken ct = default)
    {
        Calls++;
        Watermarks[kpiDefinitionId] = watermark;
        return Task.CompletedTask;
    }
}

public sealed class A1RecordingTriageSubmitter : ITriageSignalSubmitter
{
    public List<TriageSignal> Signals { get; } = [];
    public Guid NextInvestigationId { get; set; } = Guid.NewGuid();
    public bool Throw { get; set; }
    /// <summary>Runs before the submission is accepted (e.g. to cancel the caller's token mid-run).</summary>
    public Action? OnSubmit { get; set; }

    public Task<TriageSubmission> SubmitAsync(TriageSignal signal, CancellationToken ct = default)
    {
        Signals.Add(signal);
        OnSubmit?.Invoke();
        ct.ThrowIfCancellationRequested();
        if (Throw) throw new InvalidOperationException("triage queue unavailable");
        return Task.FromResult(new TriageSubmission(NextInvestigationId, false));
    }
}

/// <summary>Delegates to a real evaluator and records what it was asked to evaluate.</summary>
public sealed class A1SpyEvaluator(IKpiEvaluator inner) : IKpiEvaluator
{
    public List<(KpiDefinition Definition, KpiDetectionState State, IReadOnlyList<KpiBucket> History, IReadOnlyList<KpiBucket> Buckets)> Calls { get; } = [];

    public TimeSpan GetRequiredHistory(KpiDefinition definition) => inner.GetRequiredHistory(definition);

    public IReadOnlyList<KpiEvaluation> Evaluate(KpiDefinition definition, KpiDetectionState state, IReadOnlyList<KpiBucket> history, IReadOnlyList<KpiBucket> bucketsToEvaluate)
    {
        Calls.Add((definition, state, history, bucketsToEvaluate));
        return inner.Evaluate(definition, state, history, bucketsToEvaluate);
    }
}
