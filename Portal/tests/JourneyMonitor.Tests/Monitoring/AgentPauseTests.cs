using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Core.UseCases;
using JourneyMonitor.Infrastructure.Monitoring;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Tests.Monitoring;

/// <summary>Pause resolver double with no store behind it - the runners only ever ask one question.</summary>
internal sealed class FakeAgentPauseResolver : IAgentPauseResolver
{
    private readonly Dictionary<string, bool> _paused = new(StringComparer.OrdinalIgnoreCase);

    public static FakeAgentPauseResolver NothingPaused() => new();

    public FakeAgentPauseResolver Pause(string monkeyKey)
    {
        _paused[monkeyKey] = true;
        return this;
    }

    public void Resume(string monkeyKey) => _paused[monkeyKey] = false;

    public Task<bool> IsPausedAsync(string monkeyKey, CancellationToken cancellationToken) =>
        Task.FromResult(_paused.TryGetValue(monkeyKey, out var p) && p);

    public Task<IReadOnlyDictionary<string, bool>> GetAllAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<string, bool>>(_paused);

    public void Invalidate() { }
}

/// <summary>Counts how many times the detector was asked to do work, which is what "paused" has to drive to zero.</summary>
internal sealed class CountingLatencySpikeDetector : ILatencySpikeDetector
{
    public int RunCalls { get; private set; }

    public Task<IReadOnlyList<Finding>> RunAsync(string domain, string environment, CancellationToken ct = default)
    {
        RunCalls++;
        return Task.FromResult<IReadOnlyList<Finding>>([]);
    }
}

internal sealed class CountingDoctorAgent : IDoctorAgent
{
    public int DiagnoseCalls { get; private set; }

    public Task<DoctorReport> DiagnoseAsync(Finding finding, CancellationToken ct = default)
    {
        DiagnoseCalls++;
        return Task.FromResult(new DoctorReport
        {
            FindingId = finding.Id,
            Verdict = "n/a",
            ErrorCategory = "unknown",
            Confidence = 0,
            RootCauseHypothesis = "n/a",
            CitationsJson = "[]",
            ProposedFixJson = "{}",
            ModelUsed = "fake",
        });
    }
}

public class AgentPauseResolverTests
{
    [Fact]
    public async Task ReportsNotPausedWhenTheStoreIsNotConfigured()
    {
        var resolver = Build(new ThrowingAgentPauseStore(), configured: false);

        Assert.False(await resolver.IsPausedAsync(MonkeyKey.LatencyScoutMonkey, CancellationToken.None));
        Assert.Empty(await resolver.GetAllAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ReportsNotPausedAndDoesNotThrowWhenTheStoreFails()
    {
        var resolver = Build(new ThrowingAgentPauseStore(), configured: true);

        Assert.False(await resolver.IsPausedAsync(MonkeyKey.DoctorMonkey, CancellationToken.None));
    }

    [Fact]
    public async Task ReadsBackWhatWasUpserted()
    {
        var store = new InMemoryAgentPauseStore();
        var resolver = Build(store, configured: true);

        await store.UpsertAsync(MonkeyKey.LatencyScoutMonkey, isPaused: true, "tester", CancellationToken.None);
        resolver.Invalidate();

        Assert.True(await resolver.IsPausedAsync(MonkeyKey.LatencyScoutMonkey, CancellationToken.None));
        Assert.False(await resolver.IsPausedAsync(MonkeyKey.DoctorMonkey, CancellationToken.None));

        var stored = await store.GetAsync(MonkeyKey.LatencyScoutMonkey, CancellationToken.None);
        Assert.NotNull(stored);
        Assert.True(stored!.IsPaused);
        Assert.Equal("tester", stored.UpdatedBy);
    }

    [Fact]
    public async Task ResumingClearsThePause()
    {
        var store = new InMemoryAgentPauseStore();
        var resolver = Build(store, configured: true);

        await store.UpsertAsync(MonkeyKey.LatencySpikeMonkey, isPaused: true, "tester", CancellationToken.None);
        resolver.Invalidate();
        Assert.True(await resolver.IsPausedAsync(MonkeyKey.LatencySpikeMonkey, CancellationToken.None));

        await store.UpsertAsync(MonkeyKey.LatencySpikeMonkey, isPaused: false, "tester", CancellationToken.None);
        resolver.Invalidate();
        Assert.False(await resolver.IsPausedAsync(MonkeyKey.LatencySpikeMonkey, CancellationToken.None));
    }

    private static AgentPauseResolver Build(IAgentPauseStore store, bool configured) =>
        new(store,
            Options.Create(new JourneyStoreOptions { ConnectionString = configured ? "Server=(local);Database=fake;" : null }),
            NullLogger<AgentPauseResolver>.Instance);

    private sealed class ThrowingAgentPauseStore : IAgentPauseStore
    {
        public Task<IReadOnlyList<AgentPauseSetting>> GetAllAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the pause store is down");

        public Task<AgentPauseSetting?> GetAsync(string monkeyKey, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the pause store is down");

        public Task<AgentPauseSetting> UpsertAsync(string monkeyKey, bool isPaused, string changedBy, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the pause store is down");
    }
}

public class PausedMonkeyRunnerTests
{
    [Fact]
    public async Task APausedLatencySpikeMonkeyDoesNoWorkAtAll()
    {
        var detector = new CountingLatencySpikeDetector();
        var doctor = new CountingDoctorAgent();
        var tracker = new InMemoryMonkeyRunTracker();
        var pauses = FakeAgentPauseResolver.NothingPaused().Pause(MonkeyKey.LatencySpikeMonkey);
        var runner = new LatencySpikeMonkeyRunner(detector, doctor, tracker, pauses, NullLogger<LatencySpikeMonkeyRunner>.Instance);

        var summary = await runner.RunAsync(CancellationToken.None);

        Assert.Equal(MonkeyKey.LatencySpikeMonkey, summary.MonkeyKey);
        Assert.Equal(0, summary.NewFindings);
        Assert.Equal(0, summary.Diagnosed);
        Assert.Equal(0, summary.Notified);
        Assert.Equal(0, detector.RunCalls);
        Assert.Equal(0, doctor.DiagnoseCalls);
        // A paused agent that still refreshed its "last run" would look alive on the card.
        Assert.Null(await tracker.GetLastRunAsync(MonkeyKey.LatencySpikeMonkey));
    }

    [Fact]
    public async Task ResumingLetsTheLatencySpikeMonkeyRunAgain()
    {
        var detector = new CountingLatencySpikeDetector();
        var pauses = FakeAgentPauseResolver.NothingPaused().Pause(MonkeyKey.LatencySpikeMonkey);
        var tracker = new InMemoryMonkeyRunTracker();
        var runner = new LatencySpikeMonkeyRunner(detector, new CountingDoctorAgent(), tracker, pauses, NullLogger<LatencySpikeMonkeyRunner>.Instance);

        await runner.RunAsync(CancellationToken.None);
        Assert.Equal(0, detector.RunCalls);

        pauses.Resume(MonkeyKey.LatencySpikeMonkey);
        await runner.RunAsync(CancellationToken.None);

        Assert.Equal(LatencySpikeMonkeyRunner.Domains.Length, detector.RunCalls);
        Assert.NotNull(await tracker.GetLastRunAsync(MonkeyKey.LatencySpikeMonkey));
    }

    [Fact]
    public async Task APausedDoctorMonkeyNeverReadsTheFindingsBacklog()
    {
        var doctor = new CountingDoctorAgent();
        var findings = new InMemoryFindingsRepository();
        await findings.AddAsync(OpenFinding());

        var pauses = FakeAgentPauseResolver.NothingPaused().Pause(MonkeyKey.DoctorMonkey);
        var runner = new DoctorMonkeyRunner(
            findings, new InMemoryDoctorReportsRepository(), doctor, new InMemoryMonkeyRunTracker(), pauses,
            NullLogger<DoctorMonkeyRunner>.Instance);

        var summary = await runner.RunAsync(CancellationToken.None);

        Assert.Equal(0, summary.Diagnosed);
        Assert.Equal(0, doctor.DiagnoseCalls);
    }

    private static Finding OpenFinding() => new()
    {
        Domain = "cars",
        FindingType = FindingType.LatencySpikeCluster,
        Stage = "07 Booking",
        Severity = "medium",
        Title = "something slow",
        EvidenceJson = "{}",
        CorrelationId = "cars:07 Booking",
        OriginMonkeyKey = MonkeyKey.LatencySpikeMonkey,
    };
}
