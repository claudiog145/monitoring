using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring.Kpi;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Tests.Monitoring.Kpi.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Tests.Monitoring.Kpi;

[Collection(A1MonitorRunLockCollection.Name)]
public class PillarHealthMonitorServiceTests
{
    private static readonly DateTimeOffset Day = new(2026, 11, 4, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = Day.AddHours(17).AddMinutes(10);

    private readonly A1InMemoryDefinitionStore _definitions = new();
    private readonly A1InMemoryBucketStore _buckets = new();
    private readonly A1InMemoryStateStore _states = new();
    private readonly A1InMemoryAnomalyStore _anomalies = new();
    private readonly A1InMemoryWatermarkStore _watermarks = new();
    private readonly A1SpyEvaluator _evaluator = new(new KpiEvaluator());
    private readonly A1RecordingTriageSubmitter _triage = new();
    private readonly HealthMonitoringOptions _options = new() { Enabled = true, Pillars = ["application"] };

    private PillarHealthMonitorService CreateService() => new(
        _definitions, _buckets, _states, _anomalies, _watermarks, _evaluator, _triage,
        Microsoft.Extensions.Options.Options.Create(_options), new A1FakeTimeProvider(Now), NullLogger<PillarHealthMonitorService>.Instance);

    private static DateTimeOffset At(int hour) => Day.AddHours(hour);

    private static KpiDefinition StaticLatency(int id, string stage = "", KpiPillar pillar = KpiPillar.Application) =>
        KpiEvaluatorTests.Definition(DetectionMethodKind.StaticThreshold, KpiDirection.Increase, "{\"threshold\":3000}") with
        {
            KpiDefinitionId = id,
            Pillar = pillar,
            Stage = stage,
        };

    private static KpiBucket P95(int id, DateTimeOffset start, double p95) => new(id, start, 100, 0, null, p95, null, null, null);

    private void AddBuckets(int id, double value, params int[] hours)
    {
        foreach (var hour in hours) _buckets.Buckets.Add(P95(id, At(hour), value));
    }

    private int StoreCalls => _definitions.Calls + _buckets.Calls + _states.Calls + _anomalies.Calls + _watermarks.Calls;

    /// <summary>KPI 1 has one previous anomaly; the 16:00 bucket (3500 > 3000) makes it trigger.</summary>
    private KpiDefinition ArrangeTrigger(string stage = "")
    {
        var definition = StaticLatency(1, stage);
        _definitions.Definitions.Add(definition);
        _states.States[1] = new KpiDetectionState(1, 1, At(15), null, Now.AddHours(-1));
        _watermarks.Watermarks[1] = Now;
        AddBuckets(1, 3500, 16);
        return definition;
    }

    [Fact]
    public async Task Monitor_DoesNothingWhenDisabled()
    {
        ArrangeTrigger();
        _options.Enabled = false;

        var disabled = await CreateService().RunAsync(KpiPillar.Application);

        Assert.Equal(new PillarMonitorRunResult(0, 0, 0, 0), disabled);
        Assert.Equal(0, StoreCalls);
        Assert.Empty(_evaluator.Calls);
        Assert.Empty(_triage.Signals);

        _options.Enabled = true;
        var otherPillar = await CreateService().RunAsync(KpiPillar.Business);

        Assert.Equal(new PillarMonitorRunResult(0, 0, 0, 0), otherPillar);
        Assert.Equal(0, StoreCalls);
        Assert.Empty(_evaluator.Calls);
    }

    [Fact]
    public async Task Monitor_SkipsKpisNotDue()
    {
        _definitions.Definitions.Add(StaticLatency(1));
        _states.States[1] = new KpiDetectionState(1, 0, At(15), null, Now.AddMinutes(-2));
        _watermarks.Watermarks[1] = Now;
        AddBuckets(1, 3500, 16);

        var result = await CreateService().RunAsync(KpiPillar.Application);

        Assert.Equal(new PillarMonitorRunResult(0, 0, 0, 1), result);
        Assert.Empty(_evaluator.Calls);
        Assert.Empty(_states.Saved);
        Assert.Empty(_anomalies.Anomalies);
    }

    [Fact]
    public async Task Monitor_EvaluatesOnlyClosedAndIngestedBuckets()
    {
        // KPI 1: watermark 16:00 -> the 16:00 bucket is closed but not fully ingested.
        _definitions.Definitions.Add(StaticLatency(1));
        _states.States[1] = new KpiDetectionState(1, 0, At(13), null, null);
        _watermarks.Watermarks[1] = At(16);
        AddBuckets(1, 1000, 13, 14, 15, 16, 17);

        // KPI 2: watermark ahead of now -> the 17:00 bucket is still open at 17:10.
        _definitions.Definitions.Add(StaticLatency(2));
        _states.States[2] = new KpiDetectionState(2, 0, At(14), null, null);
        _watermarks.Watermarks[2] = At(18);
        AddBuckets(2, 1000, 14, 15, 16, 17);

        // KPI 3: never ingested -> nothing to evaluate.
        _definitions.Definitions.Add(StaticLatency(3));
        AddBuckets(3, 1000, 15, 16);

        var result = await CreateService().RunAsync(KpiPillar.Application);

        var byKpi = _evaluator.Calls.ToDictionary(c => c.Definition.KpiDefinitionId, c => c.Buckets.Select(b => b.BucketStart).ToList());
        Assert.Equal([At(14), At(15)], byKpi[1]);
        Assert.Equal([At(15), At(16)], byKpi[2]);
        Assert.False(byKpi.ContainsKey(3));
        Assert.Equal(new PillarMonitorRunResult(2, 0, 0, 1), result);
        Assert.Equal(At(15), _states.States[1].LastBucketEvaluated);
        Assert.Equal(At(16), _states.States[2].LastBucketEvaluated);
    }

    [Fact]
    public async Task Monitor_FirstRunEvaluatesOnlyLatestClosedBucket()
    {
        _definitions.Definitions.Add(StaticLatency(1));
        _watermarks.Watermarks[1] = Now;
        AddBuckets(1, 1000, 12, 13, 14, 15, 16, 17);

        var result = await CreateService().RunAsync(KpiPillar.Application);

        var call = Assert.Single(_evaluator.Calls);
        var bucket = Assert.Single(call.Buckets);
        Assert.Equal(At(16), bucket.BucketStart);
        Assert.Equal(KpiDetectionState.Initial(1), call.State);
        Assert.Equal(new PillarMonitorRunResult(1, 0, 0, 0), result);
        Assert.Equal(At(16), _states.States[1].LastBucketEvaluated);
    }

    [Fact]
    public async Task Monitor_SubmitsTriageSignalOnTrigger()
    {
        ArrangeTrigger(stage: "");
        var investigationId = Guid.Parse("6f1c2a3b-0000-4000-8000-000000000001");
        _triage.NextInvestigationId = investigationId;

        var result = await CreateService().RunAsync(KpiPillar.Application);

        Assert.Equal(new PillarMonitorRunResult(1, 1, 0, 0), result);
        var anomaly = Assert.Single(_anomalies.Anomalies);
        var signal = Assert.Single(_triage.Signals);
        Assert.Equal(Now, signal.DetectedAt);
        Assert.Equal("Application Health", signal.Pillar);
        Assert.Equal("Latency", signal.Kpi);
        Assert.Equal("2 consecutive increases: Latency 3500 vs reference 3000 (threshold 3000)", signal.Detection);
        Assert.Equal("hotels", signal.Domain);
        Assert.Null(signal.Stage);
        Assert.Equal(anomaly.AnomalyId, signal.AnomalyId);
        Assert.Equal("daemon", signal.Source);
        Assert.Equal(investigationId, anomaly.InvestigationId);
    }

    [Fact]
    public async Task Monitor_SubmitsTriageSignalWithStageWhenPresent()
    {
        ArrangeTrigger(stage: "search");

        await CreateService().RunAsync(KpiPillar.Application);

        Assert.Equal("search", Assert.Single(_triage.Signals).Stage);
    }

    [Fact]
    public async Task Monitor_PersistsStateAndAnomaly()
    {
        ArrangeTrigger();

        await CreateService().RunAsync(KpiPillar.Application);

        var saved = Assert.Single(_states.Saved);
        Assert.Equal(new KpiDetectionState(1, 2, At(16), At(19), Now), saved);

        var anomaly = Assert.Single(_anomalies.Anomalies);
        Assert.Equal(1, anomaly.KpiDefinitionId);
        Assert.Equal(Now, anomaly.DetectedAt);
        Assert.Equal(At(16), anomaly.BucketStart);
        Assert.Equal(3500, anomaly.ObservedValue);
        Assert.Equal(3000, anomaly.ReferenceValue);
        Assert.Equal(500, anomaly.Deviation);
        Assert.Equal("2 consecutive increases: Latency 3500 vs reference 3000 (threshold 3000)", anomaly.Detection);
    }

    [Fact]
    public async Task Monitor_TriageFailure_KeepsAnomalyAndState()
    {
        ArrangeTrigger();
        _triage.Throw = true;

        var result = await CreateService().RunAsync(KpiPillar.Application);

        Assert.Equal(new PillarMonitorRunResult(1, 1, 0, 0), result);
        var anomaly = Assert.Single(_anomalies.Anomalies);
        Assert.Null(anomaly.InvestigationId);
        Assert.Single(_triage.Signals);
        Assert.Equal(new KpiDetectionState(1, 2, At(16), At(19), Now), Assert.Single(_states.Saved));
    }

    [Fact]
    public async Task Monitor_FailingKpi_DoesNotStopOthers()
    {
        _definitions.Definitions.Add(StaticLatency(7));
        _watermarks.Watermarks[7] = Now;
        _buckets.ThrowFor.Add(7);
        ArrangeTrigger();

        var result = await CreateService().RunAsync(KpiPillar.Application);

        Assert.Equal(new PillarMonitorRunResult(1, 1, 0, 1), result);
        Assert.Single(_anomalies.Anomalies);
        Assert.DoesNotContain(_states.Saved, s => s.KpiDefinitionId == 7);
    }

    [Fact]
    public async Task Monitor_CancellationAfterAnomalySaved_StillPersistsState()
    {
        ArrangeTrigger();
        using var cts = new CancellationTokenSource();
        _triage.OnSubmit = () =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateService().RunAsync(KpiPillar.Application, cts.Token));

        var anomaly = Assert.Single(_anomalies.Anomalies);
        Assert.Null(anomaly.InvestigationId);
        Assert.Equal(new KpiDetectionState(1, 2, At(16), At(19), Now), Assert.Single(_states.Saved));

        // The pillar lock was released despite the cancellation, and the saved state prevents a duplicate trigger.
        _triage.OnSubmit = null;
        var next = await CreateService().RunAsync(KpiPillar.Application);
        Assert.Equal(new PillarMonitorRunResult(0, 0, 0, 1), next);
        Assert.Single(_anomalies.Anomalies);
    }

    [Fact]
    public async Task Monitor_LockReleasedAfterExceptionAndCancellation()
    {
        ArrangeTrigger();
        _definitions.BeforeGetEnabled = _ => throw new InvalidOperationException("definition store unavailable");
        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateService().RunAsync(KpiPillar.Application));

        _definitions.BeforeGetEnabled = null;
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateService().RunAsync(KpiPillar.Application, cancelled.Token));

        var result = await CreateService().RunAsync(KpiPillar.Application);

        Assert.Equal(new PillarMonitorRunResult(1, 1, 0, 0), result);
        Assert.Single(_anomalies.Anomalies);
    }

    [Fact]
    public async Task Monitor_OlderRunDoesNotOverwriteNewerState()
    {
        // KPI 1 evaluates the 16:00 bucket (no trigger); KPI 2 has nothing ingested.
        _definitions.Definitions.Add(StaticLatency(1));
        _states.States[1] = new KpiDetectionState(1, 0, At(15), null, Now.AddHours(-1));
        _watermarks.Watermarks[1] = Now;
        AddBuckets(1, 1000, 16);
        _definitions.Definitions.Add(StaticLatency(2));
        _states.States[2] = new KpiDetectionState(2, 0, At(15), null, Now.AddHours(-1));

        var newer1 = new KpiDetectionState(1, 2, At(17), At(19), Now.AddSeconds(-5));
        var newer2 = new KpiDetectionState(2, 0, At(16), null, Now.AddSeconds(-5));
        var readsById = new Dictionary<int, int>();
        _states.AfterGet = id =>
        {
            // A newer run saves its state right after this run's first read of each KPI.
            readsById[id] = readsById.GetValueOrDefault(id) + 1;
            if (readsById[id] == 1) _states.States[id] = id == 1 ? newer1 : newer2;
        };

        var result = await CreateService().RunAsync(KpiPillar.Application);

        Assert.Equal(new PillarMonitorRunResult(1, 0, 0, 1), result);
        Assert.Empty(_states.Saved);
        Assert.Equal(newer1, _states.States[1]);
        Assert.Equal(newer2, _states.States[2]);
    }

    [Fact]
    public async Task Monitor_PartialDiscard_KeepsFirstTriggerAndCountsSecondAsSuppressed()
    {
        var definition = StaticLatency(1) with { ConsecutiveCount = 1, CooldownMinutes = 0 };
        _definitions.Definitions.Add(definition);
        _states.States[1] = new KpiDetectionState(1, 0, At(14), null, Now.AddHours(-1));
        _watermarks.Watermarks[1] = Now;
        AddBuckets(1, 3500, 15, 16);

        var concurrent = new KpiDetectionState(1, 1, At(16), At(17), Now.AddSeconds(-5));
        var reads = 0;
        _states.AfterGet = id =>
        {
            // Reads: 1 = initial, 2 = before the 15:00 trigger, 3 = before the 16:00 trigger (sees the concurrent run).
            if (id == 1 && ++reads == 2) _states.States[1] = concurrent;
        };

        var result = await CreateService().RunAsync(KpiPillar.Application);

        Assert.Equal(new PillarMonitorRunResult(1, 1, 1, 0), result);
        Assert.Equal(At(15), Assert.Single(_anomalies.Anomalies).BucketStart);
        Assert.Single(_triage.Signals);
        // Our anomaly is recorded, so our state (same last bucket, not older) is persisted.
        Assert.Equal(new KpiDetectionState(1, 2, At(16), At(17), Now), Assert.Single(_states.Saved));
    }

    [Fact]
    public async Task Monitor_StateReReadFailsAfterAnomaly_StillPersistsState()
    {
        ArrangeTrigger();
        var reads = 0;
        _states.AfterGet = id =>
        {
            // Reads: 1 = initial, 2 = before the trigger, 3 = before the final save (after the anomaly insert).
            if (id == 1 && ++reads == 3) throw new InvalidOperationException("transient state read failure");
        };

        var result = await CreateService().RunAsync(KpiPillar.Application);

        Assert.Equal(new PillarMonitorRunResult(1, 1, 0, 0), result);
        Assert.Single(_anomalies.Anomalies);
        Assert.Equal(new KpiDetectionState(1, 2, At(16), At(19), Now), Assert.Single(_states.Saved));
    }

    [Fact]
    public async Task Monitor_CancellationDuringTriage_CompletesAttachAndState()
    {
        ArrangeTrigger();
        var investigationId = Guid.Parse("6f1c2a3b-0000-4000-8000-000000000002");
        _triage.NextInvestigationId = investigationId;
        using var cts = new CancellationTokenSource();
        _triage.OnSubmit = cts.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateService().RunAsync(KpiPillar.Application, cts.Token));

        Assert.Equal(investigationId, Assert.Single(_anomalies.Anomalies).InvestigationId);
        Assert.Equal(new KpiDetectionState(1, 2, At(16), At(19), Now), Assert.Single(_states.Saved));
    }

    [Fact]
    public async Task Monitor_RequestsHistoryRangeBeforeFirstBucket()
    {
        var definition = KpiEvaluatorTests.Definition(DetectionMethodKind.BaselinePercent, KpiDirection.Increase,
            "{\"lookbackDays\":7,\"thresholdPercent\":50}");
        _definitions.Definitions.Add(definition);
        _states.States[1] = new KpiDetectionState(1, 0, At(14), null, null);
        _watermarks.Watermarks[1] = Now;
        AddBuckets(1, 1000, 15, 16);
        foreach (var hour in new[] { 15, 16 })
            for (var k = 1; k <= 7; k++) _buckets.Buckets.Add(P95(1, At(hour).AddDays(-k), 1000));

        var result = await CreateService().RunAsync(KpiPillar.Application);

        Assert.Contains((1, At(15) - TimeSpan.FromDays(7), At(15)), _buckets.RangeRequests);
        var call = Assert.Single(_evaluator.Calls);
        Assert.Equal(14, call.History.Count);
        Assert.All(call.History, b => Assert.True(b.BucketStart >= At(15).AddDays(-7) && b.BucketStart < At(15)));
        Assert.Equal(new PillarMonitorRunResult(1, 0, 0, 0), result);
    }

    [Fact]
    public async Task Monitor_OverlappingRunsOfSamePillar_ProduceOneAnomalyAndOneTriage()
    {
        ArrangeTrigger();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _definitions.BeforeGetEnabled = async _ =>
        {
            entered.TrySetResult();
            await release.Task;
        };

        var first = Task.Run(() => CreateService().RunAsync(KpiPillar.Application));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var callsWhileBlocked = StoreCalls;

        var second = await CreateService().RunAsync(KpiPillar.Application).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(new PillarMonitorRunResult(0, 0, 0, 0), second);
        Assert.Equal(callsWhileBlocked, StoreCalls);

        release.SetResult();
        var firstResult = await first.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(new PillarMonitorRunResult(1, 1, 0, 0), firstResult);
        Assert.Single(_anomalies.Anomalies);
        Assert.Single(_triage.Signals);
        Assert.Single(_evaluator.Calls);

        // The lock is released after the run: a later run proceeds normally (and is not due yet).
        _definitions.BeforeGetEnabled = null;
        var third = await CreateService().RunAsync(KpiPillar.Application);
        Assert.Equal(new PillarMonitorRunResult(0, 0, 0, 1), third);
    }

    [Fact]
    public async Task Monitor_DifferentPillars_RunConcurrently()
    {
        _options.Pillars = ["Application", "Business"];
        ArrangeTrigger();
        _definitions.Definitions.Add(StaticLatency(2, pillar: KpiPillar.Business) with { Kpi = "Bookings" });
        _states.States[2] = new KpiDetectionState(2, 1, At(15), null, null);
        _watermarks.Watermarks[2] = Now;
        AddBuckets(2, 3500, 16);

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _definitions.BeforeGetEnabled = async pillar =>
        {
            if (pillar != KpiPillar.Application) return;
            entered.TrySetResult();
            await release.Task;
        };

        var application = Task.Run(() => CreateService().RunAsync(KpiPillar.Application));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var business = await CreateService().RunAsync(KpiPillar.Business).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(new PillarMonitorRunResult(1, 1, 0, 0), business);
        Assert.Equal("Business Health", Assert.Single(_triage.Signals).Pillar);
        Assert.False(application.IsCompleted);

        release.SetResult();
        Assert.Equal(new PillarMonitorRunResult(1, 1, 0, 0), await application.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(2, _anomalies.Anomalies.Count);
    }

    [Fact]
    public async Task Monitor_StateAdvancedByConcurrentRun_DiscardsDuplicateTrigger()
    {
        ArrangeTrigger();
        var reads = 0;
        _states.AfterGet = id =>
        {
            // After the monitor's first read, a concurrent run saves state that already covers the 16:00 bucket.
            if (id == 1 && ++reads == 1)
                _states.States[1] = new KpiDetectionState(1, 2, At(16), At(19), Now.AddSeconds(-5));
        };

        var result = await CreateService().RunAsync(KpiPillar.Application);

        // The discarded trigger counts as suppressed; the KPI itself was evaluated.
        Assert.Equal(new PillarMonitorRunResult(1, 0, 1, 0), result);
        Assert.Empty(_anomalies.Anomalies);
        Assert.Empty(_triage.Signals);
        Assert.Empty(_states.Saved);
        Assert.Equal(new KpiDetectionState(1, 2, At(16), At(19), Now.AddSeconds(-5)), _states.States[1]);
    }

    [Fact]
    public async Task Monitor_RespectsMaxKpisPerCycle()
    {
        _options.MaxKpisPerCycle = 1;
        _definitions.Definitions.Add(StaticLatency(1));
        _definitions.Definitions.Add(StaticLatency(2));
        _watermarks.Watermarks[1] = Now;
        _watermarks.Watermarks[2] = Now;
        AddBuckets(1, 1000, 16);
        AddBuckets(2, 1000, 16);

        var result = await CreateService().RunAsync(KpiPillar.Application);

        Assert.Equal(1, result.KpisEvaluated);
        Assert.Equal(1, Assert.Single(_evaluator.Calls).Definition.KpiDefinitionId);
    }
}
