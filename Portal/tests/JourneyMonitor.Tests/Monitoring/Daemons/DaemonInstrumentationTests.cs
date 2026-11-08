using System.Text.Json;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring.Daemons;
using JourneyMonitor.Core.Monitoring.Inspector;
using JourneyMonitor.Core.Monitoring.Kpi;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Core.UseCases;
using JourneyMonitor.Tests.Monitoring.Kpi;
using JourneyMonitor.Tests.Monitoring.Kpi.Fakes;
using JourneyMonitor.Tests.Spec1000.A2;
using JourneyMonitor.Tests.Spec1000.A5;
using Microsoft.Extensions.Logging.Abstractions;

namespace JourneyMonitor.Tests.Spec1000.D1;

/// <summary>KpiIngestionService holds a static run lock: serial with the A2 ingestion tests.</summary>
[Collection(A2IngestionRunLockCollection.Name)]
public sealed class D1IngestionActivityTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 14, 37, 12, TimeSpan.Zero);

    private readonly A2BucketStore _buckets = new();
    private readonly A2SampleStore _samples = new();
    private readonly A2WatermarkStore _watermarks = new();
    private readonly D1ActivityHarness _activity = new(new D1FixedTimeProvider(Now));

    private KpiIngestionService Service(KpiDefinition[] definitions, IKpiSource source, HealthMonitoringOptions? options = null) => new(
        new A2DefinitionStore(definitions),
        [source],
        _buckets,
        _samples,
        _watermarks,
        Microsoft.Extensions.Options.Options.Create(options ?? new HealthMonitoringOptions { Enabled = true }),
        new A2FixedTimeProvider(Now),
        NullLogger<KpiIngestionService>.Instance,
        null,
        _activity.Recorder,
        _activity.Tracker,
        _activity.Log);

    [Fact]
    public async Task Ingestion_WritesActivityForEachKpiAndRun()
    {
        var source = new A2KpiSource { Respond = (d, from, to, per) => A2KpiSource.Hourly(d, from, to, per, 2) };
        var result = await Service([A2Kpi.Definition(1), A2Kpi.Definition(2, domain: "cars")], source).RunAsync();

        var entries = await _activity.EntriesAsync(DaemonKeys.KpiIngestion);
        Assert.Equal(
            [DaemonActivityKinds.RunStarted, DaemonActivityKinds.IngestionKpi, DaemonActivityKinds.IngestionKpi, DaemonActivityKinds.RunCompleted],
            entries.Select(e => e.Kind).ToArray());
        Assert.Single(entries.Select(e => e.RunId).Distinct());
        Assert.Equal("Run started", entries[0].Message);

        var buckets1 = _buckets.All.Count(b => b.KpiDefinitionId == 1);
        var samples1 = _samples.Samples.Count(s => s.KpiDefinitionId == 1);
        Assert.Equal($"Latency/hotels: {buckets1} bucket(s) [14:00–14:00), {samples1} sample(s), watermark → 14:00", entries[1].Message);
        Assert.StartsWith("Latency/cars: ", entries[2].Message);
        using (var data = JsonDocument.Parse(entries[1].DataJson!))
        {
            Assert.Equal(1, data.RootElement.GetProperty("kpiDefinitionId").GetInt32());
            Assert.Equal(buckets1, data.RootElement.GetProperty("buckets").GetInt32());
            Assert.Equal(samples1, data.RootElement.GetProperty("samples").GetInt32());
        }

        Assert.Equal(
            $"2 KPI(s) processed, {result.BucketsWritten} bucket(s), {result.SamplesWritten} sample(s) written, 0 skipped",
            entries[3].Message);
        Assert.Equal(DaemonActivityLevel.Info, entries[3].Level);
        Assert.Equal(Now, await _activity.Tracker.GetLastRunAsync(DaemonKeys.KpiIngestion));
    }

    [Fact]
    public async Task Ingestion_UpToDateRunSaysUpToDate()
    {
        _watermarks.Values[1] = new DateTimeOffset(2026, 11, 5, 14, 0, 0, TimeSpan.Zero);
        var source = new A2KpiSource();

        var result = await Service([A2Kpi.Definition(1)], source).RunAsync();

        Assert.Equal(new KpiIngestionRunResult(0, 0, 0, 0), result);
        Assert.Empty(source.Calls);
        var entries = await _activity.EntriesAsync(DaemonKeys.KpiIngestion);
        Assert.Equal(
            [DaemonActivityKinds.RunStarted, DaemonActivityKinds.IngestionKpi, DaemonActivityKinds.RunCompleted],
            entries.Select(e => e.Kind).ToArray());
        Assert.Equal("Latency/hotels: up to date (watermark 14:00, next bucket closes 15:00)", entries[1].Message);
        Assert.Equal("Up to date — nothing to ingest", entries[2].Message);
    }

    [Fact]
    public async Task Ingestion_SourceFailuresAreLoggedAsWarningAndError()
    {
        var unavailable = new A2KpiSource { Respond = (_, _, _, _) => throw new KpiSourceUnavailableException("no workspace") };
        await Service([A2Kpi.Definition(1), A2Kpi.Definition(2, sourceKey: "other:source")], unavailable).RunAsync();

        var throwing = new A2KpiSource { Respond = (_, _, _, _) => throw new InvalidOperationException("timeout") };
        await Service([A2Kpi.Definition(3)], throwing).RunAsync();

        var messages = (await _activity.EntriesAsync()).Select(e => (e.Level, e.Kind, e.Message)).ToList();
        Assert.Contains((DaemonActivityLevel.Warning, DaemonActivityKinds.IngestionKpi, "Latency/hotels: source unavailable — no workspace"), messages);
        Assert.Contains((DaemonActivityLevel.Warning, DaemonActivityKinds.IngestionKpi, "Latency/hotels: no source for 'other:source'"), messages);
        Assert.Contains((DaemonActivityLevel.Error, DaemonActivityKinds.Error, "Latency/hotels: fetch failed — timeout"), messages);
        Assert.Contains((DaemonActivityLevel.Warning, DaemonActivityKinds.RunCompleted, "0 KPI(s) processed, 0 bucket(s), 0 sample(s) written, 1 skipped"), messages);
    }

    [Fact]
    public async Task Ingestion_DisabledOrFailingStoreDoesNotChangeTheRun()
    {
        var disabled = await Service([A2Kpi.Definition(1)], new A2KpiSource(), new HealthMonitoringOptions { Enabled = false }).RunAsync();
        Assert.Equal(new KpiIngestionRunResult(0, 0, 0, 0), disabled);
        Assert.Empty(await _activity.EntriesAsync());

        var failing = new KpiIngestionService(
            new A2DefinitionStore(A2Kpi.Definition(1)),
            [new A2KpiSource { Respond = (d, from, to, per) => A2KpiSource.Hourly(d, from, to, per, 1) }],
            _buckets, _samples, _watermarks,
            Microsoft.Extensions.Options.Options.Create(new HealthMonitoringOptions { Enabled = true }),
            new A2FixedTimeProvider(Now),
            NullLogger<KpiIngestionService>.Instance,
            null,
            new DaemonActivityRecorder(new D1ThrowingActivityLog(), TimeProvider.System, NullLogger<DaemonActivityRecorder>.Instance),
            null,
            new D1ThrowingActivityLog());

        var result = await failing.RunAsync();
        Assert.Equal(1, result.KpisProcessed);
        Assert.Equal(24, result.BucketsWritten);
    }
}

/// <summary>PillarHealthMonitorService holds static per-pillar locks: serial with the A1 monitor tests.</summary>
[Collection(A1MonitorRunLockCollection.Name)]
public sealed class D1MonitorActivityTests
{
    private static readonly DateTimeOffset Day = new(2026, 11, 4, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = Day.AddHours(17).AddMinutes(10);

    private readonly A1InMemoryDefinitionStore _definitions = new();
    private readonly A1InMemoryBucketStore _buckets = new();
    private readonly A1InMemoryStateStore _states = new();
    private readonly A1InMemoryAnomalyStore _anomalies = new();
    private readonly A1InMemoryWatermarkStore _watermarks = new();
    private readonly A1RecordingTriageSubmitter _triage = new();
    private readonly HealthMonitoringOptions _options = new() { Enabled = true, Pillars = ["application"] };
    private readonly D1ActivityHarness _activity = new(new D1FixedTimeProvider(Now));

    private PillarHealthMonitorService CreateService() => new(
        _definitions, _buckets, _states, _anomalies, _watermarks, new KpiEvaluator(), _triage,
        Microsoft.Extensions.Options.Options.Create(_options), new A1FakeTimeProvider(Now), NullLogger<PillarHealthMonitorService>.Instance,
        _activity.Recorder, _activity.Tracker);

    private static KpiDefinition StaticLatency(int id, string stage = "") =>
        KpiEvaluatorTests.Definition(DetectionMethodKind.StaticThreshold, KpiDirection.Increase, "{\"threshold\":3000}") with
        {
            KpiDefinitionId = id,
            Stage = stage,
        };

    private void ArrangeTrigger()
    {
        _definitions.Definitions.Add(StaticLatency(1, "book"));
        _states.States[1] = new KpiDetectionState(1, 1, Day.AddHours(15), null, Now.AddHours(-1));
        _watermarks.Watermarks[1] = Now;
        _buckets.Buckets.Add(new KpiBucket(1, Day.AddHours(16), 100, 0, null, 3500, null, null, null));
    }

    [Fact]
    public async Task Monitor_WritesKpiEvaluatedAnomalyAndTriageActivity()
    {
        ArrangeTrigger();
        // KPI 2 evaluated two minutes ago: not due yet.
        _definitions.Definitions.Add(StaticLatency(2));
        _states.States[2] = new KpiDetectionState(2, 0, Day.AddHours(15), null, Now.AddMinutes(-2));

        var result = await CreateService().RunAsync(KpiPillar.Application);

        Assert.Equal(new PillarMonitorRunResult(1, 1, 0, 1), result);
        var entries = await _activity.EntriesAsync(DaemonKeys.ApplicationHealthMonitor);
        Assert.Equal(
        [
            DaemonActivityKinds.RunStarted, DaemonActivityKinds.KpiEvaluated, DaemonActivityKinds.Anomaly,
            DaemonActivityKinds.TriageSubmitted, DaemonActivityKinds.KpiEvaluated, DaemonActivityKinds.RunCompleted,
        ], entries.Select(e => e.Kind).ToArray());
        Assert.Single(entries.Select(e => e.RunId).Distinct());

        Assert.StartsWith("Latency/hotels/book 16:00: ", entries[1].Message);
        using (var data = JsonDocument.Parse(entries[1].DataJson!))
        {
            Assert.Equal("triggered", data.RootElement.GetProperty("outcome").GetString());
            Assert.Equal(2, data.RootElement.GetProperty("counter").GetInt32());
            Assert.Equal(3500, data.RootElement.GetProperty("observed").GetDouble());
        }

        var anomaly = Assert.Single(_anomalies.Anomalies);
        Assert.Equal(DaemonActivityLevel.Warning, entries[2].Level);
        Assert.Equal($"Latency/hotels: {anomaly.Detection}", entries[2].Message);
        Assert.Equal($"Investigation {_triage.NextInvestigationId} queued", entries[3].Message);
        Assert.Equal("Latency/hotels: not due (next evaluation 17:13)", entries[4].Message);
        Assert.Equal("1 evaluated, 1 triggered, 0 suppressed, 1 skipped", entries[5].Message);
        Assert.Equal(DaemonActivityLevel.Info, entries[5].Level);
        Assert.Equal(Now, await _activity.Tracker.GetLastRunAsync(DaemonKeys.ApplicationHealthMonitor));
    }

    [Fact]
    public async Task Monitor_DisabledWritesNoActivity()
    {
        ArrangeTrigger();
        _options.Enabled = false;

        await CreateService().RunAsync(KpiPillar.Application);
        _options.Enabled = true;
        await CreateService().RunAsync(KpiPillar.Business);

        Assert.Empty(await _activity.EntriesAsync());
        Assert.Null(await _activity.Tracker.GetLastRunAsync(DaemonKeys.ApplicationHealthMonitor));
        Assert.Null(await _activity.Tracker.GetLastRunAsync(DaemonKeys.BusinessHealthMonitor));
    }

    [Fact]
    public async Task Monitor_TriageFailureIsAnErrorAndRunCompletesAsWarning()
    {
        ArrangeTrigger();
        _triage.Throw = true;

        var result = await CreateService().RunAsync(KpiPillar.Application);

        Assert.Equal(new PillarMonitorRunResult(1, 1, 0, 0), result);
        var entries = await _activity.EntriesAsync(DaemonKeys.ApplicationHealthMonitor);
        Assert.Contains(entries, e => e.Kind == DaemonActivityKinds.Error && e.Message == "Latency/hotels: triage queue unavailable");
        Assert.Equal(DaemonActivityLevel.Warning, entries[^1].Level);
        Assert.Equal(DaemonActivityKinds.RunCompleted, entries[^1].Kind);
    }
}

public sealed class D1TriageActivityTests
{
    private static TriageMonkeyService Service(A5InMemoryTriageStore store, D1ActivityHarness activity, TriageMode mode, string llmJson) => new(
        store,
        A5TriageFixtures.AllAvailableProviders(),
        A5FakeLlmClient.Returning(llmJson),
        new A5PauseResolver(),
        new A5BudgetResolver(),
        new A5RecordingNotificationService(),
        Microsoft.Extensions.Options.Options.Create(new TriageOptions { Mode = mode }),
        new A5FixedTimeProvider(A5TriageFixtures.Now),
        NullLogger<TriageMonkeyService>.Instance,
        activity.Recorder,
        activity.Tracker);

    [Fact]
    public async Task Triage_WritesStartedAndCompletedActivity()
    {
        var store = new A5InMemoryTriageStore();
        var investigation = A5TriageFixtures.Queued();
        await store.CreateAsync(investigation);
        var activity = new D1ActivityHarness(new D1FixedTimeProvider(A5TriageFixtures.Now));

        var result = await Service(store, activity, TriageMode.Shadow, A5TriageFixtures.AmberJson).InvestigateAsync(investigation.InvestigationId, CancellationToken.None);

        Assert.NotNull(result);
        var entries = await activity.EntriesAsync(DaemonKeys.TriageMonkey);
        Assert.Equal(
        [
            DaemonActivityKinds.RunStarted, DaemonActivityKinds.InvestigationStarted,
            DaemonActivityKinds.InvestigationCompleted, DaemonActivityKinds.RunCompleted,
        ], entries.Select(e => e.Kind).ToArray());
        Assert.Single(entries.Select(e => e.RunId).Distinct());
        Assert.Equal("Application Health · Latency · hotels: investigating (daemon)", entries[1].Message);
        Assert.Equal("amber · Latency doubled in hotels.", entries[2].Message);
        Assert.Equal(DaemonActivityLevel.Info, entries[2].Level);
        using (var data = JsonDocument.Parse(entries[2].DataJson!))
        {
            Assert.Equal("amber", data.RootElement.GetProperty("alertLevel").GetString());
            Assert.False(data.RootElement.GetProperty("notified").GetBoolean());
            Assert.Equal(investigation.InvestigationId, data.RootElement.GetProperty("investigationId").GetGuid());
            Assert.Equal(0, data.RootElement.GetProperty("unavailableDimensions").GetArrayLength());
        }
        Assert.Equal($"Investigation {investigation.InvestigationId} Completed", entries[3].Message);
        Assert.Equal(A5TriageFixtures.Now, await activity.Tracker.GetLastRunAsync(DaemonKeys.TriageMonkey));
    }

    [Fact]
    public async Task Triage_RedAlertIsWarningAndModeOffWritesNothing()
    {
        var store = new A5InMemoryTriageStore();
        var red = A5TriageFixtures.Queued();
        await store.CreateAsync(red);
        var activity = new D1ActivityHarness();
        await Service(store, activity, TriageMode.Shadow, A5TriageFixtures.RedAlertJson).InvestigateAsync(red.InvestigationId, CancellationToken.None);

        var completed = (await activity.EntriesAsync()).Single(e => e.Kind == DaemonActivityKinds.InvestigationCompleted);
        Assert.Equal(DaemonActivityLevel.Warning, completed.Level);
        Assert.StartsWith("red_alert · ", completed.Message);

        var offStore = new A5InMemoryTriageStore();
        var off = A5TriageFixtures.Queued();
        await offStore.CreateAsync(off);
        var offActivity = new D1ActivityHarness();
        var result = await Service(offStore, offActivity, TriageMode.Off, A5TriageFixtures.AmberJson).InvestigateAsync(off.InvestigationId, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Empty(await offActivity.EntriesAsync());
        Assert.Null(await offActivity.Tracker.GetLastRunAsync(DaemonKeys.TriageMonkey));
    }
}
