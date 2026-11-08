using JourneyMonitor.Core.Domain;

namespace JourneyMonitor.Core.Monitoring.Ingestion;

/// <summary>Bucket statistics computed from the sampled values of one bucket.</summary>
public static class KpiBucketStatistics
{
    /// <summary>Excel PERCENTILE.INC: linear interpolation over the sorted values. Empty list returns null.</summary>
    public static double? Percentile(IReadOnlyList<double> values, double p)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (double.IsNaN(p) || p < 0 || p > 1) throw new ArgumentOutOfRangeException(nameof(p), "p must be in [0, 1].");
        if (values.Count == 0) return null;

        var sorted = values.ToArray();
        Array.Sort(sorted);
        return PercentileOfSorted(sorted, p);
    }

    public static KpiBucket Build(int kpiDefinitionId, KpiSourceBucket sourceBucket, IReadOnlyList<KpiSample> samples)
    {
        ArgumentNullException.ThrowIfNull(sourceBucket);
        ArgumentNullException.ThrowIfNull(samples);

        if (samples.Count == 0)
        {
            return new KpiBucket(kpiDefinitionId, sourceBucket.BucketStart, sourceBucket.TotalCount, 0,
                null, null, null, null, sourceBucket.Value);
        }

        var sorted = samples.Select(s => s.Value).ToArray();
        Array.Sort(sorted);
        var mean = sorted.Average();
        var variance = sorted.Sum(v => (v - mean) * (v - mean)) / sorted.Length;

        return new KpiBucket(
            kpiDefinitionId,
            sourceBucket.BucketStart,
            sourceBucket.TotalCount,
            samples.Count,
            PercentileOfSorted(sorted, 0.5),
            PercentileOfSorted(sorted, 0.95),
            mean,
            Math.Sqrt(variance),
            sourceBucket.Value);
    }

    private static double PercentileOfSorted(double[] sorted, double p)
    {
        var rank = p * (sorted.Length - 1);
        var lower = (int)Math.Floor(rank);
        var upper = (int)Math.Ceiling(rank);
        return sorted[lower] + (rank - lower) * (sorted[upper] - sorted[lower]);
    }
}
