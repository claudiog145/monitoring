using System.Text.Json;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Infrastructure.Sql;

namespace JourneyMonitor.Tests.Spec1000.A3;

public class KpiBatchPayloadTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SqlKpiBucketStore_BuildBatchPayloads_SplitsAtOneThousandAfterDedup()
    {
        var buckets = Enumerable.Range(0, 2500).Select(i => A3Kpi.Bucket(7, T0.AddMinutes(i), i, i)).ToList();
        // 10 duplicates of existing keys: they must collapse, not add rows or new batches.
        buckets.AddRange(Enumerable.Range(0, 10).Select(i => A3Kpi.Bucket(7, T0.AddMinutes(i), 999, 999)));

        var payloads = SqlKpiBucketStore.BuildBatchPayloads(buckets);

        Assert.Equal([1000, 1000, 500], payloads.Select(Count));
        using var first = JsonDocument.Parse(payloads[0]);
        var row0 = first.RootElement[0];
        Assert.Equal(999, row0.GetProperty("totalCount").GetInt64());
        Assert.Equal(7, row0.GetProperty("kpiDefinitionId").GetInt32());
        foreach (var name in new[] { "bucketStart", "sampledCount", "p50", "p95", "mean", "stdDev", "value" })
        {
            Assert.True(row0.TryGetProperty(name, out _), $"missing camelCase property {name}");
        }
        Assert.Empty(SqlKpiBucketStore.BuildBatchPayloads([]));
    }

    [Fact]
    public void SqlKpiSampleStore_BuildBatchPayloads_SplitsAtOneThousand()
    {
        var samples = Enumerable.Range(0, 2001).Select(i => new KpiSample(0, 7, T0, T0.AddSeconds(i), i, $"op-{i}")).ToList();

        var payloads = SqlKpiSampleStore.BuildBatchPayloads(samples);

        Assert.Equal([1000, 1000, 1], payloads.Select(Count));
        using var last = JsonDocument.Parse(payloads[2]);
        var row = last.RootElement[0];
        Assert.Equal("op-2000", row.GetProperty("operationId").GetString());
        Assert.True(row.TryGetProperty("timestamp", out _));
        Assert.True(row.TryGetProperty("bucketStart", out _));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void SqlKpiStores_BuildBatchPayloads_RejectNonFiniteValues(double bad)
    {
        Assert.Throws<ArgumentException>(() => SqlKpiBucketStore.BuildBatchPayloads([A3Kpi.Bucket(7, T0, 1, 1) with { StdDev = bad }]));
        Assert.Throws<ArgumentException>(() => SqlKpiSampleStore.BuildBatchPayloads([new KpiSample(0, 7, T0, T0, bad, null)]));
    }

    private static int Count(string payload)
    {
        using var doc = JsonDocument.Parse(payload);
        return doc.RootElement.GetArrayLength();
    }
}
