using System.Collections.Concurrent;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Infrastructure.Monitoring;

namespace JourneyMonitor.Tests.Monitoring;

public class InMemoryAgentWorkQueueTests
{
    private const string PayloadType = AgentWorkPayloadType.InvestigationPackage;

    /// <summary>
    /// A lease is taken for a duration, so a negative one lands in the past: that is how an expired
    /// lease is produced deterministically without sleeping.
    /// </summary>
    private static readonly TimeSpan AlreadyExpired = TimeSpan.FromSeconds(-1);

    private static readonly TimeSpan StillValid = TimeSpan.FromMinutes(5);

    private static AgentWorkItem Item(
        string dedupKey,
        int priority = 0,
        DateTimeOffset? enqueuedAt = null,
        int maxAttempts = 3,
        string payloadType = PayloadType) => new()
        {
            SourceMonkey = MonkeyKey.LatencyScoutMonkey,
            PayloadType = payloadType,
            PayloadId = Guid.NewGuid().ToString(),
            DedupKey = dedupKey,
            Priority = priority,
            MaxAttempts = maxAttempts,
            EnqueuedAt = enqueuedAt ?? DateTimeOffset.UtcNow,
        };

    [Fact]
    public async Task EnqueueReturnsFalseForADuplicateDedupKeyAndDoesNotAddIt()
    {
        var queue = new InMemoryAgentWorkQueue();

        Assert.True(await queue.EnqueueAsync(Item("case-1")));
        Assert.False(await queue.EnqueueAsync(Item("case-1")));

        var queued = await queue.GetQueuedAsync(PayloadType, 10);
        Assert.Single(queued);
    }

    [Fact]
    public async Task LeaseTakesTheHighestPriorityFirstAndBreaksTiesOnEnqueuedAt()
    {
        var queue = new InMemoryAgentWorkQueue();
        var now = DateTimeOffset.UtcNow;
        await queue.EnqueueAsync(Item("low", priority: 1, enqueuedAt: now.AddMinutes(-30)));
        await queue.EnqueueAsync(Item("high-late", priority: 9, enqueuedAt: now.AddMinutes(-5)));
        await queue.EnqueueAsync(Item("high-early", priority: 9, enqueuedAt: now.AddMinutes(-20)));

        var first = await queue.TryLeaseAsync(PayloadType, "worker", StillValid);
        var second = await queue.TryLeaseAsync(PayloadType, "worker", StillValid);
        var third = await queue.TryLeaseAsync(PayloadType, "worker", StillValid);

        Assert.Equal("high-early", first?.DedupKey);
        Assert.Equal("high-late", second?.DedupKey);
        Assert.Equal("low", third?.DedupKey);
        Assert.Null(await queue.TryLeaseAsync(PayloadType, "worker", StillValid));
    }

    [Fact]
    public async Task AValidLeaseHidesTheItemAndAnExpiredOneHandsItBack()
    {
        var queue = new InMemoryAgentWorkQueue();
        await queue.EnqueueAsync(Item("case-1"));

        var held = await queue.TryLeaseAsync(PayloadType, "worker-a", StillValid);
        Assert.NotNull(held);
        Assert.Null(await queue.TryLeaseAsync(PayloadType, "worker-b", StillValid));

        var expiring = new InMemoryAgentWorkQueue();
        await expiring.EnqueueAsync(Item("case-2"));
        var dead = await expiring.TryLeaseAsync(PayloadType, "worker-a", AlreadyExpired);
        Assert.NotNull(dead);

        var reclaimed = await expiring.TryLeaseAsync(PayloadType, "worker-b", StillValid);
        Assert.NotNull(reclaimed);
        Assert.Equal(dead.Id, reclaimed.Id);
        Assert.Equal("worker-b", reclaimed.LeasedBy);
        Assert.Equal(2, reclaimed.Attempts);
    }

    [Fact]
    public async Task AnExpiredLeaseIsNotHandedBackOnceTheAttemptsAreGone()
    {
        var queue = new InMemoryAgentWorkQueue();
        await queue.EnqueueAsync(Item("poison", maxAttempts: 2));

        // A consumer that dies mid-investigation never reaches FailAsync, so the attempts guard on
        // the expired-lease branch is the only thing that stops the item costing LLM calls forever.
        var first = await queue.TryLeaseAsync(PayloadType, "worker-a", AlreadyExpired);
        var second = await queue.TryLeaseAsync(PayloadType, "worker-b", AlreadyExpired);

        Assert.Equal(1, first?.Attempts);
        Assert.Equal(2, second?.Attempts);
        Assert.Null(await queue.TryLeaseAsync(PayloadType, "worker-c", StillValid));
        Assert.Empty(await queue.GetQueuedAsync(PayloadType, 10));
    }

    [Fact]
    public async Task FailLeavesTheItemRetryableWhileAttemptsRemain()
    {
        var queue = new InMemoryAgentWorkQueue();
        await queue.EnqueueAsync(Item("case-1", maxAttempts: 3));

        var leased = await queue.TryLeaseAsync(PayloadType, "worker-a", StillValid);
        Assert.NotNull(leased);
        await queue.FailAsync(leased.Id, "worker-a", "tool timeout");

        var queued = await queue.GetQueuedAsync(PayloadType, 10);
        var failed = Assert.Single(queued);
        Assert.Equal(AgentWorkItemStatus.Failed, failed.Status);
        Assert.Equal("tool timeout", failed.LastError);
        Assert.NotNull(failed.CompletedAt);
        Assert.Null(failed.LeasedBy);

        var retried = await queue.TryLeaseAsync(PayloadType, "worker-b", StillValid);
        Assert.Equal(leased.Id, retried?.Id);
        // The claim puts the item back in flight, so the previous attempt's end time is cleared.
        Assert.Null(retried?.CompletedAt);
    }

    [Fact]
    public async Task FailAbandonsTheItemOnceTheAttemptsAreGone()
    {
        var queue = new InMemoryAgentWorkQueue();
        await queue.EnqueueAsync(Item("poison", maxAttempts: 1));

        var leased = await queue.TryLeaseAsync(PayloadType, "worker-a", StillValid);
        Assert.NotNull(leased);
        await queue.FailAsync(leased.Id, "worker-a", "llm unavailable");

        Assert.Empty(await queue.GetQueuedAsync(PayloadType, 10));
        Assert.Null(await queue.TryLeaseAsync(PayloadType, "worker-b", StillValid));

        var stats = await queue.GetStatsAsync(PayloadType);
        Assert.Equal(1, stats.AbandonedTotal);
        Assert.Equal(0, stats.FailedToday);
    }

    [Fact]
    public async Task ConcurrentConsumersNeverClaimTheSameItemTwice()
    {
        const int itemCount = 200;
        const int workerCount = 8;
        var queue = new InMemoryAgentWorkQueue();
        var enqueuedAt = DateTimeOffset.UtcNow;
        for (var i = 0; i < itemCount; i++)
        {
            // Same priority and timestamp on purpose: every worker is contending for the same head.
            await queue.EnqueueAsync(Item($"case-{i}", priority: 5, enqueuedAt: enqueuedAt));
        }

        var claimed = new ConcurrentBag<Guid>();
        using var start = new ManualResetEventSlim(false);
        var workers = Enumerable.Range(0, workerCount).Select(w => Task.Run(() =>
        {
            start.Wait();
            while (true)
            {
                var item = queue.TryLeaseAsync(PayloadType, $"worker-{w}", StillValid).GetAwaiter().GetResult();
                if (item is null) break;
                claimed.Add(item.Id);
            }
        })).ToArray();

        start.Set();
        await Task.WhenAll(workers);

        Assert.Equal(itemCount, claimed.Count);
        Assert.Equal(itemCount, claimed.Distinct().Count());
        Assert.Empty(await queue.GetQueuedAsync(PayloadType, itemCount));
    }

    [Fact]
    public async Task StatsCountAnExpiredLeaseAsQueuedRatherThanLeased()
    {
        var queue = new InMemoryAgentWorkQueue();
        var enqueuedAt = DateTimeOffset.UtcNow.AddHours(-1);
        // Priorities keep the two claims apart: an expired lease is claimable again, so taking it
        // first would just hand the same item to the second consumer.
        await queue.EnqueueAsync(Item("dead", priority: 1, enqueuedAt: enqueuedAt));
        await queue.EnqueueAsync(Item("alive", priority: 9));
        await queue.TryLeaseAsync(PayloadType, "live-worker", StillValid);
        await queue.TryLeaseAsync(PayloadType, "crashed-worker", AlreadyExpired);

        var stats = await queue.GetStatsAsync(PayloadType);

        Assert.Equal(1, stats.Queued);
        Assert.Equal(1, stats.Leased);
        Assert.Equal(enqueuedAt, stats.OldestQueuedAt);
    }

    [Fact]
    public async Task StatsAreScopedToTheRequestedPayloadType()
    {
        var queue = new InMemoryAgentWorkQueue();
        await queue.EnqueueAsync(Item("mine"));
        await queue.EnqueueAsync(Item("someone-elses", payloadType: "other-payload"));

        var stats = await queue.GetStatsAsync(PayloadType);

        Assert.Equal(1, stats.Queued);
        Assert.Single(await queue.GetQueuedAsync(PayloadType, 10));
    }

    [Fact]
    public async Task CompleteTakesTheItemOutOfTheQueueAndCountsAsDoneToday()
    {
        var queue = new InMemoryAgentWorkQueue();
        await queue.EnqueueAsync(Item("case-1"));
        var leased = await queue.TryLeaseAsync(PayloadType, "worker", StillValid);
        Assert.NotNull(leased);

        await queue.CompleteAsync(leased.Id, "worker");

        Assert.Empty(await queue.GetQueuedAsync(PayloadType, 10));
        var stats = await queue.GetStatsAsync(PayloadType);
        Assert.Equal(0, stats.Queued);
        Assert.Equal(0, stats.Leased);
        Assert.Equal(1, stats.DoneToday);
    }

    [Fact]
    public async Task DiscardWithoutIdsTakesEveryWaitingItemOutOfTheQueue()
    {
        var queue = new InMemoryAgentWorkQueue();
        await queue.EnqueueAsync(Item("case-1"));
        await queue.EnqueueAsync(Item("case-2"));

        var discarded = await queue.DiscardQueuedAsync(PayloadType, null, "not worth it", "claudio");

        Assert.Equal(2, discarded);
        Assert.Empty(await queue.GetQueuedAsync(PayloadType, 10));
        var stats = await queue.GetStatsAsync(PayloadType);
        Assert.Equal(0, stats.Queued);
        Assert.Equal(2, stats.DiscardedTotal);
    }

    [Fact]
    public async Task DiscardOnlyTouchesTheSelectedIds()
    {
        var queue = new InMemoryAgentWorkQueue();
        var keep = Item("keep");
        var drop = Item("drop");
        await queue.EnqueueAsync(keep);
        await queue.EnqueueAsync(drop);

        var discarded = await queue.DiscardQueuedAsync(PayloadType, [drop.Id], "duplicate signature", "claudio");

        Assert.Equal(1, discarded);
        var remaining = await queue.GetQueuedAsync(PayloadType, 10);
        Assert.Equal("keep", Assert.Single(remaining).DedupKey);
    }

    [Fact]
    public async Task DiscardWithAnEmptySelectionTouchesNothing()
    {
        var queue = new InMemoryAgentWorkQueue();
        await queue.EnqueueAsync(Item("case-1"));

        // An explicit selection of nothing must never widen into "everything", which is what would
        // happen if an empty list were treated the same as "no filter".
        var discarded = await queue.DiscardQueuedAsync(PayloadType, [], "nothing selected", "claudio");

        Assert.Equal(0, discarded);
        Assert.Single(await queue.GetQueuedAsync(PayloadType, 10));
    }

    [Fact]
    public async Task DiscardLeavesAnItemWithALiveLeaseAlone()
    {
        var queue = new InMemoryAgentWorkQueue();
        await queue.EnqueueAsync(Item("in-flight"));
        var leased = await queue.TryLeaseAsync(PayloadType, "worker", StillValid);
        Assert.NotNull(leased);

        // The worker is already paying for this investigation; flipping the row would not stop it,
        // it would only make the queue disagree with what is actually running.
        var discarded = await queue.DiscardQueuedAsync(PayloadType, null, "clear everything", "claudio");

        Assert.Equal(0, discarded);
        Assert.Equal(1, (await queue.GetStatsAsync(PayloadType)).Leased);
    }

    [Fact]
    public async Task DiscardIsScopedToTheRequestedPayloadType()
    {
        var queue = new InMemoryAgentWorkQueue();
        await queue.EnqueueAsync(Item("mine"));
        await queue.EnqueueAsync(Item("someone-elses", payloadType: "other-payload"));

        var discarded = await queue.DiscardQueuedAsync(PayloadType, null, "clear mine", "claudio");

        Assert.Equal(1, discarded);
        Assert.Single(await queue.GetQueuedAsync("other-payload", 10));
    }

    [Fact]
    public async Task DiscardRecordsWhoAskedAndWhyInsteadOfDeletingTheRow()
    {
        var queue = new InMemoryAgentWorkQueue();
        await queue.EnqueueAsync(Item("case-1"));

        await queue.DiscardQueuedAsync(PayloadType, null, "unmapped operation, fix the catalog first", "claudio");

        // A queue that silently shrinks is indistinguishable from one that silently loses work, so
        // the discarded row must still be countable afterwards.
        var stats = await queue.GetStatsAsync(PayloadType);
        Assert.Equal(1, stats.DiscardedTotal);
        Assert.Equal(0, stats.AbandonedTotal);
    }

    [Fact]
    public async Task ADiscardedItemIsNeverLeasedAgain()
    {
        var queue = new InMemoryAgentWorkQueue();
        await queue.EnqueueAsync(Item("case-1"));
        await queue.DiscardQueuedAsync(PayloadType, null, "not worth it", "claudio");

        Assert.Null(await queue.TryLeaseAsync(PayloadType, "worker", StillValid));
    }
}

public class InMemoryForensicCaseRepositoryTests
{
    private static ForensicCase Case(Guid? packageId = null, string status = ForensicCaseStatus.Running) => new()
    {
        InvestigationPackageId = packageId ?? Guid.NewGuid(),
        CaseId = "club-1 | checkout | 2026-10-07T10",
        EntityKey = "club-1",
        Vertical = "cruises",
        Stage = "checkout",
        Status = status,
    };

    [Fact]
    public async Task GetByPackageIdReturnsNullWhenNoCaseExistsForThatPackage()
    {
        var repository = new InMemoryForensicCaseRepository();

        Assert.Null(await repository.GetByPackageIdAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task GetByPackageIdReturnsTheCaseOpenedForThatPackage()
    {
        var repository = new InMemoryForensicCaseRepository();
        var forensicCase = Case();
        await repository.AddAsync(forensicCase);

        var found = await repository.GetByPackageIdAsync(forensicCase.InvestigationPackageId);

        Assert.Equal(forensicCase.Id, found?.Id);
    }

    [Fact]
    public async Task AddRejectsASecondCaseForTheSamePackage()
    {
        var repository = new InMemoryForensicCaseRepository();
        var packageId = Guid.NewGuid();
        await repository.AddAsync(Case(packageId));

        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.AddAsync(Case(packageId)));
    }

    [Fact]
    public async Task UpdatePhaseOnAnUnknownIdDoesNotThrow()
    {
        var repository = new InMemoryForensicCaseRepository();

        await repository.UpdatePhaseAsync(Guid.NewGuid(), ForensicCasePhase.Agents);
    }

    [Fact]
    public async Task UpdatePhaseIsIgnoredOnceTheCaseHasFinished()
    {
        var repository = new InMemoryForensicCaseRepository();
        var forensicCase = Case();
        await repository.AddAsync(forensicCase);

        await repository.UpdatePhaseAsync(forensicCase.Id, ForensicCasePhase.Agents);
        forensicCase.CompletedAt = DateTimeOffset.UtcNow;
        await repository.UpdatePhaseAsync(forensicCase.Id, ForensicCasePhase.Verdict);

        var stored = await repository.GetByIdAsync(forensicCase.Id);
        Assert.Equal(ForensicCasePhase.Agents, stored?.Phase);
    }

    [Fact]
    public async Task StatsCountEveryCaseOpenedIncludingTheFailedOnes()
    {
        var repository = new InMemoryForensicCaseRepository();
        var resolved = Case(status: ForensicCaseStatus.Resolved);
        resolved.CitationsTotal = 4;
        resolved.CitationsVerified = 3;
        resolved.LlmCalls = 7;
        await repository.AddAsync(resolved);
        await repository.AddAsync(Case(status: ForensicCaseStatus.Failed));
        await repository.AddAsync(Case(status: ForensicCaseStatus.Running));

        var stats = await repository.GetStatsAsync();

        Assert.Equal(3, stats.Investigations);
        Assert.Equal(1, stats.Resolved);
        Assert.Equal(1, stats.Failed);
        Assert.Equal(4, stats.CitationsTotal);
        Assert.Equal(3, stats.CitationsVerified);
        Assert.Equal(7, stats.LlmCalls);
        Assert.Equal(3, stats.InvestigationsToday);
    }

    [Fact]
    public async Task StatsAreEmptyBeforeAnyInvestigation()
    {
        var repository = new InMemoryForensicCaseRepository();

        Assert.Equal(InspectorStats.Empty, await repository.GetStatsAsync());
    }
}

public class InMemoryAgentTranscriptRepositoryTests
{
    private static AgentTurn Turn(Guid caseId, DateTimeOffset createdAt) => new()
    {
        ForensicCaseId = caseId,
        AgentRole = InspectorAgent.Logs,
        Kind = AgentKind.Specialist,
        PromptText = "prompt",
        RawResponseText = "response",
        OutcomeStatus = AgentOutcomeStatus.Final,
        CreatedAt = createdAt,
    };

    [Fact]
    public async Task TurnsComeBackInOrderAndOnlyForTheRequestedCase()
    {
        var repository = new InMemoryAgentTranscriptRepository();
        var caseId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var second = await repository.AddTurnAsync(Turn(caseId, now));
        var first = await repository.AddTurnAsync(Turn(caseId, now.AddMinutes(-1)));
        await repository.AddTurnAsync(Turn(Guid.NewGuid(), now));

        var turns = await repository.GetTurnsAsync(caseId);

        Assert.Equal(new[] { first.Id, second.Id }, turns.Select(t => t.Id).ToArray());
    }

    [Fact]
    public async Task ToolCallsAreResolvedThroughTheirTurn()
    {
        var repository = new InMemoryAgentTranscriptRepository();
        var caseId = Guid.NewGuid();
        var turn = await repository.AddTurnAsync(Turn(caseId, DateTimeOffset.UtcNow));
        await repository.AddToolCallAsync(new AgentToolCall
        {
            AgentTurnId = turn.Id,
            ToolName = "run_kql",
            ArgumentsJson = "{}",
            ResultJson = "[]",
            Success = true,
        });
        await repository.AddToolCallAsync(new AgentToolCall
        {
            AgentTurnId = Guid.NewGuid(),
            ToolName = "run_kql",
            ArgumentsJson = "{}",
            ResultJson = "[]",
            Success = true,
        });

        var toolCalls = await repository.GetToolCallsAsync(caseId);

        var only = Assert.Single(toolCalls);
        Assert.Equal(turn.Id, only.AgentTurnId);
    }

    [Fact]
    public async Task AnUnknownCaseHasAnEmptyTranscriptRatherThanThrowing()
    {
        var repository = new InMemoryAgentTranscriptRepository();

        Assert.Empty(await repository.GetTurnsAsync(Guid.NewGuid()));
        Assert.Empty(await repository.GetToolCallsAsync(Guid.NewGuid()));
    }
}
