using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring.Ingestion;

namespace JourneyMonitor.Tests.Spec1000.A2;

public sealed class KpiBucketStatisticsTests
{
    private static readonly DateTimeOffset Bucket = new(2026, 11, 5, 13, 0, 0, TimeSpan.Zero);

    private static List<KpiSample> Samples(params double[] values) =>
        values.Select((v, i) => new KpiSample(0, 7, Bucket, Bucket.AddSeconds(i), v, null)).ToList();

    [Fact]
    public void Statistics_PercentileMatchesExcelPercentileInc()
    {
        Assert.Equal(3.85, KpiBucketStatistics.Percentile([1, 2, 3, 4], 0.95)!.Value, 10);
        Assert.Equal(35, KpiBucketStatistics.Percentile([15, 20, 35, 40, 50], 0.5)!.Value, 10);
        // Unsorted input, edges and a single value.
        Assert.Equal(35, KpiBucketStatistics.Percentile([50, 15, 40, 35, 20], 0.5)!.Value, 10);
        Assert.Equal(15, KpiBucketStatistics.Percentile([15, 20, 35, 40, 50], 0)!.Value, 10);
        Assert.Equal(50, KpiBucketStatistics.Percentile([15, 20, 35, 40, 50], 1)!.Value, 10);
        Assert.Equal(42, KpiBucketStatistics.Percentile([42], 0.95)!.Value, 10);
        Assert.Throws<ArgumentOutOfRangeException>(() => KpiBucketStatistics.Percentile([1, 2], 1.5));
    }

    [Fact]
    public void Statistics_EmptySamples_ProduceNullStats()
    {
        Assert.Null(KpiBucketStatistics.Percentile([], 0.5));

        var bucket = KpiBucketStatistics.Build(7, new KpiSourceBucket(Bucket, 120, 1.5), []);

        Assert.Equal(7, bucket.KpiDefinitionId);
        Assert.Equal(Bucket, bucket.BucketStart);
        Assert.Equal(120, bucket.TotalCount);
        Assert.Equal(1.5, bucket.Value);
        Assert.Equal(0, bucket.SampledCount);
        Assert.Null(bucket.P50);
        Assert.Null(bucket.P95);
        Assert.Null(bucket.Mean);
        Assert.Null(bucket.StdDev);
    }

    [Fact]
    public void Statistics_BuildComputesPopulationStats()
    {
        var bucket = KpiBucketStatistics.Build(7, new KpiSourceBucket(Bucket, 1000, null), Samples(2, 4, 4, 4, 5, 5, 7, 9));

        Assert.Equal(1000, bucket.TotalCount);
        Assert.Equal(8, bucket.SampledCount);
        Assert.Equal(4.5, bucket.P50!.Value, 10);
        Assert.Equal(8.3, bucket.P95!.Value, 10);
        Assert.Equal(5, bucket.Mean!.Value, 10);
        Assert.Equal(2, bucket.StdDev!.Value, 10);
        Assert.Null(bucket.Value);
    }

    [Fact]
    public void Sampling_SplitsSampleSizeAcrossLookbackBuckets()
    {
        Assert.Equal(167, KpiSampling.SamplesPerBucket(A2Kpi.Definition(sampleSize: 4000)));
        Assert.Equal(0, KpiSampling.SamplesPerBucket(A2Kpi.Definition(sampleSize: 0)));
        Assert.Equal(100, KpiSampling.SamplesPerBucket(A2Kpi.Definition(sampleSize: 100, lookback: TimeSpan.FromHours(1))));
    }
}
