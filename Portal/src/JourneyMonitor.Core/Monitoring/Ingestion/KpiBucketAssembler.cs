using JourneyMonitor.Core.Domain;

namespace JourneyMonitor.Core.Monitoring.Ingestion;

public sealed record KpiIngestionBatch(IReadOnlyList<KpiBucket> Buckets, IReadOnlyList<KpiSample> Samples);

/// <summary>
/// Turns a raw <see cref="KpiSourceResult"/> into the buckets and samples to persist for one KPI:
/// keeps only closed buckets inside [from, to), merges duplicate BucketStart rows (the bucket store
/// MERGEs by primary key and cannot take the same key twice in one batch), caps samples per bucket
/// and computes the statistics with <see cref="KpiBucketStatistics.Build"/>.
/// </summary>
public static class KpiBucketAssembler
{
    public static KpiIngestionBatch Assemble(
        KpiDefinition definition,
        KpiSourceResult result,
        DateTimeOffset fromInclusive,
        DateTimeOffset toExclusive,
        int samplesPerBucket)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(result);

        bool InRange(DateTimeOffset start) => start >= fromInclusive && start < toExclusive;

        // Duplicate rows for one bucket are partial counts of the same interval, so the totals add up.
        // A rate cannot be added, so the first non-null Value wins.
        var sourceBuckets = (result.Buckets ?? [])
            .Where(b => InRange(b.BucketStart))
            .GroupBy(b => b.BucketStart.UtcTicks)
            .Select(g => new KpiSourceBucket(
                g.First().BucketStart.ToUniversalTime(),
                g.Sum(b => b.TotalCount),
                g.Select(b => b.Value).FirstOrDefault(v => v.HasValue)))
            .OrderBy(b => b.BucketStart)
            .ToList();

        var samplesByBucket = samplesPerBucket <= 0
            ? new Dictionary<long, List<KpiSample>>()
            : (result.Samples ?? [])
                .Where(s => InRange(s.BucketStart))
                .GroupBy(s => s.BucketStart.UtcTicks)
                .ToDictionary(g => g.Key, g => g.Take(samplesPerBucket).ToList());

        var buckets = new List<KpiBucket>(sourceBuckets.Count);
        var samples = new List<KpiSample>();
        foreach (var sourceBucket in sourceBuckets)
        {
            var bucketSamples = samplesByBucket.TryGetValue(sourceBucket.BucketStart.UtcTicks, out var found)
                ? found.Select(s => s with { KpiDefinitionId = definition.KpiDefinitionId, BucketStart = sourceBucket.BucketStart }).ToList()
                : [];
            buckets.Add(KpiBucketStatistics.Build(definition.KpiDefinitionId, sourceBucket, bucketSamples));
            samples.AddRange(bucketSamples);
        }

        return new KpiIngestionBatch(buckets, samples);
    }
}
