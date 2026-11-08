using System.Text.Json;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring.Daemons;
using JourneyMonitor.Infrastructure.Monitoring;
using Microsoft.Extensions.Logging;

namespace JourneyMonitor.Tests.Spec1000.D1;

public sealed class DaemonActivityRecorderTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 10, 15, 0, TimeSpan.Zero);

    [Fact]
    public async Task Recorder_NeverThrowsWhenStoreFails()
    {
        var store = new D1ThrowingActivityLog();
        var logger = new D1ListLogger<DaemonActivityRecorder>();
        var recorder = new DaemonActivityRecorder(store, new D1FixedTimeProvider(Now), logger);

        var run = recorder.BeginRun(DaemonKeys.KpiIngestion);
        run.Info(DaemonActivityKinds.RunStarted, "Run started");
        run.Error(DaemonActivityKinds.Error, "boom", new { kpiDefinitionId = 1 });

        var flush = Record.ExceptionAsync(() => run.FlushAsync());
        Assert.Null(await flush);
        Assert.Equal(1, store.AppendCalls);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Exception is InvalidOperationException);

        // Auto-flush path (every 50 entries) must not throw either, and a later flush still completes.
        for (var i = 0; i < DaemonActivityRecorder.AutoFlushThreshold + 5; i++) run.Info(DaemonActivityKinds.KpiEvaluated, $"entry {i}");
        Assert.Null(await Record.ExceptionAsync(() => run.FlushAsync()));
        Assert.Equal(3, store.AppendCalls);
    }

    [Fact]
    public async Task Recorder_EntriesShareRunIdAndKeepOrder()
    {
        var log = new InMemoryDaemonActivityLog();
        var recorder = new DaemonActivityRecorder(log, new D1FixedTimeProvider(Now), new D1ListLogger<DaemonActivityRecorder>());

        var run = recorder.BeginRun(DaemonKeys.ApplicationHealthMonitor);
        run.Info(DaemonActivityKinds.RunStarted, "Run started");
        run.Warning(DaemonActivityKinds.Anomaly, "Latency/hotels: up", new { kpiDefinitionId = 7, anomalyId = 11L });
        run.Info(DaemonActivityKinds.RunCompleted, "1 evaluated, 1 triggered, 0 suppressed, 0 skipped");
        await run.FlushAsync();

        var entries = (await log.GetAsync(0, null, null, 100)).Entries;
        Assert.Equal(3, entries.Count);
        Assert.All(entries, e => Assert.Equal(run.RunId, e.RunId));
        Assert.All(entries, e => Assert.Equal(DaemonKeys.ApplicationHealthMonitor, e.DaemonKey));
        Assert.All(entries, e => Assert.Equal(Now, e.At));
        Assert.NotEqual(Guid.Empty, run.RunId);
        Assert.Equal(
            [DaemonActivityKinds.RunStarted, DaemonActivityKinds.Anomaly, DaemonActivityKinds.RunCompleted],
            entries.Select(e => e.Kind).ToArray());
        Assert.Equal(DaemonActivityLevel.Warning, entries[1].Level);
        Assert.True(entries[0].ActivityId < entries[1].ActivityId && entries[1].ActivityId < entries[2].ActivityId);

        using var data = JsonDocument.Parse(entries[1].DataJson!);
        Assert.Equal(7, data.RootElement.GetProperty("kpiDefinitionId").GetInt32());
        Assert.Equal(11, data.RootElement.GetProperty("anomalyId").GetInt64());
        Assert.Null(entries[0].DataJson);

        // A second run gets a new RunId.
        Assert.NotEqual(run.RunId, recorder.BeginRun(DaemonKeys.ApplicationHealthMonitor).RunId);
    }

    [Fact]
    public async Task Recorder_TruncatesMessageTo1000()
    {
        var log = new InMemoryDaemonActivityLog();
        var recorder = new DaemonActivityRecorder(log, new D1FixedTimeProvider(Now), new D1ListLogger<DaemonActivityRecorder>());

        var run = recorder.BeginRun(DaemonKeys.TriageMonkey);
        run.Info(DaemonActivityKinds.InvestigationCompleted, new string('x', 1500));
        await run.FlushAsync();

        var entry = Assert.Single((await log.GetAsync(0, null, null, 10)).Entries);
        Assert.Equal(1000, entry.Message.Length);
    }

    [Fact]
    public async Task Recorder_AutoFlushesEvery50EntriesInOrder()
    {
        var log = new InMemoryDaemonActivityLog();
        var recorder = new DaemonActivityRecorder(log, new D1FixedTimeProvider(Now), new D1ListLogger<DaemonActivityRecorder>());

        var run = recorder.BeginRun(DaemonKeys.KpiIngestion);
        for (var i = 0; i < 120; i++) run.Info(DaemonActivityKinds.IngestionKpi, $"entry {i}");
        await run.FlushAsync();

        var entries = (await log.GetAsync(0, null, null, 1000)).Entries;
        Assert.Equal(Enumerable.Range(0, 120).Select(i => $"entry {i}"), entries.Select(e => e.Message));
    }
}
