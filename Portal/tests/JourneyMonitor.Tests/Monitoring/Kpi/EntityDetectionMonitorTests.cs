using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring.Kpi;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Core.UseCases;
using JourneyMonitor.Tests.Monitoring.Kpi.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace JourneyMonitor.Tests.Monitoring.Kpi;

[Collection(A1MonitorRunLockCollection.Name)]
public class EntityDetectionMonitorTests
{
    private const int EntityId = 7;
    private static readonly DateTimeOffset Day = new(2026, 11, 4, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = Day.AddHours(17).AddMinutes(10);

    private readonly A1InMemoryDefinitionStore _definitions = new();
    private readonly EntityBucketStore _buckets = new();
    private readonly EntityStateStore _states = new();
    private readonly EntityAnomalyStore _anomalies = new();
    private readonly A1InMemoryWatermarkStore _watermarks = new();
    private readonly A1SpyEvaluator _evaluator = new(new KpiEvaluator());
    private readonly A1RecordingTriageSubmitter _triage = new();
    private readonly EntityActivityRecorder _activity = new();
    private readonly HealthMonitoringOptions _options = new() { Enabled = true, Pillars = ["Application"] };
    private readonly A1FakeTimeProvider _time = new(Now);

    private EntityDetectionMonitor CreateMonitor() => new(
        _definitions, _buckets, _states, _anomalies, _watermarks, _evaluator,
        Microsoft.Extensions.Options.Options.Create(_options), _time, NullLogger<EntityDetectionMonitor>.Instance, _activity, _triage);

    private PillarHealthMonitorService CreatePillarMonitor() => new(
        _definitions, _buckets, _states, _anomalies, _watermarks, _evaluator, _triage,
        Microsoft.Extensions.Options.Options.Create(_options), _time, NullLogger<PillarHealthMonitorService>.Instance);

    private static DateTimeOffset At(int hour) => Day.AddHours(hour);

    private static KpiDefinition Definition(int id, KpiGrain grain, string kpi = "Latency", string domain = "hotels") =>
        KpiEvaluatorTests.Definition(DetectionMethodKind.StaticThreshold, KpiDirection.Increase, "{\"threshold\":3000}", kpi: kpi) with
        {
            KpiDefinitionId = id,
            Domain = domain,
            Grain = grain,
        };

    private static KpiBucket Bucket(int id, string entityKey, DateTimeOffset start, double p95) =>
        new(id, start, 100, 0, null, p95, null, null, null) { EntityKey = entityKey };

    private void AddBuckets(int id, string entityKey, double p95, params int[] hours)
    {
        foreach (var hour in hours) _buckets.Buckets.Add(Bucket(id, entityKey, At(hour), p95));
    }

    private void SetState(int id, string entityKey, int counter, int lastHour, DateTimeOffset? cooldownUntil = null) =>
        _states.States[(id, entityKey)] = new KpiDetectionState(id, counter, At(lastHour), cooldownUntil, Now.AddHours(-1)) { EntityKey = entityKey };

    private void AdvanceOneHour()
    {
        _time.Now = _time.Now.AddHours(1);
        foreach (var key in _watermarks.Watermarks.Keys.ToList()) _watermarks.Watermarks[key] = _time.Now;
    }

    [Fact]
    public async Task EntityMonitor_UsesSameEvaluator()
    {
        var definition = Definition(EntityId, KpiGrain.Entity);
        _definitions.Definitions.Add(definition);
        _watermarks.Watermarks[EntityId] = Now;
        AddBuckets(EntityId, "club-a", 1000, 12, 13, 14, 15, 16);
        AddBuckets(EntityId, "club-b", 3500, 14, 15, 16);
        _buckets.Buckets.Add(Bucket(EntityId, "", At(16), 9999));

        var result = await CreateMonitor().RunAsync();

        Assert.Equal(new EntityDetectionRunResult(1, 2, 1, 0), result);
        Assert.Equal(2, _evaluator.Calls.Count);
        foreach (var call in _evaluator.Calls)
        {
            Assert.Same(definition, call.Definition);
            Assert.NotEqual("", call.State.EntityKey);
            Assert.All(call.Buckets, b => Assert.Equal(call.State.EntityKey, b.EntityKey));
            Assert.All(call.History, b => Assert.Equal(call.State.EntityKey, b.EntityKey));
            // A series without state starts at its latest ready bucket, exactly like the domain grain.
            Assert.Equal([At(16)], call.Buckets.Select(b => b.BucketStart));
        }
        Assert.Equal(["club-a", "club-b"], _evaluator.Calls.Select(c => c.State.EntityKey).Order());

        // Batched reads: one entity range read and no per-entity bucket/state reads.
        Assert.Equal(1, _buckets.EntityRangeCalls);
        Assert.Equal(0, _buckets.SeriesRangeCalls);
        Assert.Equal(0, _states.SingleGetCalls);
        Assert.Equal(1, _watermarks.Calls);

        var b = _states.States[(EntityId, "club-b")];
        Assert.Equal(1, b.ConsecutiveCount);
        Assert.Equal(At(16), b.LastBucketEvaluated);
        Assert.Equal(Now, b.LastEvaluatedAt);
        Assert.False(_states.States.ContainsKey((EntityId, "")));
    }

    [Fact]
    public async Task EntityMonitor_ConsecutiveAndCooldownPerEntity()
    {
        _definitions.Definitions.Add(Definition(EntityId, KpiGrain.Entity));
        _watermarks.Watermarks[EntityId] = Now;
        SetState(EntityId, "club-a", 1, 15);
        SetState(EntityId, "club-b", 0, 15);
        AddBuckets(EntityId, "club-a", 3500, 16, 17);
        AddBuckets(EntityId, "club-b", 3500, 16, 17);

        var first = await CreateMonitor().RunAsync();

        Assert.Equal(1, first.Triggered);
        var a = _states.States[(EntityId, "club-a")];
        var b = _states.States[(EntityId, "club-b")];
        Assert.Equal(2, a.ConsecutiveCount);
        Assert.NotNull(a.CooldownUntil);
        Assert.Equal(1, b.ConsecutiveCount);
        Assert.Null(b.CooldownUntil);

        AdvanceOneHour();
        var second = await CreateMonitor().RunAsync();

        Assert.Equal(1, second.Triggered);
        Assert.Equal(
            [("club-a", At(16)), ("club-b", At(17))],
            _anomalies.Anomalies.Select(x => (x.EntityKey, x.BucketStart)).OrderBy(x => x.EntityKey).ToList());
        var aAfter = _states.States[(EntityId, "club-a")];
        Assert.Equal(At(17), aAfter.LastBucketEvaluated);
        Assert.Equal(a.CooldownUntil, aAfter.CooldownUntil);
        Assert.Contains(_evaluator.Calls, c => c.State.EntityKey == "club-a" && c.State.CooldownUntil == a.CooldownUntil);
        Assert.NotNull(_states.States[(EntityId, "club-b")].CooldownUntil);

        // Nothing new: the state of each series never moves backwards and nothing is re-evaluated.
        _time.Now = _time.Now.AddMinutes(10);
        var third = await CreateMonitor().RunAsync();
        Assert.Equal(new EntityDetectionRunResult(1, 0, 0, 0), third);
        Assert.Equal(2, _anomalies.Anomalies.Count);
    }

    [Fact]
    public async Task EntityMonitor_SecondTickWithoutNewBucket_DoesNotReadBuckets()
    {
        _definitions.Definitions.Add(Definition(EntityId, KpiGrain.Entity));
        _watermarks.Watermarks[EntityId] = Now;
        AddBuckets(EntityId, "club-a", 1000, 15, 16);
        AddBuckets(EntityId, "club-b", 3500, 15, 16);

        await CreateMonitor().RunAsync();
        Assert.Equal(1, _buckets.EntityRangeCalls);
        Assert.Equal(2, _evaluator.Calls.Count);

        // Not due yet: nothing is read beyond the batched states.
        _time.Now = Now.AddMinutes(2);
        await CreateMonitor().RunAsync();
        // Due again (frequency 5 min) but the 17:00 bucket is not closed: still no bucket read.
        _time.Now = Now.AddMinutes(10);
        _watermarks.Watermarks[EntityId] = _time.Now;
        var idle = await CreateMonitor().RunAsync();

        Assert.Equal(new EntityDetectionRunResult(1, 0, 0, 0), idle);
        Assert.Equal(1, _buckets.EntityRangeCalls);
        Assert.Equal(2, _evaluator.Calls.Count);
        Assert.Equal(
            ["Latency/hotels [entity]: 2 series evaluated, 1 anomalous, 0 triggered",
             "Latency/hotels [entity]: not due (next evaluation 17:15)",
             "Latency/hotels [entity]: no new closed and ingested bucket"],
            _activity.Entries.Select(e => e.Message));

        // The next closed bucket is read again, only from where the series need it.
        AdvanceOneHour();
        AddBuckets(EntityId, "club-a", 1000, 17);
        AddBuckets(EntityId, "club-b", 3500, 17);
        var next = await CreateMonitor().RunAsync();
        Assert.Equal(2, next.SeriesEvaluated);
        Assert.Equal(2, _buckets.EntityRangeCalls);
    }

    [Fact]
    public async Task EntityMonitor_StateNeverRegresses()
    {
        _definitions.Definitions.Add(Definition(EntityId, KpiGrain.Entity));
        _watermarks.Watermarks[EntityId] = Now;
        SetState(EntityId, "club-a", 0, 15);
        AddBuckets(EntityId, "club-a", 1000, 16);
        // A concurrent run advances the series past this run's bucket between the evaluation and the save.
        _states.BeforeEntityStatesRead = calls =>
        {
            if (calls == 2) _states.States[(EntityId, "club-a")] = new KpiDetectionState(EntityId, 0, At(18), null, Now) { EntityKey = "club-a" };
        };

        await CreateMonitor().RunAsync();

        Assert.Equal(At(18), _states.States[(EntityId, "club-a")].LastBucketEvaluated);
        Assert.Empty(_states.Saved);
    }

    [Fact]
    public async Task EntityMonitor_AnomalyCarriesEntityKey()
    {
        var definition = Definition(EntityId, KpiGrain.Entity);
        _definitions.Definitions.Add(definition);
        _watermarks.Watermarks[EntityId] = Now;
        SetState(EntityId, "club-a", 1, 15);
        AddBuckets(EntityId, "club-a", 3500, 16);
        AddBuckets(EntityId, "club-b", 1000, 16);

        await CreateMonitor().RunAsync();

        var anomaly = Assert.Single(_anomalies.Anomalies);
        Assert.Equal("club-a", anomaly.EntityKey);
        Assert.Equal(EntityId, anomaly.KpiDefinitionId);
        Assert.Equal(At(16), anomaly.BucketStart);
        Assert.Equal(Now, anomaly.DetectedAt);
        Assert.Equal(3500, anomaly.ObservedValue);
        Assert.Equal(KpiDetectionText.Format(definition, definition.ConsecutiveCount, 3500, anomaly.ReferenceValue, anomaly.Deviation), anomaly.Detection);
        Assert.Null(anomaly.InvestigationId);
    }

    [Fact]
    public async Task EntityMonitor_DoesNotChangePillarStatus()
    {
        var domain = Definition(1, KpiGrain.Domain);
        var entity = Definition(EntityId, KpiGrain.Entity);
        _definitions.Definitions.AddRange([domain, entity]);
        _watermarks.Watermarks[1] = Now;
        _watermarks.Watermarks[EntityId] = Now;
        _buckets.Buckets.Add(Bucket(1, "", At(16), 1000));
        SetState(EntityId, "club-a", 1, 15);
        AddBuckets(EntityId, "club-a", 3500, 16);

        var pillar = await CreatePillarMonitor().RunAsync(KpiPillar.Application);
        var entityRun = await CreateMonitor().RunAsync();

        Assert.Equal(1, entityRun.Triggered);
        // The pillar monitor evaluates only the domain grain.
        Assert.Equal(1, pillar.KpisEvaluated);
        Assert.DoesNotContain(_evaluator.Calls, c => c.Definition.KpiDefinitionId == EntityId && c.State.EntityKey == "");
        Assert.DoesNotContain(_states.Saved, s => s.KpiDefinitionId == EntityId && s.EntityKey == "");
        Assert.All(_states.Saved.Where(s => s.KpiDefinitionId == EntityId), s => Assert.NotEqual("", s.EntityKey));

        // Pillar status is read from the domain state only: an entity in cooldown leaves it "ok".
        var reader = new GetPillarHealthService(_definitions, _buckets, _states, _anomalies, _time);
        var application = (await reader.GetAsync(null)).Single(p => p.Pillar == KpiPillar.Application);
        Assert.Equal(GetPillarHealthService.StatusOk, application.Status);
        Assert.All(application.Kpis, k => Assert.Equal(0, k.State.ConsecutiveCount));
    }

    [Fact]
    public async Task EntityMonitor_DoesNotSubmitTriage()
    {
        _definitions.Definitions.Add(Definition(EntityId, KpiGrain.Entity));
        _watermarks.Watermarks[EntityId] = Now;
        SetState(EntityId, "club-a", 1, 15);
        SetState(EntityId, "club-b", 1, 15);
        AddBuckets(EntityId, "club-a", 3500, 16);
        AddBuckets(EntityId, "club-b", 3500, 16);

        await CreatePillarMonitor().RunAsync(KpiPillar.Application);
        var result = await CreateMonitor().RunAsync();

        Assert.Equal(2, result.Triggered);
        Assert.Equal(2, _anomalies.Anomalies.Count);
        // The monitor HAS a Triage submitter (it is the partner path's, contract v4 §5) and still never uses it
        // for an entity KPI that is not a partner definition.
        Assert.Empty(_triage.Signals);
        Assert.All(_anomalies.Anomalies, a => Assert.Null(a.InvestigationId));
    }

    [Fact]
    public async Task EntityMonitor_ActivityIsOneLinePerDefinition()
    {
        _definitions.Definitions.Add(Definition(EntityId, KpiGrain.Entity));
        _definitions.Definitions.Add(Definition(8, KpiGrain.Entity, kpi: "Exceptions", domain: "cars"));
        _definitions.Definitions.Add(Definition(9, KpiGrain.Domain));
        _watermarks.Watermarks[EntityId] = Now;
        _watermarks.Watermarks[8] = Now;
        SetState(EntityId, "club-a", 1, 15);
        AddBuckets(EntityId, "club-a", 3500, 16);
        AddBuckets(EntityId, "club-b", 3500, 16);
        AddBuckets(EntityId, "club-c", 1000, 16);
        AddBuckets(8, "host-1", 1000, 16);

        await CreateMonitor().RunAsync();

        Assert.Equal(2, _activity.Entries.Count);
        Assert.All(_activity.Entries, e =>
        {
            Assert.Equal(DaemonKeys.ApplicationHealthMonitor, e.DaemonKey);
            Assert.Equal(DaemonActivityLevel.Info, e.Level);
            Assert.Equal(DaemonActivityKinds.KpiEvaluated, e.Kind);
        });
        Assert.Equal(
            ["Latency/hotels [entity]: 3 series evaluated, 2 anomalous, 1 triggered",
             "Exceptions/cars [entity]: 1 series evaluated, 0 anomalous, 0 triggered"],
            _activity.Entries.Select(e => e.Message));
    }

    [Fact]
    public async Task EntityMonitor_DoesNothingWhenDisabledOrApplicationPillarOff()
    {
        _definitions.Definitions.Add(Definition(EntityId, KpiGrain.Entity));
        _watermarks.Watermarks[EntityId] = Now;
        AddBuckets(EntityId, "club-a", 3500, 16);

        _options.Enabled = false;
        Assert.Equal(new EntityDetectionRunResult(0, 0, 0, 0), await CreateMonitor().RunAsync());
        _options.Enabled = true;
        _options.Pillars = ["Business"];
        Assert.Equal(new EntityDetectionRunResult(0, 0, 0, 0), await CreateMonitor().RunAsync());

        Assert.Empty(_evaluator.Calls);
        Assert.Empty(_activity.Entries);
    }

    [Fact]
    public async Task EntityMonitor_FailingDefinitionDoesNotStopTheOthers()
    {
        _definitions.Definitions.Add(Definition(EntityId, KpiGrain.Entity));
        _definitions.Definitions.Add(Definition(8, KpiGrain.Entity, kpi: "Exceptions"));
        _watermarks.Watermarks[EntityId] = Now;
        _watermarks.Watermarks[8] = Now;
        AddBuckets(8, "host-1", 1000, 16);
        _buckets.ThrowFor.Add(EntityId);

        var result = await CreateMonitor().RunAsync();

        Assert.Equal(new EntityDetectionRunResult(1, 1, 0, 0), result);
        Assert.Contains(_activity.Entries, e => e.Level == DaemonActivityLevel.Error && e.Message.StartsWith("Latency/hotels [entity]:", StringComparison.Ordinal));
        Assert.Contains(_activity.Entries, e => e.Message == "Exceptions/hotels [entity]: 1 series evaluated, 0 anomalous, 0 triggered");
    }
}

public sealed class EntityBucketStore : IKpiBucketStore
{
    public List<KpiBucket> Buckets { get; } = [];
    public HashSet<int> ThrowFor { get; } = [];
    public int EntityRangeCalls { get; private set; }
    public int SeriesRangeCalls { get; private set; }

    public Task UpsertAsync(IReadOnlyList<KpiBucket> buckets, CancellationToken ct = default)
    {
        foreach (var bucket in buckets)
        {
            Buckets.RemoveAll(b => b.KpiDefinitionId == bucket.KpiDefinitionId && b.EntityKey == bucket.EntityKey && b.BucketStart == bucket.BucketStart);
            Buckets.Add(bucket);
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<KpiBucket>> GetRangeAsync(int kpiDefinitionId, DateTimeOffset fromInclusive, DateTimeOffset toExclusive, CancellationToken ct = default) =>
        Task.FromResult(Range(kpiDefinitionId, fromInclusive, toExclusive, b => b.EntityKey == ""));

    public Task<IReadOnlyList<KpiBucket>> GetRangeAsync(int kpiDefinitionId, DateTimeOffset fromInclusive, DateTimeOffset toExclusive, string? entityKey, CancellationToken ct = default)
    {
        SeriesRangeCalls++;
        var key = entityKey ?? "";
        return Task.FromResult(Range(kpiDefinitionId, fromInclusive, toExclusive, b => b.EntityKey == key));
    }

    public Task<IReadOnlyList<KpiBucket>> GetEntityRangeAsync(int kpiDefinitionId, DateTimeOffset fromInclusive, DateTimeOffset toExclusive, CancellationToken ct = default)
    {
        EntityRangeCalls++;
        if (ThrowFor.Contains(kpiDefinitionId)) throw new InvalidOperationException("bucket store unavailable");
        return Task.FromResult(Range(kpiDefinitionId, fromInclusive, toExclusive, b => b.EntityKey != ""));
    }

    private IReadOnlyList<KpiBucket> Range(int id, DateTimeOffset from, DateTimeOffset to, Func<KpiBucket, bool> filter) =>
        Buckets.Where(b => b.KpiDefinitionId == id && b.BucketStart >= from && b.BucketStart < to && filter(b))
            .OrderBy(b => b.EntityKey, StringComparer.Ordinal).ThenBy(b => b.BucketStart).ToList();
}

public sealed class EntityStateStore : IKpiStateStore
{
    public Dictionary<(int Id, string EntityKey), KpiDetectionState> States { get; } = [];
    public List<KpiDetectionState> Saved { get; } = [];
    public int SingleGetCalls { get; private set; }
    public int EntityStatesCalls { get; private set; }
    public Action<int>? BeforeEntityStatesRead { get; set; }

    public Task<KpiDetectionState> GetAsync(int kpiDefinitionId, CancellationToken ct = default) =>
        Task.FromResult(Get(kpiDefinitionId, ""));

    public Task<KpiDetectionState> GetAsync(int kpiDefinitionId, string? entityKey, CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(entityKey)) SingleGetCalls++;
        return Task.FromResult(Get(kpiDefinitionId, entityKey ?? ""));
    }

    public Task<IReadOnlyList<KpiDetectionState>> GetEntityStatesAsync(int kpiDefinitionId, CancellationToken ct = default)
    {
        EntityStatesCalls++;
        BeforeEntityStatesRead?.Invoke(EntityStatesCalls);
        return Task.FromResult<IReadOnlyList<KpiDetectionState>>(States
            .Where(s => s.Key.Id == kpiDefinitionId && s.Key.EntityKey != "")
            .Select(s => s.Value).ToList());
    }

    public Task SaveAsync(KpiDetectionState state, CancellationToken ct = default)
    {
        States[(state.KpiDefinitionId, state.EntityKey)] = state;
        Saved.Add(state);
        return Task.CompletedTask;
    }

    private KpiDetectionState Get(int id, string key) =>
        States.TryGetValue((id, key), out var state) ? state : KpiDetectionState.Initial(id, key);
}

public sealed class EntityAnomalyStore : IKpiAnomalyStore
{
    private long _nextId = 500;
    public List<KpiAnomaly> Anomalies { get; } = [];

    public Task<KpiAnomaly> AddAsync(KpiAnomaly anomaly, CancellationToken ct = default)
    {
        var stored = anomaly with { AnomalyId = ++_nextId };
        Anomalies.Add(stored);
        return Task.FromResult(stored);
    }

    public Task AttachInvestigationAsync(long anomalyId, Guid investigationId, CancellationToken ct = default)
    {
        var index = Anomalies.FindIndex(a => a.AnomalyId == anomalyId);
        if (index >= 0) Anomalies[index] = Anomalies[index] with { InvestigationId = investigationId };
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<KpiAnomaly>> GetRecentAsync(KpiPillar? pillar, string? domain, int top, string? profileKey = null, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<KpiAnomaly>>(Anomalies.OrderByDescending(a => a.DetectedAt).Take(top).ToList());
}

public sealed class EntityActivityRecorder : IDaemonActivityRecorder
{
    public List<(string DaemonKey, DaemonActivityLevel Level, string Kind, string Message)> Entries { get; } = [];

    public IDaemonRun BeginRun(string daemonKey) => new Run(this, daemonKey);

    private sealed class Run(EntityActivityRecorder owner, string daemonKey) : IDaemonRun
    {
        public Guid RunId { get; } = Guid.NewGuid();
        public string DaemonKey { get; } = daemonKey;
        public void Info(string kind, string message, object? data = null) => owner.Entries.Add((DaemonKey, DaemonActivityLevel.Info, kind, message));
        public void Warning(string kind, string message, object? data = null) => owner.Entries.Add((DaemonKey, DaemonActivityLevel.Warning, kind, message));
        public void Error(string kind, string message, object? data = null) => owner.Entries.Add((DaemonKey, DaemonActivityLevel.Error, kind, message));
        public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
