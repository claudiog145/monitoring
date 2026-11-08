using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Infrastructure.Monitoring;

namespace JourneyMonitor.Tests.Spec1000.A6;

/// <summary>Counts every write call; reads go to the in-memory store, which the test seeds directly.</summary>
public sealed class A6WriteLog
{
    private readonly List<string> _writes = [];

    public IReadOnlyList<string> Writes
    {
        get { lock (_writes) return _writes.ToList(); }
    }

    public void Record(string call)
    {
        lock (_writes) _writes.Add(call);
    }
}

public sealed class A6SpyKpiDefinitionStore(IKpiDefinitionStore inner, A6WriteLog log) : IKpiDefinitionStore
{
    public Task<IReadOnlyList<KpiDefinition>> GetAllAsync(CancellationToken ct = default) => inner.GetAllAsync(ct);
    public Task<IReadOnlyList<KpiDefinition>> GetEnabledAsync(KpiPillar? pillar, CancellationToken ct = default) => inner.GetEnabledAsync(pillar, ct);
    public Task<KpiDefinition?> GetAsync(int kpiDefinitionId, CancellationToken ct = default) => inner.GetAsync(kpiDefinitionId, ct);

    public Task<KpiDefinition> UpsertAsync(KpiDefinition definition, string changedBy, CancellationToken ct = default)
    {
        log.Record("KpiDefinition.Upsert");
        return inner.UpsertAsync(definition, changedBy, ct);
    }

    public Task<IReadOnlyList<KpiDefinitionAuditEntry>> GetAuditAsync(int kpiDefinitionId, int top, CancellationToken ct = default)
        => inner.GetAuditAsync(kpiDefinitionId, top, ct);
}

public sealed class A6SpyKpiBucketStore(IKpiBucketStore inner, A6WriteLog log) : IKpiBucketStore
{
    public Task UpsertAsync(IReadOnlyList<KpiBucket> buckets, CancellationToken ct = default)
    {
        log.Record("KpiBucket.Upsert");
        return inner.UpsertAsync(buckets, ct);
    }

    public Task<IReadOnlyList<KpiBucket>> GetRangeAsync(int kpiDefinitionId, DateTimeOffset fromInclusive, DateTimeOffset toExclusive, CancellationToken ct = default)
        => inner.GetRangeAsync(kpiDefinitionId, fromInclusive, toExclusive, ct);
}

public sealed class A6SpyKpiSampleStore(IKpiSampleStore inner, A6WriteLog log) : IKpiSampleStore
{
    public Task AddAsync(IReadOnlyList<KpiSample> samples, CancellationToken ct = default)
    {
        log.Record("KpiSample.Add");
        return inner.AddAsync(samples, ct);
    }

    public Task<IReadOnlyList<KpiSample>> GetAsync(int kpiDefinitionId, DateTimeOffset bucketStart, int top, CancellationToken ct = default)
        => inner.GetAsync(kpiDefinitionId, bucketStart, top, ct);

    public Task<int> PurgeOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        log.Record("KpiSample.Purge");
        return inner.PurgeOlderThanAsync(cutoff, ct);
    }
}

public sealed class A6SpyKpiStateStore(IKpiStateStore inner, A6WriteLog log) : IKpiStateStore
{
    public Task<KpiDetectionState> GetAsync(int kpiDefinitionId, CancellationToken ct = default) => inner.GetAsync(kpiDefinitionId, ct);

    public Task SaveAsync(KpiDetectionState state, CancellationToken ct = default)
    {
        log.Record("KpiState.Save");
        return inner.SaveAsync(state, ct);
    }
}

public sealed class A6SpyKpiAnomalyStore(IKpiAnomalyStore inner, A6WriteLog log) : IKpiAnomalyStore
{
    public Task<KpiAnomaly> AddAsync(KpiAnomaly anomaly, CancellationToken ct = default)
    {
        log.Record("KpiAnomaly.Add");
        return inner.AddAsync(anomaly, ct);
    }

    public Task AttachInvestigationAsync(long anomalyId, Guid investigationId, CancellationToken ct = default)
    {
        log.Record("KpiAnomaly.AttachInvestigation");
        return inner.AttachInvestigationAsync(anomalyId, investigationId, ct);
    }

    public Task<IReadOnlyList<KpiAnomaly>> GetRecentAsync(KpiPillar? pillar, string? domain, int top, string? profileKey = null, CancellationToken ct = default)
        => inner.GetRecentAsync(pillar, domain, top, profileKey, ct);
}

public sealed class A6SpyIngestionWatermarkStore(IIngestionWatermarkStore inner, A6WriteLog log) : IIngestionWatermarkStore
{
    public Task<DateTimeOffset?> GetAsync(int kpiDefinitionId, CancellationToken ct = default) => inner.GetAsync(kpiDefinitionId, ct);

    public Task SetAsync(int kpiDefinitionId, DateTimeOffset watermark, CancellationToken ct = default)
    {
        log.Record("Watermark.Set");
        return inner.SetAsync(kpiDefinitionId, watermark, ct);
    }
}

public sealed class A6FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

/// <summary>In-memory stores seeded directly, exposed to the console only through write-logging spies.</summary>
public sealed class A6SpiedStores
{
    public A6WriteLog Log { get; } = new();
    public InMemoryKpiDefinitionStore Definitions { get; } = new();
    public InMemoryKpiBucketStore Buckets { get; } = new();
    public InMemoryKpiSampleStore Samples { get; } = new();
    public InMemoryKpiStateStore States { get; } = new();
    public InMemoryIngestionWatermarkStore Watermarks { get; } = new();
    public InMemoryKpiAnomalyStore Anomalies { get; }

    public A6SpiedStores() => Anomalies = new InMemoryKpiAnomalyStore(Definitions);

    public IServiceProvider BuildServices(DateTimeOffset now)
    {
        var services = new A6ServiceProvider();
        services.Add<IKpiDefinitionStore>(new A6SpyKpiDefinitionStore(Definitions, Log));
        services.Add<IKpiBucketStore>(new A6SpyKpiBucketStore(Buckets, Log));
        services.Add<IKpiSampleStore>(new A6SpyKpiSampleStore(Samples, Log));
        services.Add<IKpiStateStore>(new A6SpyKpiStateStore(States, Log));
        services.Add<IKpiAnomalyStore>(new A6SpyKpiAnomalyStore(Anomalies, Log));
        services.Add<IIngestionWatermarkStore>(new A6SpyIngestionWatermarkStore(Watermarks, Log));
        services.Add<TimeProvider>(new A6FixedTimeProvider(now));
        return services;
    }
}

public sealed class A6ServiceProvider : IServiceProvider
{
    private readonly Dictionary<Type, object> _services = [];

    public void Add<T>(T instance) where T : notnull => _services[typeof(T)] = instance;

    public object? GetService(Type serviceType) => _services.GetValueOrDefault(serviceType);
}
