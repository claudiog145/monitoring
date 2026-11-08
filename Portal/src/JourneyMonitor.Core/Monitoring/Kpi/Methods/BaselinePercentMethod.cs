using JourneyMonitor.Core.Domain;

namespace JourneyMonitor.Core.Monitoring.Kpi.Methods;

internal static class BaselinePercentMethod
{
    public static (double? reference, double? deviation, string? notEvaluatedReason) Compute(
        double observed,
        DateTimeOffset bucketStart,
        IReadOnlyList<KpiBucket> history,
        KpiAggregation aggregation,
        int lookbackDays)
    {
        var slots = new HashSet<DateTimeOffset>();
        for (var k = 1; k <= lookbackDays; k++) slots.Add(bucketStart.AddDays(-k));

        var values = history
            .Where(b => slots.Contains(b.BucketStart))
            .Select(b => KpiStatistics.Observed(b, aggregation))
            .Where(v => v.HasValue)
            .Select(v => v!.Value)
            .ToList();

        var required = Math.Min(3, lookbackDays);
        if (values.Count < required)
            return (null, null, $"Insufficient baseline history: {values.Count} of {required} required same-slot buckets in the previous {lookbackDays} days.");

        var reference = KpiStatistics.Median(values);
        if (reference <= 0)
            return (reference, null, "Baseline reference is <= 0; percent deviation is undefined.");

        return (reference, (observed - reference) / reference * 100.0, null);
    }
}
