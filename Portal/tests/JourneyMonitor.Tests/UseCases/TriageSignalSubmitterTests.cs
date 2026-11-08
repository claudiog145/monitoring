using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.UseCases;
using JourneyMonitor.Tests.Spec1000.A4;
using Microsoft.Extensions.Logging.Abstractions;

namespace JourneyMonitor.Tests.UseCases;

public sealed class TriageSignalSubmitterTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    private readonly A4FakeInvestigationStore _store = new();
    private readonly A4FakeWorkQueue _queue = new();

    private TriageSignalSubmitter Submitter() =>
        new(_store, _queue, new A4FakeTimeProvider(Now), NullLogger<TriageSignalSubmitter>.Instance);

    private static TriageSignal DaemonSignal(string pillar = "Application Health", long? anomalyId = 42) =>
        new(Now.AddMinutes(-5), pillar, "Latency", "2 consecutive increases", "hotels", null, anomalyId, TriageSignalSources.Daemon);

    [Fact]
    public async Task Submitter_CreatesInvestigationAndEnqueuesWork()
    {
        var signal = DaemonSignal();

        var submission = await Submitter().SubmitAsync(signal);

        Assert.False(submission.Duplicate);
        var investigation = Assert.Single(_store.Items.Values);
        Assert.Equal(submission.InvestigationId, investigation.InvestigationId);
        Assert.Equal("anomaly:42", investigation.DedupKey);
        Assert.Equal(42, investigation.AnomalyId);
        Assert.Equal(signal, investigation.Signal);
        Assert.Equal(TriageInvestigationStatus.Queued, investigation.Status);
        Assert.Null(investigation.Result);
        Assert.Null(investigation.AlertLevel);
        Assert.False(investigation.Notified);
        Assert.Equal(Now, investigation.CreatedAt);
        Assert.Null(investigation.CompletedAt);
        Assert.Null(investigation.Error);

        var work = Assert.Single(_queue.Enqueued);
        Assert.Equal("triage-signal", work.PayloadType);
        Assert.Equal(AgentWorkPayloadType.TriageSignal, work.PayloadType);
        Assert.Equal(submission.InvestigationId.ToString(), work.PayloadId);
        Assert.Equal($"triage:{submission.InvestigationId}", work.DedupKey);
        Assert.Equal(100, work.Priority);
        Assert.Equal(MonkeyKey.ApplicationHealthMonitor, work.SourceMonkey);
    }

    [Theory]
    [InlineData("Application Health", TriageSignalSources.Daemon, MonkeyKey.ApplicationHealthMonitor)]
    [InlineData("Business Health", TriageSignalSources.Daemon, MonkeyKey.BusinessHealthMonitor)]
    [InlineData("Infrastructure Health", TriageSignalSources.Daemon, MonkeyKey.InfrastructureHealthMonitor)]
    [InlineData("Business Health", TriageSignalSources.Manual, "portal")]
    public async Task Submitter_SourceMonkeyFollowsSourceAndPillar(string pillar, string source, string expectedSourceMonkey)
    {
        var signal = DaemonSignal(pillar, anomalyId: null) with { Source = source };

        await Submitter().SubmitAsync(signal);

        Assert.Equal(expectedSourceMonkey, Assert.Single(_queue.Enqueued).SourceMonkey);
    }

    [Fact]
    public async Task Submitter_OpenDuplicate_DoesNotEnqueueAgain()
    {
        var submitter = Submitter();

        var first = await submitter.SubmitAsync(DaemonSignal());
        var second = await submitter.SubmitAsync(DaemonSignal());

        Assert.False(first.Duplicate);
        Assert.True(second.Duplicate);
        Assert.Equal(first.InvestigationId, second.InvestigationId);
        Assert.Single(_store.Items);
        Assert.Single(_queue.Enqueued);
    }

    [Fact]
    public async Task Submitter_ClosedInvestigationWithSameKey_CreatesANewOne()
    {
        var submitter = Submitter();
        var first = await submitter.SubmitAsync(DaemonSignal());
        await _store.UpdateStatusAsync(first.InvestigationId, TriageInvestigationStatus.Completed, null);

        var second = await submitter.SubmitAsync(DaemonSignal());

        Assert.False(second.Duplicate);
        Assert.NotEqual(first.InvestigationId, second.InvestigationId);
        Assert.Equal(2, _queue.Enqueued.Count);
    }

    [Fact]
    public async Task Submitter_ConcurrentCreateConflict_ReturnsExistingAsDuplicate()
    {
        var signal = DaemonSignal();
        var winnerId = Guid.NewGuid();
        _store.ConcurrentWinnerOnCreate = new TriageInvestigation(
            winnerId, TriageDedupKey.For(signal), signal.AnomalyId, signal, TriageInvestigationStatus.Queued,
            null, null, false, Now, null, null);

        var submission = await Submitter().SubmitAsync(signal);

        Assert.True(submission.Duplicate);
        Assert.Equal(winnerId, submission.InvestigationId);
        Assert.Empty(_queue.Enqueued);
        Assert.Single(_store.Items);
    }

    [Fact]
    public async Task Submitter_CreateThrowsAfterCommit_EnqueuesOwnInvestigation()
    {
        _store.ThrowAfterCommitOnCreate = true;

        var submission = await Submitter().SubmitAsync(DaemonSignal());

        Assert.False(submission.Duplicate);
        var stored = Assert.Single(_store.Items.Values);
        Assert.Equal(stored.InvestigationId, submission.InvestigationId);
        Assert.Equal(TriageInvestigationStatus.Queued, stored.Status);
        var work = Assert.Single(_queue.Enqueued);
        Assert.Equal(submission.InvestigationId.ToString(), work.PayloadId);
        Assert.Equal($"triage:{submission.InvestigationId}", work.DedupKey);
    }

    [Fact]
    public async Task Submitter_RequeryFails_RethrowsOriginalException()
    {
        _store.ThrowOnCreate = true;
        _store.ThrowOnRequery = true;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Submitter().SubmitAsync(DaemonSignal()));

        Assert.Equal("store down", ex.Message);
        Assert.Empty(_queue.Enqueued);
    }

    [Fact]
    public async Task Submitter_CreateFailureWithoutOpenInvestigation_Rethrows()
    {
        _store.ThrowOnCreate = true;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Submitter().SubmitAsync(DaemonSignal()));

        Assert.Equal("store down", ex.Message);
        Assert.Empty(_queue.Enqueued);
    }

    [Fact]
    public async Task Submitter_EnqueueFailure_MarksInvestigationFailedAndRethrows()
    {
        _queue.ThrowOnEnqueue = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => Submitter().SubmitAsync(DaemonSignal()));

        var investigation = Assert.Single(_store.Items.Values);
        Assert.Equal(TriageInvestigationStatus.Failed, investigation.Status);
        var update = Assert.Single(_store.StatusUpdates);
        Assert.Equal(TriageInvestigationStatus.Failed, update.Status);
        Assert.StartsWith("enqueue failed: ", update.Error);
    }
}
