using JourneyMonitor.Core.Domain;

namespace JourneyMonitor.Core.Monitoring.Ingestion;

/// <summary>Spreads a KPI's SampleSize evenly across the buckets of its lookback window.</summary>
public static class KpiSampling
{
    /// <summary>SampleSize == 0 ? 0 : ceil(SampleSize / max(1, LookbackWindow / BucketSize)). 4000 over 24 x 1 h buckets = 167.</summary>
    public static int SamplesPerBucket(KpiDefinition d)
    {
        ArgumentNullException.ThrowIfNull(d);
        if (d.SampleSize <= 0) return 0;
        if (d.BucketSize <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(d), "BucketSize must be positive.");

        var buckets = Math.Max(1d, d.LookbackWindow / d.BucketSize);
        return (int)Math.Ceiling(d.SampleSize / buckets);
    }
}
