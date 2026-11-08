using System.Globalization;
using JourneyMonitor.Core.Domain;

namespace JourneyMonitor.Core.Monitoring.Ingestion;

/// <summary>
/// Entity-grain KpiBuckets are kept 8 days (mirror of <c>SqlKpiBucketStore.EntityRetention</c>, which purges older ones
/// on every entity upsert). The ingestion never asks for entity history beyond it, so a method that needs more cannot
/// trigger a backfill that is purged again on the next cycle.
/// </summary>
public static class KpiEntityRetention
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(8);

    /// <summary>Longest entity history span the ingestion keeps: the retention minus one bucket, in whole buckets.</summary>
    public static TimeSpan MaxHistorySpan(TimeSpan bucketSize)
    {
        if (bucketSize <= TimeSpan.Zero) return Retention;
        var buckets = Math.Max(1, Retention.Ticks / bucketSize.Ticks - 1);
        return TimeSpan.FromTicks(buckets * bucketSize.Ticks);
    }

    /// <summary>Oldest entity bucket start still inside the retention at <paramref name="now"/> (rounded up to a bucket).</summary>
    public static DateTimeOffset OldestRetainedBucket(DateTimeOffset now, TimeSpan bucketSize)
    {
        var cutoff = now - Retention;
        var floor = KpiTime.FloorToBucket(cutoff, bucketSize);
        return floor < cutoff ? floor + bucketSize : floor;
    }

    /// <summary>
    /// True when the detection method's required history plus the evaluated bucket does not fit
    /// <see cref="MaxHistorySpan"/>.
    /// </summary>
    public static bool ExceedsRetention(TimeSpan requiredHistory, TimeSpan bucketSize) =>
        requiredHistory + bucketSize > MaxHistorySpan(bucketSize);

    public static string ExceedsRetentionMessage(TimeSpan requiredHistory) =>
        $"Entity-grain KPIs keep 8 days; method requires {requiredHistory.TotalDays.ToString("0.##", CultureInfo.InvariantCulture)} days.";
}
