using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring.Ingestion;
using JourneyMonitor.Core.Monitoring.Kpi;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Core.UseCases;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Tests.Spec1000.A2;

[Collection(A2IngestionRunLockCollection.Name)]
public sealed class KpiIngestionServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 14, 37, 12, TimeSpan.Zero);
    private static readonly DateTimeOffset LastClosedBucket = new(2026, 11, 5, 14, 0, 0, TimeSpan.Zero);

    private readonly A2BucketStore _buckets = new();
    private readonly A2SampleStore _samples = new();
    private readonly A2WatermarkStore _watermarks = new();

    private KpiIngestionService Service(IEnumerable<KpiDefinition> definitions, IEnumerable<IKpiSource> sources, HealthMonitoringOptions? options = null, DateTimeOffset? now = null, int? lagMinutes = null) =>
        new(
            new A2DefinitionStore(definitions.ToArray()),
            sources,
            _buckets,
            _samples,
            _watermarks,
            Microsoft.Extensions.Options.Options.Create(options ?? new HealthMonitoringOptions { Enabled = true }),
            new A2FixedTimeProvider(now ?? Now),
            NullLogger<KpiIngestionService>.Instance,
            lagMinutes is null ? null : Microsoft.Extensions.Options.Options.Create(new KpiIngestionOptions { LagMinutes = lagMinutes.Value }));

    [Fact]
    public async Task Ingestion_OverlappingRunIsSkipped()
    {
        var source = new A2BlockingKpiSource();
        var definition = A2Kpi.Definition();

        var first = Service([definition], [source]).RunAsync();
        await source.Entered.WaitAsync(TimeSpan.FromSeconds(10));

        var second = await Service([definition], [source]).RunAsync();

        Assert.Equal(new KpiIngestionRunResult(0, 0, 0, 0), second);
        Assert.Equal(1, source.Calls);

        source.Release();
        var firstResult = await first.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, firstResult.KpisProcessed);
        Assert.Single(_samples.PurgeCutoffs);

        // The lock is released afterwards: a new run proceeds (and finds nothing new to fetch).
        var third = await Service([definition], [new A2KpiSource()]).RunAsync();
        Assert.Equal(2, _samples.PurgeCutoffs.Count);
        Assert.Equal(0, third.Skipped);
    }

    [Fact]
    public async Task Ingestion_RespectsIngestionLag()
    {
        var definition = A2Kpi.Definition();
        var source = new A2KpiSource { Respond = (d, f, t, n) => A2KpiSource.Hourly(d, f, t, n, samplesAvailable: 2) };
        var now = new DateTimeOffset(2026, 11, 5, 14, 3, 0, TimeSpan.Zero);
        var expectedTo = new DateTimeOffset(2026, 11, 5, 13, 0, 0, TimeSpan.Zero);

        await Service([definition], [source], now: now, lagMinutes: 5).RunAsync();

        var call = Assert.Single(source.Calls);
        Assert.Equal(expectedTo, call.To);
        Assert.Equal(expectedTo, _watermarks.Values[definition.KpiDefinitionId]);
        Assert.DoesNotContain(_buckets.All, b => b.BucketStart >= expectedTo);

        // Without lag the 13:00 bucket would already be ingested.
        var noLag = new A2KpiSource();
        _watermarks.Values.Clear();
        await Service([definition], [noLag], now: now, lagMinutes: 0).RunAsync();
        Assert.Equal(new DateTimeOffset(2026, 11, 5, 14, 0, 0, TimeSpan.Zero), Assert.Single(noLag.Calls).To);
    }

    [Fact]
    public async Task Ingestion_DistributesSampleAcrossBuckets()
    {
        var definition = A2Kpi.Definition(sampleSize: 4000, bucketSize: TimeSpan.FromHours(1), lookback: TimeSpan.FromHours(24));
        var source = new A2KpiSource { Respond = (d, f, t, n) => A2KpiSource.Hourly(d, f, t, n, samplesAvailable: 500) };

        var result = await Service([definition], [source]).RunAsync();

        var call = Assert.Single(source.Calls);
        Assert.Equal(167, call.SamplesPerBucket);
        Assert.Equal(24, result.BucketsWritten);
        Assert.Equal(24 * 167, result.SamplesWritten);
        Assert.All(_buckets.All, b => Assert.Equal(167, b.SampledCount));
        Assert.All(_samples.Samples.GroupBy(s => s.BucketStart), g => Assert.Equal(167, g.Count()));
    }

    [Fact]
    public async Task Ingestion_BucketWithFewerRowsTakesAll()
    {
        var definition = A2Kpi.Definition(sampleSize: 4000);
        var source = new A2KpiSource { Respond = (d, f, t, n) => A2KpiSource.Hourly(d, f, t, n, samplesAvailable: 50) };

        await Service([definition], [source]).RunAsync();

        Assert.Equal(24, _buckets.All.Count());
        Assert.All(_buckets.All, b =>
        {
            Assert.Equal(50, b.SampledCount);
            Assert.Equal(500, b.TotalCount);
            Assert.NotNull(b.P95);
        });
        Assert.Equal(24 * 50, _samples.Samples.Count);
    }

    [Fact]
    public async Task Ingestion_AdvancesWatermarkToLastClosedBucket()
    {
        var definition = A2Kpi.Definition();
        var source = new A2KpiSource { Respond = (d, f, t, n) => A2KpiSource.Hourly(d, f, t, n, samplesAvailable: 5) };

        await Service([definition], [source]).RunAsync();

        Assert.Equal(LastClosedBucket, _watermarks.Values[definition.KpiDefinitionId]);
        var call = Assert.Single(source.Calls);
        Assert.Equal(LastClosedBucket, call.To);
        // The open 14:00 bucket is never written.
        Assert.DoesNotContain(_buckets.All, b => b.BucketStart >= LastClosedBucket);
        Assert.Equal(LastClosedBucket.AddHours(-1), _buckets.All.Max(b => b.BucketStart));
    }

    [Fact]
    public async Task Ingestion_FirstRunStartsAtLookbackWindow()
    {
        var definition = A2Kpi.Definition(lookback: TimeSpan.FromHours(24));
        var source = new A2KpiSource();

        await Service([definition], [source]).RunAsync();

        var call = Assert.Single(source.Calls);
        Assert.Equal(LastClosedBucket - TimeSpan.FromHours(24), call.From);
        Assert.Equal(LastClosedBucket, call.To);
    }

    [Fact]
    public async Task Ingestion_LaterRunStartsAtWatermarkAndSkipsWhenCaughtUp()
    {
        var definition = A2Kpi.Definition();
        var source = new A2KpiSource { Respond = (d, f, t, n) => A2KpiSource.Hourly(d, f, t, n, samplesAvailable: 3) };
        _watermarks.Values[definition.KpiDefinitionId] = LastClosedBucket.AddHours(-2);

        await Service([definition], [source]).RunAsync();

        var call = Assert.Single(source.Calls);
        Assert.Equal(LastClosedBucket.AddHours(-2), call.From);
        Assert.Equal(2, _buckets.All.Count());

        // Same hour again: nothing new is closed, so the source is not called.
        await Service([definition], [source], now: Now.AddMinutes(10)).RunAsync();
        Assert.Single(source.Calls);
        Assert.Single(_watermarks.SetCalls);
    }

    [Fact]
    public async Task Ingestion_RespectsMaxRowsPerCycle()
    {
        var first = A2Kpi.Definition(id: 1);
        var second = A2Kpi.Definition(id: 2);
        var third = A2Kpi.Definition(id: 3);
        var source = new A2KpiSource { Respond = (d, f, t, n) => A2KpiSource.Hourly(d, f, t, n, samplesAvailable: 500) };
        var options = new HealthMonitoringOptions { Enabled = true, MaxRowsPerCycle = 1000 };

        var result = await Service([third, first, second], [source], options).RunAsync();

        var call = Assert.Single(source.Calls);
        Assert.Equal(1, call.KpiDefinitionId);
        Assert.Equal(1, result.KpisProcessed);
        Assert.True(result.SamplesWritten > options.MaxRowsPerCycle);
        Assert.False(_watermarks.Values.ContainsKey(2));
        Assert.False(_watermarks.Values.ContainsKey(3));
        Assert.Single(_samples.PurgeCutoffs);
    }

    [Fact]
    public async Task Ingestion_SkipsDefinitionsWithoutSource()
    {
        var orphan = A2Kpi.Definition(id: 1, sourceKey: "e2e:none");
        var handled = A2Kpi.Definition(id: 2);
        var source = new A2KpiSource();

        var result = await Service([orphan, handled], [source]).RunAsync();

        Assert.Equal(1, result.Skipped);
        Assert.Equal(1, result.KpisProcessed);
        Assert.Equal(2, Assert.Single(source.Calls).KpiDefinitionId);
        Assert.False(_watermarks.Values.ContainsKey(1));
    }

    [Fact]
    public async Task Ingestion_SourceUnavailable_DoesNotAdvanceWatermark()
    {
        var definition = A2Kpi.Definition();
        var previous = LastClosedBucket.AddHours(-3);
        _watermarks.Values[definition.KpiDefinitionId] = previous;
        var source = new A2KpiSource { Respond = (_, _, _, _) => throw new KpiSourceUnavailableException("air is not configured") };

        var result = await Service([definition], [source]).RunAsync();

        Assert.Equal(1, result.Skipped);
        Assert.Equal(0, result.KpisProcessed);
        Assert.Equal(previous, _watermarks.Values[definition.KpiDefinitionId]);
        Assert.Empty(_watermarks.SetCalls);
        Assert.Empty(_buckets.Batches);
    }

    [Fact]
    public async Task Ingestion_FetchFailure_SkipsKpiAndContinues()
    {
        var failing = A2Kpi.Definition(id: 1);
        var healthy = A2Kpi.Definition(id: 2);
        var source = new A2KpiSource
        {
            Respond = (d, f, t, n) => d.KpiDefinitionId == 1
                ? throw new InvalidOperationException("App Insights query 503")
                : A2KpiSource.Hourly(d, f, t, n, samplesAvailable: 1),
        };

        var result = await Service([failing, healthy], [source]).RunAsync();

        Assert.Equal(1, result.Skipped);
        Assert.Equal(1, result.KpisProcessed);
        Assert.False(_watermarks.Values.ContainsKey(1));
        Assert.Equal(LastClosedBucket, _watermarks.Values[2]);
    }

    [Fact]
    public async Task Ingestion_PurgesSamplesOlderThanRetention()
    {
        var source = new A2KpiSource();
        var options = new HealthMonitoringOptions { Enabled = true, SampleRetentionDays = 7 };

        await Service([A2Kpi.Definition()], [source], options).RunAsync();

        Assert.Equal(Now - TimeSpan.FromDays(7), Assert.Single(_samples.PurgeCutoffs));
    }

    [Fact]
    public async Task Ingestion_DisabledDoesNothing()
    {
        var source = new A2KpiSource();

        var result = await Service([A2Kpi.Definition()], [source], new HealthMonitoringOptions { Enabled = false }).RunAsync();

        Assert.Equal(new KpiIngestionRunResult(0, 0, 0, 0), result);
        Assert.Empty(source.Calls);
        Assert.Empty(_samples.PurgeCutoffs);
        Assert.Empty(_watermarks.SetCalls);
    }

    [Fact]
    public async Task Ingestion_DeduplicatesBucketsAndDropsOpenBuckets()
    {
        var definition = A2Kpi.Definition(sampleSize: 0);
        var closed = LastClosedBucket.AddHours(-1);
        var source = new A2KpiSource
        {
            Respond = (_, _, _, _) => new KpiSourceResult(
                [
                    new KpiSourceBucket(closed, 10, null),
                    new KpiSourceBucket(closed, 5, 2.5),
                    new KpiSourceBucket(LastClosedBucket, 99, null),
                ],
                []),
        };

        var result = await Service([definition], [source]).RunAsync();

        var batch = Assert.Single(_buckets.Batches);
        var bucket = Assert.Single(batch);
        Assert.Equal(closed, bucket.BucketStart);
        Assert.Equal(15, bucket.TotalCount);
        Assert.Equal(2.5, bucket.Value);
        Assert.Equal(0, bucket.SampledCount);
        Assert.Null(bucket.P95);
        Assert.Equal(1, result.BucketsWritten);
        Assert.Equal(0, result.SamplesWritten);
        Assert.Equal(0, Assert.Single(source.Calls).SamplesPerBucket);
    }

    // ---- H1: backfill of the history required by the detection method ----

    // BaselinePercent {lookbackDays:7} on 1 h buckets: 7 days of same-slot history + the evaluated bucket.
    private static readonly DateTimeOffset HistoryStart = LastClosedBucket - TimeSpan.FromDays(7) - TimeSpan.FromHours(1);

    private readonly H1ActivityRecorder _activity = new();

    private KpiIngestionService ServiceWithEvaluator(IEnumerable<KpiDefinition> definitions, IEnumerable<IKpiSource> sources, HealthMonitoringOptions? options = null, DateTimeOffset? now = null) =>
        new(
            new A2DefinitionStore(definitions.ToArray()),
            sources,
            _buckets,
            _samples,
            _watermarks,
            Microsoft.Extensions.Options.Options.Create(options ?? new HealthMonitoringOptions { Enabled = true }),
            new A2FixedTimeProvider(now ?? Now),
            NullLogger<KpiIngestionService>.Instance,
            null,
            _activity,
            evaluator: new KpiEvaluator());

    private static HealthMonitoringOptions Unlimited => new() { Enabled = true, MaxRowsPerCycle = 1_000_000 };

    private static A2KpiSource HourlySource(int samplesAvailable = 500) =>
        new() { Respond = (d, f, t, n) => A2KpiSource.Hourly(d, f, t, n, samplesAvailable) };

    private async Task SeedBucketsAsync(int kpiDefinitionId, DateTimeOffset from, DateTimeOffset to)
    {
        var buckets = new List<KpiBucket>();
        for (var start = from; start < to; start += TimeSpan.FromHours(1))
        {
            buckets.Add(new KpiBucket(kpiDefinitionId, start, 5000, 167, 100, 200, 120, 10, null));
        }
        await _buckets.UpsertAsync(buckets);
    }

    [Fact]
    public async Task Ingestion_FirstRun_BackfillsRequiredHistory()
    {
        var definition = A2Kpi.Definition(sampleSize: 4000, lookback: TimeSpan.FromHours(24));
        var source = HourlySource();

        var result = await ServiceWithEvaluator([definition], [source]).RunAsync();

        var call = Assert.Single(source.Calls);
        Assert.Equal(HistoryStart, call.From);
        Assert.Equal(LastClosedBucket, call.To);
        Assert.Equal(167, call.SamplesPerBucket);
        Assert.Equal(7 * 24 + 1, result.BucketsWritten);
        Assert.All(_buckets.All, b => Assert.Equal(167, b.SampledCount));
        Assert.Equal(LastClosedBucket, _watermarks.Values[definition.KpiDefinitionId]);

        // The monitor can evaluate the latest closed bucket right after this first ingestion.
        var evaluator = new KpiEvaluator();
        var latest = LastClosedBucket - TimeSpan.FromHours(1);
        var history = await _buckets.GetRangeAsync(definition.KpiDefinitionId, latest - evaluator.GetRequiredHistory(definition), latest);
        var toEvaluate = await _buckets.GetRangeAsync(definition.KpiDefinitionId, latest, LastClosedBucket);
        var evaluation = Assert.Single(evaluator.Evaluate(definition, KpiDetectionState.Initial(definition.KpiDefinitionId), history, toEvaluate));
        Assert.NotEqual(KpiBucketVerdict.NotEvaluated, evaluation.Verdict);
    }

    [Fact]
    public async Task Ingestion_HistoryGap_BackfillsOnlyMissingRange()
    {
        var definition = A2Kpi.Definition(sampleSize: 4000);
        var dayAgo = LastClosedBucket - TimeSpan.FromHours(24);
        await SeedBucketsAsync(definition.KpiDefinitionId, dayAgo, LastClosedBucket);
        _watermarks.Values[definition.KpiDefinitionId] = LastClosedBucket;
        var source = HourlySource();

        var result = await ServiceWithEvaluator([definition], [source], Unlimited).RunAsync();

        var call = Assert.Single(source.Calls);
        Assert.Equal(HistoryStart, call.From);
        Assert.Equal(dayAgo, call.To);
        Assert.Equal(167, call.SamplesPerBucket);
        Assert.Equal(LastClosedBucket, _watermarks.Values[definition.KpiDefinitionId]);
        Assert.Empty(_watermarks.SetCalls);
        Assert.Equal(145, result.BucketsWritten);
        Assert.Equal(145 * 167, result.SamplesWritten);
        Assert.Contains(
            (DaemonActivityLevel.Info, DaemonActivityKinds.IngestionKpi,
                $"Latency/hotels: backfilled history 145 bucket(s) [2026-10-29 13:00–2026-11-04 14:00), {145 * 167} sample(s)"),
            _activity.Entries);

        // Complete now: the next run does not fetch it again.
        await ServiceWithEvaluator([definition], [source], Unlimited).RunAsync();
        Assert.Single(source.Calls);
    }

    [Fact]
    public async Task Ingestion_NoGap_DoesNotBackfill()
    {
        var definition = A2Kpi.Definition();
        var watermark = LastClosedBucket - TimeSpan.FromHours(1);
        await SeedBucketsAsync(definition.KpiDefinitionId, HistoryStart, watermark);
        _watermarks.Values[definition.KpiDefinitionId] = watermark;
        var source = HourlySource();

        await ServiceWithEvaluator([definition], [source], Unlimited).RunAsync();

        var call = Assert.Single(source.Calls);
        Assert.Equal(watermark, call.From);
        Assert.Equal(LastClosedBucket, call.To);
        Assert.Equal(LastClosedBucket, _watermarks.Values[definition.KpiDefinitionId]);
        Assert.DoesNotContain(_activity.Entries, e => e.Message.Contains("backfilled", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Ingestion_BackfillRespectsMaxRowsPerCycle()
    {
        var definition = A2Kpi.Definition(sampleSize: 4000);
        var dayAgo = LastClosedBucket - TimeSpan.FromDays(1);
        await SeedBucketsAsync(definition.KpiDefinitionId, dayAgo, LastClosedBucket);
        _watermarks.Values[definition.KpiDefinitionId] = LastClosedBucket;
        var source = HourlySource();
        // Room for 48 buckets x 167 samples: the 145-bucket gap goes in 2-day chunks, newest first.
        var options = new HealthMonitoringOptions { Enabled = true, MaxRowsPerCycle = 48 * 167 };

        for (var i = 0; i < 5; i++)
        {
            var result = await ServiceWithEvaluator([definition], [source], options).RunAsync();
            Assert.True(result.SamplesWritten <= options.MaxRowsPerCycle);
        }

        Assert.Equal(
            [
                (LastClosedBucket - TimeSpan.FromDays(3), dayAgo),
                (LastClosedBucket - TimeSpan.FromDays(5), LastClosedBucket - TimeSpan.FromDays(3)),
                (LastClosedBucket - TimeSpan.FromDays(7), LastClosedBucket - TimeSpan.FromDays(5)),
                (HistoryStart, LastClosedBucket - TimeSpan.FromDays(7)),
            ],
            source.Calls.Select(c => (c.From, c.To)).ToArray());
        var stored = await _buckets.GetRangeAsync(definition.KpiDefinitionId, HistoryStart, LastClosedBucket);
        Assert.Equal(7 * 24 + 1, stored.Count);
        Assert.Empty(_watermarks.SetCalls);
    }

    [Fact]
    public async Task Ingestion_BackfillSourceFailure_KeepsWatermarkAndRetries()
    {
        var definition = A2Kpi.Definition();
        var dayAgo = LastClosedBucket - TimeSpan.FromHours(24);
        await SeedBucketsAsync(definition.KpiDefinitionId, dayAgo, LastClosedBucket);
        _watermarks.Values[definition.KpiDefinitionId] = LastClosedBucket;

        var throwing = new A2KpiSource { Respond = (_, _, _, _) => throw new InvalidOperationException("App Insights query 503") };
        await ServiceWithEvaluator([definition], [throwing], Unlimited).RunAsync();

        var unavailable = new A2KpiSource { Respond = (_, _, _, _) => throw new KpiSourceUnavailableException("not configured") };
        var skipped = await ServiceWithEvaluator([definition], [unavailable], Unlimited).RunAsync();

        Assert.Equal((HistoryStart, dayAgo), (Assert.Single(throwing.Calls).From, throwing.Calls[0].To));
        Assert.Equal(1, skipped.Skipped);
        Assert.Contains(_activity.Entries, e => e.Level == DaemonActivityLevel.Warning && e.Message == "Latency/hotels: source unavailable — not configured");
        Assert.Equal(LastClosedBucket, _watermarks.Values[definition.KpiDefinitionId]);
        Assert.Empty(_watermarks.SetCalls);
        Assert.Equal(24, _buckets.All.Count());

        var healthy = HourlySource();
        await ServiceWithEvaluator([definition], [healthy], Unlimited).RunAsync();

        var retry = Assert.Single(healthy.Calls);
        Assert.Equal(HistoryStart, retry.From);
        Assert.Equal(dayAgo, retry.To);
        Assert.Empty(_watermarks.SetCalls);
    }

    [Fact]
    public async Task Ingestion_WithoutEvaluator_KeepsOldBehaviour()
    {
        var definition = A2Kpi.Definition(lookback: TimeSpan.FromHours(24));
        var source = HourlySource();

        await Service([definition], [source]).RunAsync();

        var call = Assert.Single(source.Calls);
        Assert.Equal(LastClosedBucket - definition.LookbackWindow, call.From);
        Assert.Equal(LastClosedBucket, call.To);

        // A later run with missing method history still only fetches from the watermark.
        _watermarks.Values[definition.KpiDefinitionId] = LastClosedBucket - TimeSpan.FromHours(1);
        var later = HourlySource();
        await Service([definition], [later], now: Now.AddHours(1)).RunAsync();
        var next = Assert.Single(later.Calls);
        Assert.Equal(LastClosedBucket - TimeSpan.FromHours(1), next.From);
        Assert.Equal(LastClosedBucket.AddHours(1), next.To);
    }

    [Fact]
    public async Task Ingestion_BackfillEmptyChunk_AdvancesAndStops()
    {
        var definition = A2Kpi.Definition(sampleSize: 4000);
        var dayAgo = LastClosedBucket - TimeSpan.FromDays(1);
        var threeDaysAgo = LastClosedBucket - TimeSpan.FromDays(3);
        await SeedBucketsAsync(definition.KpiDefinitionId, dayAgo, LastClosedBucket);
        _watermarks.Values[definition.KpiDefinitionId] = LastClosedBucket;
        // The newest chunk had no traffic at all; older days did.
        var source = new A2KpiSource
        {
            Respond = (d, f, t, n) => f >= threeDaysAgo ? new KpiSourceResult([], []) : A2KpiSource.Hourly(d, f, t, n, 500),
        };
        var options = new HealthMonitoringOptions { Enabled = true, MaxRowsPerCycle = 48 * 167 };

        for (var i = 0; i < 6; i++)
        {
            await ServiceWithEvaluator([definition], [source], options).RunAsync();
        }

        Assert.Equal(
            [
                (threeDaysAgo, dayAgo),
                (LastClosedBucket - TimeSpan.FromDays(5), threeDaysAgo),
                (LastClosedBucket - TimeSpan.FromDays(7), LastClosedBucket - TimeSpan.FromDays(5)),
                (HistoryStart, LastClosedBucket - TimeSpan.FromDays(7)),
            ],
            source.Calls.Select(c => (c.From, c.To)).ToArray());
        Assert.Contains(_activity.Entries, e => e.Message ==
            "Latency/hotels: backfilled history 0 bucket(s) +48 empty [2026-11-02 14:00–2026-11-04 14:00), 0 sample(s)");
        var stored = await _buckets.GetRangeAsync(definition.KpiDefinitionId, HistoryStart, LastClosedBucket);
        Assert.Equal(7 * 24 + 1, stored.Count);
        Assert.Equal(48, stored.Count(b => b.TotalCount == 0 && b.SampledCount == 0 && b.P95 is null && b.Value is null));
        Assert.Empty(_watermarks.SetCalls);
    }

    [Fact]
    public async Task Ingestion_BackfillZeroTrafficOldestHour_DoesNotRequery()
    {
        var definition = A2Kpi.Definition(sampleSize: 4000);
        var dayAgo = LastClosedBucket - TimeSpan.FromHours(24);
        await SeedBucketsAsync(definition.KpiDefinitionId, dayAgo, LastClosedBucket);
        _watermarks.Values[definition.KpiDefinitionId] = LastClosedBucket;
        // No traffic in the oldest required hour.
        var source = new A2KpiSource { Respond = (d, f, t, n) => A2KpiSource.Hourly(d, f + d.BucketSize, t, n, 500) };

        await ServiceWithEvaluator([definition], [source], Unlimited).RunAsync();
        await ServiceWithEvaluator([definition], [source], Unlimited).RunAsync();
        await ServiceWithEvaluator([definition], [source], Unlimited, Now.AddMinutes(15)).RunAsync();

        var call = Assert.Single(source.Calls);
        Assert.Equal((HistoryStart, dayAgo), (call.From, call.To));
        var oldest = Assert.Single(await _buckets.GetRangeAsync(definition.KpiDefinitionId, HistoryStart, HistoryStart.AddHours(1)));
        Assert.Equal(new KpiBucket(definition.KpiDefinitionId, HistoryStart, 0, 0, null, null, null, null, null), oldest);
        Assert.Contains(_activity.Entries, e => e.Message ==
            $"Latency/hotels: backfilled history 144 bucket(s) +1 empty [2026-10-29 13:00–2026-11-04 14:00), {144 * 167} sample(s)");
        Assert.Single(_activity.Entries, e => e.Message.Contains("backfilled", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Ingestion_BackfillFailure_NormalFetchStillAdvancesWatermark()
    {
        var definition = A2Kpi.Definition();
        var dayAgo = LastClosedBucket - TimeSpan.FromHours(24);
        var watermark = LastClosedBucket - TimeSpan.FromHours(1);
        await SeedBucketsAsync(definition.KpiDefinitionId, dayAgo, watermark);
        _watermarks.Values[definition.KpiDefinitionId] = watermark;
        var source = new A2KpiSource
        {
            Respond = (d, f, t, n) => f < dayAgo
                ? throw new InvalidOperationException("App Insights query 503")
                : A2KpiSource.Hourly(d, f, t, n, 5),
        };

        var result = await ServiceWithEvaluator([definition], [source], Unlimited).RunAsync();

        Assert.Equal(
            [(HistoryStart, dayAgo), (watermark, LastClosedBucket)],
            source.Calls.Select(c => (c.From, c.To)).ToArray());
        Assert.Equal(1, result.KpisProcessed);
        Assert.Equal(0, result.Skipped);
        Assert.Equal(1, result.BucketsWritten);
        Assert.Equal(LastClosedBucket, _watermarks.Values[definition.KpiDefinitionId]);
        Assert.Contains((DaemonActivityLevel.Error, DaemonActivityKinds.Error, "Latency/hotels: history backfill failed — App Insights query 503"), _activity.Entries);
    }

    [Fact]
    public async Task Ingestion_BackfillOkButNormalFetchFails_CountsKpiOnlyAsSkipped()
    {
        var definition = A2Kpi.Definition();
        var dayAgo = LastClosedBucket - TimeSpan.FromHours(24);
        var watermark = LastClosedBucket - TimeSpan.FromHours(1);
        await SeedBucketsAsync(definition.KpiDefinitionId, dayAgo, watermark);
        _watermarks.Values[definition.KpiDefinitionId] = watermark;
        var source = new A2KpiSource
        {
            Respond = (d, f, t, n) => f >= watermark
                ? throw new InvalidOperationException("timeout")
                : A2KpiSource.Hourly(d, f, t, n, 5),
        };

        var result = await ServiceWithEvaluator([definition], [source], Unlimited).RunAsync();

        Assert.Equal(0, result.KpisProcessed);
        Assert.Equal(1, result.Skipped);
        Assert.Equal(145, result.BucketsWritten);
        Assert.Equal(watermark, _watermarks.Values[definition.KpiDefinitionId]);
    }

    [Fact]
    public async Task Ingestion_BackfillChunkUsesRemainingBudget()
    {
        var caughtUp = A2Kpi.Definition(id: 1, sampleSize: 4000);
        var gapped = A2Kpi.Definition(id: 2, sampleSize: 4000);
        var dayAgo = LastClosedBucket - TimeSpan.FromDays(1);
        // KPI 1 has its history and 24 new buckets (24 x 167 samples) to ingest first.
        await SeedBucketsAsync(1, HistoryStart, dayAgo);
        _watermarks.Values[1] = dayAgo;
        await SeedBucketsAsync(2, dayAgo, LastClosedBucket);
        _watermarks.Values[2] = LastClosedBucket;
        var source = HourlySource();
        var options = new HealthMonitoringOptions { Enabled = true, MaxRowsPerCycle = (24 + 48) * 167 };

        await ServiceWithEvaluator([caughtUp, gapped], [source], options).RunAsync();

        // 48 buckets of budget remain after KPI 1, so KPI 2 gets a 2-day chunk instead of 3.
        Assert.Equal(
            [(1, dayAgo, LastClosedBucket), (2, LastClosedBucket - TimeSpan.FromDays(3), dayAgo)],
            source.Calls.Select(c => (c.KpiDefinitionId, c.From, c.To)).ToArray());
    }

    [Fact]
    public async Task Ingestion_BackfilledEmptyBucket_EvaluatorTreatsAsNoTraffic()
    {
        var definition = A2Kpi.Definition();
        await SeedBucketsAsync(definition.KpiDefinitionId, LastClosedBucket - TimeSpan.FromHours(24), LastClosedBucket);
        _watermarks.Values[definition.KpiDefinitionId] = LastClosedBucket;
        var silent = new A2KpiSource();

        await ServiceWithEvaluator([definition], [silent], Unlimited).RunAsync();

        var empty = Assert.Single(await _buckets.GetRangeAsync(definition.KpiDefinitionId, HistoryStart, HistoryStart.AddHours(1)));
        var evaluator = new KpiEvaluator();
        KpiEvaluation Evaluate(KpiDefinition d) =>
            Assert.Single(evaluator.Evaluate(d, KpiDetectionState.Initial(d.KpiDefinitionId), [], [empty]));

        Assert.Equal(KpiBucketVerdict.NotEvaluated, Evaluate(definition).Verdict);
        Assert.Equal(KpiBucketVerdict.NotEvaluated, Evaluate(definition with { Aggregation = KpiAggregation.Value, MinObservationsPerBucket = 0 }).Verdict);

        var count = Evaluate(definition with
        {
            Aggregation = KpiAggregation.Count,
            MinObservationsPerBucket = 0,
            Direction = KpiDirection.Decrease,
            DetectionMethod = DetectionMethodKind.StaticThreshold,
            MethodParamsJson = """{"threshold":10}""",
        });
        Assert.NotEqual(KpiBucketVerdict.NotEvaluated, count.Verdict);
        Assert.Equal(0, count.ObservedValue);
    }
}

/// <summary>Records every daemon activity entry of the runs it starts.</summary>
internal sealed class H1ActivityRecorder : IDaemonActivityRecorder
{
    public List<(DaemonActivityLevel Level, string Kind, string Message)> Entries { get; } = [];

    public IDaemonRun BeginRun(string daemonKey) => new Run(this, daemonKey);

    private sealed class Run(H1ActivityRecorder owner, string daemonKey) : IDaemonRun
    {
        public Guid RunId { get; } = Guid.NewGuid();
        public string DaemonKey { get; } = daemonKey;
        public void Info(string kind, string message, object? data = null) => owner.Entries.Add((DaemonActivityLevel.Info, kind, message));
        public void Warning(string kind, string message, object? data = null) => owner.Entries.Add((DaemonActivityLevel.Warning, kind, message));
        public void Error(string kind, string message, object? data = null) => owner.Entries.Add((DaemonActivityLevel.Error, kind, message));
        public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
