using JourneyMonitor.Core.Domain;

namespace JourneyMonitor.Core.Monitoring.Kpi.Methods;

internal static class RobustZScoreMethod
{
    public const double MadScale = 1.4826;

    /// <param name="previous">Earlier buckets (history plus buckets already evaluated in this call).</param>
    public static (double? reference, double? deviation, string? notEvaluatedReason) Compute(
        double observed,
        DateTimeOffset bucketStart,
        TimeSpan bucketSize,
        IEnumerable<KpiBucket> previous,
        KpiAggregation aggregation,
        int windowBuckets)
    {
        var windowStart = bucketStart - TimeSpan.FromTicks(bucketSize.Ticks * windowBuckets);
        var values = previous
            .Where(b => b.BucketStart >= windowStart && b.BucketStart < bucketStart)
            .GroupBy(b => b.BucketStart)
            .Select(g => KpiStatistics.Observed(g.Last(), aggregation))
            .Where(v => v.HasValue)
            .Select(v => v!.Value)
            .ToList();

        var required = (windowBuckets + 1) / 2;
        if (values.Count < required)
            return (null, null, $"Insufficient window: {values.Count} of {required} required buckets in the previous {windowBuckets}.");

        var median = KpiStatistics.Median(values);
        var mad = KpiStatistics.Median(values.Select(v => Math.Abs(v - median)));
        var scale = MadScale * mad;
        if (scale == 0)
            return (median, null, "MAD is 0 (flat window); robust z-score is undefined.");

        return (median, (observed - median) / scale, null);
    }
}
