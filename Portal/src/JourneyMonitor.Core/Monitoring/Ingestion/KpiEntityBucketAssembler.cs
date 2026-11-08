using JourneyMonitor.Core.Domain;

namespace JourneyMonitor.Core.Monitoring.Ingestion;

/// <summary>Entity-grain buckets to persist for one KPI, plus how many source rows were dropped and why.</summary>
public sealed record KpiEntityIngestionBatch(IReadOnlyList<KpiBucket> Buckets, int InvalidEntityRows, int CappedEntityRows)
{
    public int Entities => Buckets.Select(b => b.EntityKey).Distinct(StringComparer.OrdinalIgnoreCase).Count();
}

/// <summary>
/// Contract v4 §1–§2: turns an entity-grain <see cref="KpiSourceResult"/> into one <see cref="KpiBucket"/> per
/// (entity, bucket) carrying the source's aggregated statistics. Entity grain never stores samples, so
/// <see cref="KpiBucket.SampledCount"/> is 0 and the source samples are ignored. Rows outside [from, to) and rows
/// whose EntityKey is empty, whitespace or longer than <see cref="MaxEntityKeyLength"/> are dropped (an empty key would
/// land on the domain aggregate row "").
/// Each bucket keeps at most <see cref="KpiDefinition.MaxEntitiesPerBucket"/> entities, highest TotalCount first.
/// </summary>
public static class KpiEntityBucketAssembler
{
    /// <summary>Size of the EntityKey column (contract v4 §1).</summary>
    public const int MaxEntityKeyLength = 256;

    public static bool IsValidEntityKey(string? entityKey)
    {
        var trimmed = entityKey?.Trim() ?? "";
        return trimmed.Length is > 0 and <= MaxEntityKeyLength;
    }

    public static KpiEntityIngestionBatch Assemble(
        KpiDefinition definition,
        KpiSourceResult result,
        DateTimeOffset fromInclusive,
        DateTimeOffset toExclusive)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(result);

        var inRange = (result.Buckets ?? [])
            .Where(b => b.BucketStart >= fromInclusive && b.BucketStart < toExclusive)
            .ToList();
        var keyed = inRange.Where(b => IsValidEntityKey(b.EntityKey)).ToList();
        var invalid = inRange.Count - keyed.Count;
        var cap = Math.Max(1, definition.MaxEntitiesPerBucket);

        var buckets = new List<KpiBucket>();
        var capped = 0;
        foreach (var bucket in keyed.GroupBy(b => b.BucketStart.UtcTicks).OrderBy(g => g.Key))
        {
            var bucketStart = new DateTimeOffset(bucket.Key, TimeSpan.Zero);

            // The SQL key is case-insensitive, so "ABC" and "abc" are the same series and must be merged before the MERGE.
            var entities = bucket
                .GroupBy(b => b.EntityKey.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(g => Merge(definition.KpiDefinitionId, bucketStart, g.ToList()))
                .OrderByDescending(b => b.TotalCount)
                .ThenBy(b => b.EntityKey, StringComparer.Ordinal)
                .ToList();

            capped += Math.Max(0, entities.Count - cap);
            buckets.AddRange(entities.Take(cap));
        }

        return new KpiEntityIngestionBatch(buckets, invalid, capped);
    }

    /// <summary>
    /// Duplicate rows of one (entity, bucket) are partial counts of the same interval: totals and failures add up.
    /// Percentiles and rates (Value) cannot be combined, so they come from the row with the most traffic; the mean is weighted by count.
    /// </summary>
    private static KpiBucket Merge(int kpiDefinitionId, DateTimeOffset bucketStart, IReadOnlyList<KpiSourceBucket> rows)
    {
        var main = rows.OrderByDescending(r => r.TotalCount).First();
        var total = rows.Sum(r => r.TotalCount);
        var failed = rows.Any(r => r.FailedCount.HasValue) ? rows.Sum(r => r.FailedCount ?? 0) : (long?)null;

        var mean = main.Mean;
        if (rows.Count > 1 && rows.All(r => r.Mean.HasValue) && total > 0)
        {
            mean = rows.Sum(r => r.Mean!.Value * r.TotalCount) / total;
        }

        return new KpiBucket(
            kpiDefinitionId,
            bucketStart,
            total,
            0,
            Finite(main.P50),
            Finite(main.P95),
            Finite(mean),
            null,
            Finite(main.Value))
        {
            EntityKey = main.EntityKey.Trim(),
            FailedCount = failed,
        };
    }

    // The bucket store rejects NaN/Infinity; a statistic the source could not compute is simply missing.
    private static double? Finite(double? value) => value is { } v && double.IsFinite(v) ? v : null;
}
