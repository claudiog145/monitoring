using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;

namespace JourneyMonitor.HealthConsole;

/// <summary>
/// Decorators that let the console read real stores while guaranteeing it never writes:
/// every write method throws instead of reaching the inner store.
/// </summary>
internal static class ReadOnlyGuard
{
    public static InvalidOperationException Write(string store, string method) =>
        new($"HealthConsole is read-only: {store}.{method} is not allowed.");
}

public sealed class ReadOnlyKpiDefinitionStore(IKpiDefinitionStore inner) : IKpiDefinitionStore
{
    public Task<IReadOnlyList<KpiDefinition>> GetAllAsync(CancellationToken ct) => inner.GetAllAsync(ct);

    public Task<IReadOnlyList<KpiDefinition>> GetAllAsync(string? profileKey = null, CancellationToken ct = default) => inner.GetAllAsync(profileKey, ct);

    public Task<IReadOnlyList<KpiDefinition>> GetEnabledAsync(KpiPillar? pillar, CancellationToken ct) => inner.GetEnabledAsync(pillar, ct);

    public Task<IReadOnlyList<KpiDefinition>> GetEnabledAsync(KpiPillar? pillar, string? profileKey = null, CancellationToken ct = default) => inner.GetEnabledAsync(pillar, profileKey, ct);

    public Task<KpiDefinition?> GetAsync(int kpiDefinitionId, CancellationToken ct = default) => inner.GetAsync(kpiDefinitionId, ct);

    public Task<KpiDefinition> UpsertAsync(KpiDefinition definition, string changedBy, CancellationToken ct = default)
        => throw ReadOnlyGuard.Write(nameof(IKpiDefinitionStore), nameof(UpsertAsync));

    public Task<IReadOnlyList<KpiDefinitionAuditEntry>> GetAuditAsync(int kpiDefinitionId, int top, CancellationToken ct = default)
        => inner.GetAuditAsync(kpiDefinitionId, top, ct);
}

public sealed class ReadOnlyKpiBucketStore(IKpiBucketStore inner) : IKpiBucketStore
{
    public Task UpsertAsync(IReadOnlyList<KpiBucket> buckets, CancellationToken ct = default)
        => throw ReadOnlyGuard.Write(nameof(IKpiBucketStore), nameof(UpsertAsync));

    public Task<IReadOnlyList<KpiBucket>> GetRangeAsync(int kpiDefinitionId, DateTimeOffset fromInclusive, DateTimeOffset toExclusive, CancellationToken ct = default)
        => inner.GetRangeAsync(kpiDefinitionId, fromInclusive, toExclusive, ct);
}

public sealed class ReadOnlyKpiStateStore(IKpiStateStore inner) : IKpiStateStore
{
    public Task<KpiDetectionState> GetAsync(int kpiDefinitionId, CancellationToken ct = default) => inner.GetAsync(kpiDefinitionId, ct);

    public Task SaveAsync(KpiDetectionState state, CancellationToken ct = default)
        => throw ReadOnlyGuard.Write(nameof(IKpiStateStore), nameof(SaveAsync));
}

public sealed class ReadOnlyDaemonActivityLog(IDaemonActivityLog inner) : IDaemonActivityLog
{
    public Task AppendAsync(IReadOnlyList<DaemonActivityEntry> entries, CancellationToken ct = default)
        => throw ReadOnlyGuard.Write(nameof(IDaemonActivityLog), nameof(AppendAsync));

    public Task<DaemonActivityPage> GetAsync(long afterId, string? daemonKey, DaemonActivityLevel? minLevel, int top, CancellationToken ct = default)
        => inner.GetAsync(afterId, daemonKey, minLevel, top, ct);

    public Task<int> PurgeOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default)
        => throw ReadOnlyGuard.Write(nameof(IDaemonActivityLog), nameof(PurgeOlderThanAsync));
}

public sealed class ReadOnlyMonkeyRunTracker(IMonkeyRunTracker inner) : IMonkeyRunTracker
{
    public Task RecordRunAsync(string monkeyKey, DateTimeOffset at, CancellationToken ct = default)
        => throw ReadOnlyGuard.Write(nameof(IMonkeyRunTracker), nameof(RecordRunAsync));

    public Task<DateTimeOffset?> GetLastRunAsync(string monkeyKey, CancellationToken ct = default) => inner.GetLastRunAsync(monkeyKey, ct);
}

public sealed class ReadOnlyIngestionWatermarkStore(IIngestionWatermarkStore inner) : IIngestionWatermarkStore
{
    public Task<DateTimeOffset?> GetAsync(int kpiDefinitionId, CancellationToken ct = default) => inner.GetAsync(kpiDefinitionId, ct);

    public Task SetAsync(int kpiDefinitionId, DateTimeOffset watermark, CancellationToken ct = default)
        => throw ReadOnlyGuard.Write(nameof(IIngestionWatermarkStore), nameof(SetAsync));
}
