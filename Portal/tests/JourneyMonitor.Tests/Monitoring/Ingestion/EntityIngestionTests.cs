using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring.Daemons;
using JourneyMonitor.Core.Monitoring.Ingestion;
using JourneyMonitor.Core.Monitoring.Kpi;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Core.UseCases;
using Microsoft.Extensions.Logging.Abstractions;

namespace JourneyMonitor.Tests.Spec1000.A2;

/// <summary>SD3: ingestion of entity-grain KPIs (contract v4 §1–§2).</summary>
[Collection(A2IngestionRunLockCollection.Name)]
public sealed class EntityIngestionTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 14, 37, 12, TimeSpan.Zero);
    private static readonly DateTimeOffset LastClosedBucket = new(2026, 11, 5, 14, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset DayAgo = LastClosedBucket - TimeSpan.FromDays(1);

    // BaselinePercent {lookbackDays:7} on 1 h buckets: 7 days of same-slot history + the evaluated bucket.
    private static readonly DateTimeOffset HistoryStart = LastClosedBucket - TimeSpan.FromDays(7) - TimeSpan.FromHours(1);

    private readonly A2BucketStore _buckets = new();
    private readonly A2SampleStore _samples = new();
    private readonly A2WatermarkStore _watermarks = new();
    private readonly H1ActivityRecorder _activity = new();

    private KpiIngestionService Service(IEnumerable<KpiDefinition> definitions, IEnumerable<IKpiSource> sources, bool withEvaluator, HealthMonitoringOptions? options = null, DateTimeOffset? now = null) =>
        new(
            new A2DefinitionStore(definitions.ToArray()),
            sources,
            _buckets,
            _samples,
            _watermarks,
            Microsoft.Extensions.Options.Options.Create(options ?? new HealthMonitoringOptions { Enabled = true, MaxRowsPerCycle = 1_000_000 }),
            new A2FixedTimeProvider(now ?? Now),
            NullLogger<KpiIngestionService>.Instance,
            null,
            _activity,
            evaluator: withEvaluator ? new KpiEvaluator() : null);

    private static KpiDefinition EntityDefinition(int id = 1, int maxEntities = 500) =>
        A2Kpi.Definition(id: id) with { Grain = KpiGrain.Entity, MaxEntitiesPerBucket = maxEntities };

    private static KpiSourceBucket Row(DateTimeOffset start, string entity, long total, double p95 = 200, long? failed = 1) =>
        new(start, total, null) { EntityKey = entity, P50 = p95 / 2, P95 = p95, Mean = p95 / 2 + 10, FailedCount = failed };

    /// <summary>One row per hour in [from, to) for each entity, plus a sample per row the ingestion must ignore.</summary>
    private static KpiSourceResult EntityHourly(KpiDefinition definition, DateTimeOffset from, DateTimeOffset to, params string[] entities)
    {
        var buckets = new List<KpiSourceBucket>();
        var samples = new List<KpiSample>();
        for (var start = from; start < to; start += definition.BucketSize)
        {
            foreach (var entity in entities)
            {
                buckets.Add(Row(start, entity, 100));
                samples.Add(new KpiSample(0, definition.KpiDefinitionId, start, start.AddSeconds(1), 150, "op"));
            }
        }
        return new KpiSourceResult(buckets, samples);
    }

    private async Task SeedEntityBucketsAsync(int kpiDefinitionId, DateTimeOffset from, DateTimeOffset to, params string[] entities)
    {
        var buckets = new List<KpiBucket>();
        for (var start = from; start < to; start += TimeSpan.FromHours(1))
        {
            buckets.AddRange(entities.Select(e => new KpiBucket(kpiDefinitionId, start, 100, 0, 100, 200, 110, null, null) { EntityKey = e }));
        }
        await _buckets.UpsertAsync(buckets);
    }

    [Fact]
    public async Task Ingestion_EntityGrain_WritesOneBucketPerEntity()
    {
        var definition = EntityDefinition(maxEntities: 2);
        var from = LastClosedBucket.AddHours(-2);
        _watermarks.Values[definition.KpiDefinitionId] = from;
        var source = new A2KpiSource
        {
            Respond = (d, f, t, n) =>
            {
                var rows = new List<KpiSourceBucket>();
                for (var start = f; start < t; start += d.BucketSize)
                {
                    rows.Add(Row(start, "CLUB-B", 100, p95: 150, failed: 1) with { Mean = 75, Value = 9 });
                    rows.Add(Row(start, "club-b", 300, p95: 200, failed: 3) with { Value = 5 });
                    rows.Add(Row(start, "club-a", 350, p95: 400, failed: null));
                    rows.Add(Row(start, "club-c", 50));
                    rows.Add(Row(start, "   ", 999));
                    rows.Add(Row(start, "", 5));
                    rows.Add(Row(start, new string('x', KpiEntityBucketAssembler.MaxEntityKeyLength + 1), 5000));
                }
                rows.Add(Row(t, "club-a", 10));
                return new KpiSourceResult(rows, []);
            },
        };

        var result = await Service([definition], [source], withEvaluator: false).RunAsync();

        var stored = _buckets.All.ToList();
        Assert.Equal(4, stored.Count);
        Assert.DoesNotContain(stored, b => string.IsNullOrWhiteSpace(b.EntityKey));
        Assert.DoesNotContain(stored, b => b.EntityKey is "club-c");
        Assert.All(stored, b => Assert.InRange(b.BucketStart, from, LastClosedBucket.AddTicks(-1)));
        foreach (var hour in new[] { from, from.AddHours(1) })
        {
            var inHour = stored.Where(b => b.BucketStart == hour).ToList();
            Assert.Equal(["club-b", "club-a"], inHour.Select(b => b.EntityKey).ToArray());

            var merged = inHour[0];
            Assert.Equal(400, merged.TotalCount);
            Assert.Equal(4, merged.FailedCount);
            Assert.Equal(100, merged.P50);
            Assert.Equal(200, merged.P95);
            Assert.Equal((110d * 300 + 75d * 100) / 400, merged.Mean);
            Assert.Equal(0, merged.SampledCount);
            Assert.Equal(5, merged.Value);
            Assert.Null(merged.StdDev);

            Assert.Equal(new KpiBucket(definition.KpiDefinitionId, hour, 350, 0, 200, 400, 210, null, null) { EntityKey = "club-a" }, inHour[1]);
        }

        Assert.Equal(LastClosedBucket, _watermarks.Values[definition.KpiDefinitionId]);
        Assert.Equal(new KpiIngestionRunResult(1, 4, 0, 0), result);
        Assert.Contains((DaemonActivityLevel.Info, DaemonActivityKinds.IngestionKpi, "Latency/hotels [entity]: 4 bucket(s) for 2 entit(ies) [12:00–14:00)"), _activity.Entries);
        Assert.Empty(await _buckets.GetRangeAsync(definition.KpiDefinitionId, from, LastClosedBucket));
    }

    [Fact]
    public async Task Ingestion_EntityGrain_EmptySource_DoesNotRequeryEveryCycle()
    {
        // Own id: the empty-gap record is process-wide per KpiDefinitionId.
        var definition = EntityDefinition(id: 9301);
        _watermarks.Values[definition.KpiDefinitionId] = LastClosedBucket;
        var source = new A2KpiSource();
        bool IsBackfill(A2FetchCall c) => c.To <= LastClosedBucket && c.From < LastClosedBucket;

        // Same open hour (14:00 is the last closed bucket in all three runs): the empty gap is queried once.
        foreach (var now in new[] { Now, Now.AddMinutes(5), Now.AddMinutes(20) })
        {
            var result = await Service([definition], [source], withEvaluator: true, now: now).RunAsync();
            Assert.Equal(new KpiIngestionRunResult(0, 0, 0, 0), result);
        }
        var first = Assert.Single(source.Calls);
        Assert.Equal((HistoryStart, LastClosedBucket), (first.From, first.To));

        // The 15:00 bucket closes: at most one more backfill attempt, plus the normal fetch of the new bucket.
        foreach (var now in new[] { Now.AddMinutes(30), Now.AddMinutes(40), Now.AddMinutes(50) })
        {
            await Service([definition], [source], withEvaluator: true, now: now).RunAsync();
        }
        Assert.Equal(2, source.Calls.Count(IsBackfill));
        Assert.Single(source.Calls, c => c.From == LastClosedBucket);
        Assert.Equal(3, source.Calls.Count);

        Assert.DoesNotContain(_activity.Entries, e => e.Message.Contains("backfilled", StringComparison.Ordinal));
        Assert.Empty(_buckets.All);
    }

    [Fact]
    public async Task Ingestion_EntityGrain_WritesNoSamples()
    {
        var definition = EntityDefinition();
        _watermarks.Values[definition.KpiDefinitionId] = LastClosedBucket.AddHours(-3);
        var source = new A2KpiSource { Respond = (d, f, t, n) => EntityHourly(d, f, t, "club-1", "club-2") };

        var result = await Service([definition], [source], withEvaluator: true).RunAsync();

        Assert.Empty(_samples.Samples);
        Assert.Equal(0, result.SamplesWritten);
        Assert.All(source.Calls, c => Assert.Equal(0, c.SamplesPerBucket));
        var fresh = _buckets.All.Where(b => b.BucketStart >= LastClosedBucket.AddHours(-3)).ToList();
        Assert.Equal(6, fresh.Count);
        Assert.All(_buckets.All, b => Assert.Equal(0, b.SampledCount));
    }

    [Fact]
    public async Task Ingestion_EntityGrain_BackfillsHistory()
    {
        var definition = EntityDefinition();
        await SeedEntityBucketsAsync(definition.KpiDefinitionId, DayAgo, LastClosedBucket, "x", "y", "z");
        _watermarks.Values[definition.KpiDefinitionId] = LastClosedBucket;
        var source = new A2KpiSource { Respond = (d, f, t, n) => EntityHourly(d, f, t, "x", "y", "z") };
        // 3 entities per stored bucket: the budget holds 48 buckets, so the 145-bucket gap is fetched in 2-day chunks.
        var options = new HealthMonitoringOptions { Enabled = true, MaxRowsPerCycle = 48 * 3 };

        var first = await Service([definition], [source], withEvaluator: true, options).RunAsync();
        for (var i = 0; i < 4; i++)
        {
            await Service([definition], [source], withEvaluator: true, options).RunAsync();
        }

        Assert.Equal(
            [
                (DayAgo - TimeSpan.FromDays(2), DayAgo),
                (DayAgo - TimeSpan.FromDays(4), DayAgo - TimeSpan.FromDays(2)),
                (DayAgo - TimeSpan.FromDays(6), DayAgo - TimeSpan.FromDays(4)),
                (HistoryStart, DayAgo - TimeSpan.FromDays(6)),
            ],
            source.Calls.Select(c => (c.From, c.To)).ToArray());
        Assert.All(source.Calls, c => Assert.Equal(0, c.SamplesPerBucket));
        Assert.Equal(new KpiIngestionRunResult(1, 144, 0, 0), first);
        Assert.Contains((DaemonActivityLevel.Info, DaemonActivityKinds.IngestionKpi,
            "Latency/hotels [entity]: backfilled history 144 bucket(s) for 3 entit(ies) [2026-11-02 14:00–2026-11-04 14:00)"), _activity.Entries);

        var history = await _buckets.GetEntityRangeAsync(definition.KpiDefinitionId, HistoryStart, LastClosedBucket);
        Assert.Equal((7 * 24 + 1) * 3, history.Count);
        Assert.DoesNotContain(_buckets.All, b => b.EntityKey.Length == 0 || b.TotalCount == 0);
        Assert.Empty(_samples.Samples);
        Assert.Empty(_watermarks.SetCalls);
    }

    [Fact]
    public async Task Ingestion_EntityGrain_BackfillEmptyChunk_WalksBackInSameRun()
    {
        var definition = EntityDefinition();
        await SeedEntityBucketsAsync(definition.KpiDefinitionId, DayAgo, LastClosedBucket, "x", "y", "z");
        _watermarks.Values[definition.KpiDefinitionId] = LastClosedBucket;
        var threeDaysAgo = LastClosedBucket - TimeSpan.FromDays(3);
        var source = new A2KpiSource
        {
            Respond = (d, f, t, n) => f >= threeDaysAgo ? new KpiSourceResult([], []) : EntityHourly(d, f, t, "x", "y", "z"),
        };
        var options = new HealthMonitoringOptions { Enabled = true, MaxRowsPerCycle = 48 * 3 };

        await Service([definition], [source], withEvaluator: true, options).RunAsync();

        Assert.Equal(
            [(threeDaysAgo, DayAgo), (LastClosedBucket - TimeSpan.FromDays(5), threeDaysAgo)],
            source.Calls.Select(c => (c.From, c.To)).ToArray());
        Assert.Contains((DaemonActivityLevel.Info, DaemonActivityKinds.IngestionKpi,
            "Latency/hotels [entity]: backfilled history 144 bucket(s) for 3 entit(ies) [2026-10-31 14:00–2026-11-04 14:00)"), _activity.Entries);
        Assert.Empty(await _buckets.GetEntityRangeAsync(definition.KpiDefinitionId, threeDaysAgo, DayAgo));
        Assert.DoesNotContain(_buckets.All, b => b.TotalCount == 0);
    }

    [Fact]
    public async Task Ingestion_EntityGrain_FirstRunClampedToRowBudget()
    {
        var definition = EntityDefinition(maxEntities: 500);
        var source = new A2KpiSource { Respond = (d, f, t, n) => EntityHourly(d, f, t, "x") };
        // Without a watermark the history would be 169 buckets x 500 entities; one day fits in 24 x 500 rows.
        var options = new HealthMonitoringOptions { Enabled = true, MaxRowsPerCycle = 24 * 500 };

        await Service([definition], [source], withEvaluator: true, options).RunAsync();

        var call = Assert.Single(source.Calls);
        Assert.Equal((DayAgo, LastClosedBucket), (call.From, call.To));
        Assert.Equal(LastClosedBucket, _watermarks.Values[definition.KpiDefinitionId]);

        // The next run backfills the older history through H1.
        await Service([definition], [source], withEvaluator: true, options).RunAsync();
        Assert.Equal((DayAgo - TimeSpan.FromDays(6) - TimeSpan.FromHours(1), DayAgo), (source.Calls[1].From, source.Calls[1].To));
        Assert.Equal(7 * 24 + 1, (await _buckets.GetEntityRangeAsync(definition.KpiDefinitionId, HistoryStart, LastClosedBucket)).Count);
    }

    [Fact]
    public async Task Ingestion_EntityGrain_RowsCountAgainstMaxRowsPerCycle()
    {
        var entity = EntityDefinition(id: 1);
        var domain = A2Kpi.Definition(id: 2);
        _watermarks.Values[1] = LastClosedBucket.AddHours(-2);
        _watermarks.Values[2] = LastClosedBucket.AddHours(-2);
        var source = new A2KpiSource
        {
            Respond = (d, f, t, n) => d.Grain == KpiGrain.Entity ? EntityHourly(d, f, t, "a", "b", "c") : A2KpiSource.Hourly(d, f, t, n, 5),
        };
        var options = new HealthMonitoringOptions { Enabled = true, MaxRowsPerCycle = 5 };

        var result = await Service([entity, domain], [source], withEvaluator: false, options).RunAsync();

        Assert.Equal([1], source.Calls.Select(c => c.KpiDefinitionId).ToArray());
        Assert.Equal(new KpiIngestionRunResult(1, 6, 0, 0), result);
    }

    [Fact]
    public async Task Ingestion_DomainGrain_Unchanged()
    {
        var domain = A2Kpi.Definition(id: 1, sampleSize: 4000);
        var entity = EntityDefinition(id: 2);
        var from = LastClosedBucket.AddHours(-2);
        _watermarks.Values[1] = from;
        _watermarks.Values[2] = from;
        var source = new A2KpiSource
        {
            Respond = (d, f, t, n) => d.Grain == KpiGrain.Entity
                ? EntityHourly(d, f, t, "club-1")
                : A2KpiSource.Hourly(d, f, t, n, 500),
        };

        var result = await Service([domain, entity], [source], withEvaluator: false).RunAsync();

        Assert.Equal(167, source.Calls.Single(c => c.KpiDefinitionId == 1).SamplesPerBucket);
        var domainBuckets = await _buckets.GetRangeAsync(1, from, LastClosedBucket);
        Assert.Equal(2, domainBuckets.Count);
        Assert.All(domainBuckets, b =>
        {
            Assert.Equal("", b.EntityKey);
            Assert.Equal(167, b.SampledCount);
            Assert.Equal(5000, b.TotalCount);
            Assert.NotNull(b.P95);
            Assert.NotNull(b.StdDev);
            Assert.Null(b.FailedCount);
        });
        Assert.Equal(2 * 167, _samples.Samples.Count);
        Assert.All(_samples.Samples, s => Assert.Equal(1, s.KpiDefinitionId));
        Assert.Contains((DaemonActivityLevel.Info, DaemonActivityKinds.IngestionKpi,
            "Latency/hotels: 2 bucket(s) [12:00–14:00), 334 sample(s), watermark → 14:00"), _activity.Entries);
        Assert.Equal(new KpiIngestionRunResult(2, 4, 334, 0), result);
        Assert.Equal(LastClosedBucket, _watermarks.Values[1]);
    }
}
