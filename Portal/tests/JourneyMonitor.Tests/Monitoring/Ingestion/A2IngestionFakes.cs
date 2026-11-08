using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;

namespace JourneyMonitor.Tests.Spec1000.A2;

internal static class A2Kpi
{
    public static KpiDefinition Definition(
        int id = 1,
        string sourceKey = "fake:latency",
        string domain = "hotels",
        string stage = "",
        TimeSpan? bucketSize = null,
        TimeSpan? lookback = null,
        int sampleSize = 4000,
        bool enabled = true) =>
        new(
            id,
            KpiPillar.Application,
            "Latency",
            domain,
            stage,
            sourceKey,
            KpiDirection.Increase,
            KpiAggregation.P95,
            TimeSpan.FromMinutes(5),
            bucketSize ?? TimeSpan.FromHours(1),
            lookback ?? TimeSpan.FromHours(24),
            sampleSize,
            30,
            DetectionMethodKind.BaselinePercent,
            """{"lookbackDays":7,"thresholdPercent":50}""",
            2,
            120,
            enabled,
            DateTimeOffset.UnixEpoch,
            "test");
}

internal sealed class A2FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

/// <summary>KpiIngestionService uses a static run lock, so its tests must not run in parallel with each other.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class A2IngestionRunLockCollection
{
    public const string Name = "A2 KPI ingestion run lock";
}

/// <summary>IKpiSource whose first fetch blocks until <see cref="Release"/> is called.</summary>
internal sealed class A2BlockingKpiSource : IKpiSource
{
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int Calls;

    public Task Entered => _entered.Task;

    public void Release() => _release.TrySetResult();

    public bool CanHandle(string sourceKey) => true;

    public async Task<KpiSourceResult> FetchAsync(KpiDefinition definition, DateTimeOffset fromInclusive, DateTimeOffset toExclusive, int samplesPerBucket, CancellationToken ct = default)
    {
        Interlocked.Increment(ref Calls);
        _entered.TrySetResult();
        await _release.Task.WaitAsync(ct);
        return new KpiSourceResult([], []);
    }
}

internal sealed class A2DefinitionStore(params KpiDefinition[] definitions) : IKpiDefinitionStore
{
    public Task<IReadOnlyList<KpiDefinition>> GetAllAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<KpiDefinition>>(definitions);

    public Task<IReadOnlyList<KpiDefinition>> GetEnabledAsync(KpiPillar? pillar, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<KpiDefinition>>(definitions
            .Where(d => d.Enabled && (pillar is null || d.Pillar == pillar))
            .ToList());

    public Task<KpiDefinition?> GetAsync(int kpiDefinitionId, CancellationToken ct = default) =>
        Task.FromResult(definitions.FirstOrDefault(d => d.KpiDefinitionId == kpiDefinitionId));

    public Task<KpiDefinition> UpsertAsync(KpiDefinition definition, string changedBy, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<KpiDefinitionAuditEntry>> GetAuditAsync(int kpiDefinitionId, int top, CancellationToken ct = default) =>
        throw new NotSupportedException();
}

internal sealed record A2FetchCall(int KpiDefinitionId, DateTimeOffset From, DateTimeOffset To, int SamplesPerBucket);

/// <summary>Scripted IKpiSource: returns <see cref="Respond"/>'s result (or throws) and records every call.</summary>
internal sealed class A2KpiSource(string handles = "fake:latency") : IKpiSource
{
    public List<A2FetchCall> Calls { get; } = [];

    public Func<KpiDefinition, DateTimeOffset, DateTimeOffset, int, KpiSourceResult> Respond { get; set; } =
        (_, _, _, _) => new KpiSourceResult([], []);

    public bool CanHandle(string sourceKey) => string.Equals(sourceKey, handles, StringComparison.OrdinalIgnoreCase);

    public Task<KpiSourceResult> FetchAsync(KpiDefinition definition, DateTimeOffset fromInclusive, DateTimeOffset toExclusive, int samplesPerBucket, CancellationToken ct = default)
    {
        Calls.Add(new A2FetchCall(definition.KpiDefinitionId, fromInclusive, toExclusive, samplesPerBucket));
        return Task.FromResult(Respond(definition, fromInclusive, toExclusive, samplesPerBucket));
    }

    /// <summary>One bucket per hour in [from, to) with <paramref name="samplesAvailable"/> samples each, capped at samplesPerBucket.</summary>
    public static KpiSourceResult Hourly(KpiDefinition definition, DateTimeOffset from, DateTimeOffset to, int samplesPerBucket, int samplesAvailable)
    {
        var buckets = new List<KpiSourceBucket>();
        var samples = new List<KpiSample>();
        for (var start = from; start < to; start += definition.BucketSize)
        {
            buckets.Add(new KpiSourceBucket(start, samplesAvailable * 10L, null));
            for (var i = 0; i < Math.Min(samplesAvailable, samplesPerBucket); i++)
            {
                samples.Add(new KpiSample(0, definition.KpiDefinitionId, start, start.AddSeconds(i), 100 + i, $"op-{i}"));
            }
        }
        return new KpiSourceResult(buckets, samples);
    }
}

internal sealed class A2BucketStore : IKpiBucketStore
{
    public List<IReadOnlyList<KpiBucket>> Batches { get; } = [];

    public IEnumerable<KpiBucket> All => Batches.SelectMany(b => b);

    public Task UpsertAsync(IReadOnlyList<KpiBucket> buckets, CancellationToken ct = default)
    {
        Batches.Add(buckets.ToList());
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<KpiBucket>> GetRangeAsync(int kpiDefinitionId, DateTimeOffset fromInclusive, DateTimeOffset toExclusive, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<KpiBucket>>(All
            .Where(b => b.KpiDefinitionId == kpiDefinitionId && b.EntityKey.Length == 0 && b.BucketStart >= fromInclusive && b.BucketStart < toExclusive)
            .OrderBy(b => b.BucketStart)
            .ToList());

    public Task<IReadOnlyList<KpiBucket>> GetEntityRangeAsync(int kpiDefinitionId, DateTimeOffset fromInclusive, DateTimeOffset toExclusive, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<KpiBucket>>(All
            .Where(b => b.KpiDefinitionId == kpiDefinitionId && b.EntityKey.Length > 0 && b.BucketStart >= fromInclusive && b.BucketStart < toExclusive)
            .OrderBy(b => b.EntityKey, StringComparer.Ordinal)
            .ThenBy(b => b.BucketStart)
            .ToList());
}

internal sealed class A2SampleStore : IKpiSampleStore
{
    public List<KpiSample> Samples { get; } = [];
    public List<DateTimeOffset> PurgeCutoffs { get; } = [];

    public Task AddAsync(IReadOnlyList<KpiSample> samples, CancellationToken ct = default)
    {
        Samples.AddRange(samples);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<KpiSample>> GetAsync(int kpiDefinitionId, DateTimeOffset bucketStart, int top, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<KpiSample>>(Samples
            .Where(s => s.KpiDefinitionId == kpiDefinitionId && s.BucketStart == bucketStart)
            .Take(top)
            .ToList());

    public Task<int> PurgeOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        PurgeCutoffs.Add(cutoff);
        return Task.FromResult(Samples.RemoveAll(s => s.BucketStart < cutoff));
    }
}

internal sealed class A2WatermarkStore : IIngestionWatermarkStore
{
    public Dictionary<int, DateTimeOffset> Values { get; } = [];
    public List<(int Id, DateTimeOffset Watermark)> SetCalls { get; } = [];

    public Task<DateTimeOffset?> GetAsync(int kpiDefinitionId, CancellationToken ct = default) =>
        Task.FromResult<DateTimeOffset?>(Values.TryGetValue(kpiDefinitionId, out var value) ? value : null);

    public Task SetAsync(int kpiDefinitionId, DateTimeOffset watermark, CancellationToken ct = default)
    {
        SetCalls.Add((kpiDefinitionId, watermark));
        Values[kpiDefinitionId] = watermark;
        return Task.CompletedTask;
    }
}
