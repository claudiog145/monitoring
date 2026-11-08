using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Infrastructure.Sql;

namespace JourneyMonitor.Infrastructure.Monitoring;

/// <summary>
/// Process-local buckets keyed by (KpiDefinitionId, trimmed EntityKey, BucketStart instant); upsert replaces, last
/// one wins. EntityKey compares case-insensitively like the SQL column. Like usp_UpsertKpiBuckets, an
/// upsert carrying entity buckets purges the entity buckets of those definitions older than
/// <see cref="SqlKpiBucketStore.EntityRetention"/>; domain buckets are never purged.
/// </summary>
public sealed class InMemoryKpiBucketStore(TimeProvider? timeProvider = null) : IKpiBucketStore
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly Dictionary<(int, string), SortedDictionary<DateTimeOffset, KpiBucket>> _series = new(SeriesComparer.Instance);

    public Task UpsertAsync(IReadOnlyList<KpiBucket> buckets, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(buckets);
        KpiValueGuard.EnsureFinite(buckets, nameof(buckets));
        KpiValueGuard.EnsureEntityKeys(buckets.Select(b => b.EntityKey), nameof(buckets));
        lock (_gate)
        {
            foreach (var bucket in buckets.Select(b => b with { EntityKey = KpiValueGuard.NormalizeEntityKey(b.EntityKey) }))
            {
                var key = (bucket.KpiDefinitionId, bucket.EntityKey);
                if (!_series.TryGetValue(key, out var series))
                {
                    series = [];
                    _series[key] = series;
                }
                // DateTimeOffset equality/ordering is by UTC instant, matching the datetimeoffset primary key.
                series[bucket.BucketStart] = bucket;
            }

            var cutoff = _time.GetUtcNow() - SqlKpiBucketStore.EntityRetention;
            var purged = buckets.Where(b => KpiValueGuard.NormalizeEntityKey(b.EntityKey).Length > 0).Select(b => b.KpiDefinitionId).ToHashSet();
            foreach (var (key, series) in _series.Where(kv => kv.Key.Item2.Length > 0 && purged.Contains(kv.Key.Item1)).ToList())
            {
                foreach (var start in series.Keys.Where(s => s < cutoff).ToList())
                {
                    series.Remove(start);
                }
                if (series.Count == 0)
                {
                    _series.Remove(key);
                }
            }
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<KpiBucket>> GetRangeAsync(int kpiDefinitionId, DateTimeOffset fromInclusive, DateTimeOffset toExclusive, CancellationToken ct = default) =>
        GetRangeAsync(kpiDefinitionId, fromInclusive, toExclusive, null, ct);

    public Task<IReadOnlyList<KpiBucket>> GetRangeAsync(int kpiDefinitionId, DateTimeOffset fromInclusive, DateTimeOffset toExclusive, string? entityKey, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_series.TryGetValue((kpiDefinitionId, KpiValueGuard.NormalizeEntityKey(entityKey)), out var series))
            {
                return Task.FromResult<IReadOnlyList<KpiBucket>>([]);
            }
            return Task.FromResult<IReadOnlyList<KpiBucket>>(InRange(series, fromInclusive, toExclusive).ToList());
        }
    }

    public Task<IReadOnlyList<KpiBucket>> GetEntityRangeAsync(int kpiDefinitionId, DateTimeOffset fromInclusive, DateTimeOffset toExclusive, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<KpiBucket>>(_series
                .Where(kv => kv.Key.Item1 == kpiDefinitionId && kv.Key.Item2.Length > 0)
                .OrderBy(kv => kv.Key.Item2, StringComparer.OrdinalIgnoreCase)
                .SelectMany(kv => InRange(kv.Value, fromInclusive, toExclusive))
                .ToList());
        }
    }

    private static IEnumerable<KpiBucket> InRange(SortedDictionary<DateTimeOffset, KpiBucket> series, DateTimeOffset fromInclusive, DateTimeOffset toExclusive) =>
        series.Where(kv => kv.Key >= fromInclusive && kv.Key < toExclusive).Select(kv => kv.Value);

    private sealed class SeriesComparer : IEqualityComparer<(int, string)>
    {
        public static readonly SeriesComparer Instance = new();

        public bool Equals((int, string) x, (int, string) y) =>
            x.Item1 == y.Item1 && StringComparer.OrdinalIgnoreCase.Equals(x.Item2, y.Item2);

        public int GetHashCode((int, string) obj) =>
            HashCode.Combine(obj.Item1, StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Item2));
    }
}
