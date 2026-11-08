using System.Text;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring.Inspector;
using JourneyMonitor.Tests.Functions;

namespace JourneyMonitor.Tests.Spec1000.A4;

internal sealed class A4FakeTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

internal sealed class A4FakeInvestigationStore : ITriageInvestigationStore
{
    public Dictionary<Guid, TriageInvestigation> Items { get; } = [];
    public List<(Guid Id, TriageInvestigationStatus Status, string? Error)> StatusUpdates { get; } = [];

    /// <summary>When set, the next CreateAsync simulates losing the race: this row appears first and the insert throws.</summary>
    public TriageInvestigation? ConcurrentWinnerOnCreate { get; set; }

    /// <summary>When true, CreateAsync throws without any competing row (a genuine store failure).</summary>
    public bool ThrowOnCreate { get; set; }

    /// <summary>When true, CreateAsync stores the row and then throws (ambiguous commit, e.g. timeout after commit).</summary>
    public bool ThrowAfterCommitOnCreate { get; set; }

    /// <summary>When true, FindOpenByDedupKeyAsync throws on every call after the first one.</summary>
    public bool ThrowOnRequery { get; set; }

    private int _findCalls;

    public Task CreateAsync(TriageInvestigation investigation, CancellationToken ct = default)
    {
        if (ConcurrentWinnerOnCreate is { } winner)
        {
            ConcurrentWinnerOnCreate = null;
            Items[winner.InvestigationId] = winner;
            throw new InvalidOperationException("unique index UX_monitoring_TriageInvestigation_OpenDedup violated");
        }
        if (ThrowOnCreate) throw new InvalidOperationException("store down");
        Items[investigation.InvestigationId] = investigation;
        if (ThrowAfterCommitOnCreate) throw new TimeoutException("timeout after commit");
        return Task.CompletedTask;
    }

    public Task<TriageInvestigation?> GetAsync(Guid investigationId, CancellationToken ct = default) =>
        Task.FromResult(Items.TryGetValue(investigationId, out var item) ? item : null);

    public Task<TriageInvestigation?> FindOpenByDedupKeyAsync(string dedupKey, CancellationToken ct = default)
    {
        if (_findCalls++ > 0 && ThrowOnRequery) throw new InvalidOperationException("requery failed");
        return Task.FromResult(Items.Values.FirstOrDefault(i =>
            i.DedupKey == dedupKey && i.Status is TriageInvestigationStatus.Queued or TriageInvestigationStatus.Running));
    }

    public Task UpdateStatusAsync(Guid investigationId, TriageInvestigationStatus status, string? error, CancellationToken ct = default)
    {
        StatusUpdates.Add((investigationId, status, error));
        if (Items.TryGetValue(investigationId, out var item)) Items[investigationId] = item with { Status = status, Error = error };
        return Task.CompletedTask;
    }

    public Task CompleteAsync(Guid investigationId, TriageResult result, bool notified, CancellationToken ct = default)
    {
        if (Items.TryGetValue(investigationId, out var item))
            Items[investigationId] = item with { Status = TriageInvestigationStatus.Completed, Result = result, Notified = notified };
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TriageInvestigation>> GetRecentAsync(int top, string? profileKey = null, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<TriageInvestigation>>(Items.Values
            .Where(i => profileKey is null || string.Equals(TriageMonkeyService.ProfileKeyOf(i.Signal), profileKey, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(i => i.CreatedAt).Take(top).ToList());
}

internal sealed class A4FakeWorkQueue : IAgentWorkQueue
{
    public List<AgentWorkItem> Enqueued { get; } = [];
    public bool ThrowOnEnqueue { get; set; }

    public Task<bool> EnqueueAsync(AgentWorkItem item, CancellationToken ct = default)
    {
        if (ThrowOnEnqueue) throw new InvalidOperationException("queue down");
        if (Enqueued.Any(e => e.DedupKey == item.DedupKey)) return Task.FromResult(false);
        Enqueued.Add(item);
        return Task.FromResult(true);
    }

    public Task<AgentWorkItem?> TryLeaseAsync(string payloadType, string leasedBy, TimeSpan leaseDuration, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task CompleteAsync(Guid itemId, string leasedBy, CancellationToken ct = default) => throw new NotSupportedException();

    public Task FailAsync(Guid itemId, string leasedBy, string error, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<IReadOnlyList<AgentWorkItem>> GetQueuedAsync(string payloadType, int top, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<AgentWorkQueueStats> GetStatsAsync(string payloadType, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<int> DiscardQueuedAsync(string payloadType, IReadOnlyCollection<Guid>? itemIds, string reason, string discardedBy, CancellationToken ct = default) =>
        throw new NotSupportedException();
}

internal sealed class A4FakeKpiDefinitionStore : IKpiDefinitionStore
{
    public List<KpiDefinition> Definitions { get; } = [];
    public List<(KpiDefinition Definition, string ChangedBy)> Upserts { get; } = [];

    public Task<IReadOnlyList<KpiDefinition>> GetAllAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<KpiDefinition>>(Definitions.ToList());

    public Task<IReadOnlyList<KpiDefinition>> GetEnabledAsync(KpiPillar? pillar, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<KpiDefinition>>(Definitions.Where(d => d.Enabled && (pillar is null || d.Pillar == pillar)).ToList());

    public Task<KpiDefinition?> GetAsync(int kpiDefinitionId, CancellationToken ct = default) =>
        Task.FromResult(Definitions.FirstOrDefault(d => d.KpiDefinitionId == kpiDefinitionId));

    public Task<KpiDefinition> UpsertAsync(KpiDefinition definition, string changedBy, CancellationToken ct = default)
    {
        Upserts.Add((definition, changedBy));
        var stored = definition.KpiDefinitionId == 0
            ? definition with { KpiDefinitionId = Definitions.Count == 0 ? 1 : Definitions.Max(d => d.KpiDefinitionId) + 1 }
            : definition;
        Definitions.RemoveAll(d => d.KpiDefinitionId == stored.KpiDefinitionId);
        Definitions.Add(stored);
        return Task.FromResult(stored);
    }

    public Task<IReadOnlyList<KpiDefinitionAuditEntry>> GetAuditAsync(int kpiDefinitionId, int top, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<KpiDefinitionAuditEntry>>([]);
}

internal sealed class A4FakeKpiBucketStore : IKpiBucketStore
{
    public List<KpiBucket> Buckets { get; } = [];

    public Task UpsertAsync(IReadOnlyList<KpiBucket> buckets, CancellationToken ct = default)
    {
        Buckets.AddRange(buckets);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<KpiBucket>> GetRangeAsync(int kpiDefinitionId, DateTimeOffset fromInclusive, DateTimeOffset toExclusive, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<KpiBucket>>(Buckets
            .Where(b => b.KpiDefinitionId == kpiDefinitionId && b.BucketStart >= fromInclusive && b.BucketStart < toExclusive)
            .OrderBy(b => b.BucketStart)
            .ToList());
}

internal sealed class A4FakeKpiStateStore : IKpiStateStore
{
    public Dictionary<int, KpiDetectionState> States { get; } = [];

    public Task<KpiDetectionState> GetAsync(int kpiDefinitionId, CancellationToken ct = default) =>
        Task.FromResult(States.TryGetValue(kpiDefinitionId, out var state) ? state : KpiDetectionState.Initial(kpiDefinitionId));

    public Task SaveAsync(KpiDetectionState state, CancellationToken ct = default)
    {
        States[state.KpiDefinitionId] = state;
        return Task.CompletedTask;
    }
}

/// <summary>Ignores the pillar/domain filters: the reader under test must match anomalies to its own KPIs.</summary>
internal sealed class A4FakeKpiAnomalyStore : IKpiAnomalyStore
{
    public List<KpiAnomaly> Anomalies { get; } = [];

    public Task<KpiAnomaly> AddAsync(KpiAnomaly anomaly, CancellationToken ct = default)
    {
        var stored = anomaly with { AnomalyId = Anomalies.Count + 1 };
        Anomalies.Add(stored);
        return Task.FromResult(stored);
    }

    public Task AttachInvestigationAsync(long anomalyId, Guid investigationId, CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<KpiAnomaly>> GetRecentAsync(KpiPillar? pillar, string? domain, int top, string? profileKey = null, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<KpiAnomaly>>(Anomalies.OrderByDescending(a => a.DetectedAt).Take(top).ToList());
}

internal sealed class A4FakeIngestionRunner : IKpiIngestionRunner
{
    public int Calls { get; private set; }
    public KpiIngestionRunResult Result { get; set; } = new(2, 5, 7, 1);

    public Task<KpiIngestionRunResult> RunAsync(CancellationToken ct = default)
    {
        Calls++;
        return Task.FromResult(Result);
    }
}

internal sealed class A4FakePillarMonitor : IPillarHealthMonitor
{
    public List<KpiPillar> Calls { get; } = [];
    public PillarMonitorRunResult Result { get; set; } = new(3, 1, 0, 2);

    public Task<PillarMonitorRunResult> RunAsync(KpiPillar pillar, CancellationToken ct = default)
    {
        Calls.Add(pillar);
        return Task.FromResult(Result);
    }
}

internal static class A4Http
{
    public const string InternalSecret = "a4-test-internal-secret";
    public const string WriteSecret = "a4-test-write-secret";

    public static FakeHttpRequestData Request(string url, string? body = null, string? internalSecret = null, string? writeSecret = null)
    {
        var req = new FakeHttpRequestData(url);
        if (body is not null)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            req.Body.Write(bytes, 0, bytes.Length);
            req.Body.Position = 0;
        }
        if (internalSecret is not null) req.WithHeader("X-Internal-Api-Secret", internalSecret);
        if (writeSecret is not null) req.WithHeader("X-Settings-Write-Secret", writeSecret);
        return req;
    }

    public static KpiDefinition Definition(int id, KpiPillar pillar, string domain = "hotels", bool enabled = true) => new(
        id, pillar, "Latency", domain, "", "appinsights:latency",
        KpiDirection.Increase, KpiAggregation.P95,
        TimeSpan.FromMinutes(5), TimeSpan.FromHours(1), TimeSpan.FromDays(1),
        4000, 30, DetectionMethodKind.BaselinePercent, "{\"lookbackDays\":7,\"thresholdPercent\":50}",
        2, 120, enabled, DateTimeOffset.UnixEpoch, "seed");
}
