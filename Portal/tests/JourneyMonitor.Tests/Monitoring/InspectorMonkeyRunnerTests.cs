using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring;
using JourneyMonitor.Core.Monitoring.Inspector;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Core.UseCases;
using JourneyMonitor.Infrastructure.Monitoring;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Tests.Monitoring;

public class InspectorMonkeyRunnerTests
{
    private const string DebateAnswer = """{"status":"answer","hypothesis":"vendor timeouts","supportingEvidence":["op-aaa"],"confidence":0.8,"unknowns":[]}""";

    [Fact]
    public async Task ClaimsAPackageInvestigatesItAndMarksTheWorkItemDone()
    {
        var (runner, queue, _, _) = await BuildAsync(HappyClient());

        var summary = await runner.RunAsync(CancellationToken.None);

        Assert.Equal(MonkeyKey.InspectorMonkey, summary.MonkeyKey);
        Assert.Equal(1, summary.NewFindings);
        Assert.Equal(1, summary.Diagnosed);

        var stats = await queue.GetStatsAsync(AgentWorkPayloadType.InvestigationPackage);
        Assert.Equal(1, stats.DoneToday);
        Assert.Equal(0, stats.Queued);
    }

    [Fact]
    public async Task AFailedInvestigationReleasesTheLeaseInsteadOfStrandingTheItem()
    {
        // The case already records why it failed; the item must go back to the queue so a
        // transient provider outage is retried rather than silently counted as investigated.
        var (runner, queue, cases, _) = await BuildAsync(ScriptedToolCallingLlmClient.AlwaysUnavailable());

        var summary = await runner.RunAsync(CancellationToken.None);

        Assert.Equal(0, summary.Diagnosed);

        var stats = await queue.GetStatsAsync(AgentWorkPayloadType.InvestigationPackage);
        Assert.Equal(1, stats.FailedToday);
        Assert.Equal(0, stats.DoneToday);
        // Eligible again - a released item is the whole point of reporting the failure.
        Assert.Equal(1, stats.Queued);

        var forensicCase = Assert.Single(await cases.GetRecentAsync(10));
        Assert.Equal(ForensicCaseStatus.Failed, forensicCase.Status);
        Assert.False(string.IsNullOrWhiteSpace(forensicCase.ErrorMessage));
        Assert.NotNull(forensicCase.CompletedAt);
    }

    [Fact]
    public async Task LeasesNothingOnceTheDailyCapIsReached()
    {
        // This cap is the only thing that bounds what the Inspector costs - one investigation is
        // ~20-25 completions against much larger prompts than the Scout's.
        var options = DefaultOptions();
        options.MaxInvestigationsPerDay = 40;

        var (runner, queue, _, llm) = await BuildAsync(HappyClient(), options, new StubStatsForensicCaseRepository(investigationsToday: 40));

        var summary = await runner.RunAsync(CancellationToken.None);

        Assert.Equal(0, summary.NewFindings);
        Assert.Equal(0, llm.Calls);
        var stats = await queue.GetStatsAsync(AgentWorkPayloadType.InvestigationPackage);
        Assert.Equal(1, stats.Queued);
        Assert.Equal(0, stats.Leased);
    }

    [Fact]
    public async Task LeasesNothingWhenTheInspectorIsDisabledSoTheBacklogIsWaitingRatherThanLost()
    {
        var options = DefaultOptions();
        options.Enabled = false;

        var (runner, queue, _, llm) = await BuildAsync(HappyClient(), options);

        await runner.RunAsync(CancellationToken.None);

        Assert.Equal(0, llm.Calls);
        Assert.Equal(1, (await queue.GetStatsAsync(AgentWorkPayloadType.InvestigationPackage)).Queued);
    }

    [Fact]
    public async Task AnItemPointingAtAPackageThatNoLongerExistsFailsWithAReasonRatherThanThrowing()
    {
        var (runner, queue, _, _) = await BuildAsync(HappyClient(), payloadId: Guid.NewGuid().ToString());

        var summary = await runner.RunAsync(CancellationToken.None);

        Assert.Equal(0, summary.Diagnosed);
        Assert.Equal(1, (await queue.GetStatsAsync(AgentWorkPayloadType.InvestigationPackage)).FailedToday);
    }

    [Fact]
    public async Task APausedInspectorLeasesNothingAndSpendsNothing()
    {
        var (runner, queue, cases, _) = await BuildAsync(
            HappyClient(),
            pauseResolver: FakeAgentPauseResolver.NothingPaused().Pause(MonkeyKey.InspectorMonkey));

        var summary = await runner.RunAsync(CancellationToken.None);

        Assert.Equal(0, summary.NewFindings);
        Assert.Equal(0, summary.Diagnosed);
        Assert.Empty(await cases.GetRecentAsync(10));

        // The package is still queued, not leased: a paused agent must not hold work hostage.
        var stats = await queue.GetStatsAsync(AgentWorkPayloadType.InvestigationPackage);
        Assert.Equal(1, stats.Queued);
        Assert.Equal(0, stats.Leased);
        Assert.Equal(0, stats.DoneToday);
    }

    // ---------------------------------------------------------------------------------------------
    // Fixtures
    // ---------------------------------------------------------------------------------------------

    private static InspectorOptions DefaultOptions() => new()
    {
        DebateModels = "model-a",
        PlannerModel = "planner",
        DebateRounds = 0,
        Concurrency = 1,
    };

    private static ScriptedToolCallingLlmClient HappyClient() =>
        new((model, _) => InspectorFixtures.Answer(model, model == "planner" ? InspectorFixtures.GoodSynthesis : DebateAnswer));

    private static async Task<(InspectorMonkeyRunner Runner, IAgentWorkQueue Queue, IForensicCaseRepository Cases, ScriptedToolCallingLlmClient Llm)> BuildAsync(
        ScriptedToolCallingLlmClient llm,
        InspectorOptions? options = null,
        IForensicCaseRepository? cases = null,
        string? payloadId = null,
        IAgentPauseResolver? pauseResolver = null)
    {
        var resolved = Options.Create(options ?? DefaultOptions());
        var packages = new InMemoryInvestigationPackageRepository();
        var package = await packages.AddAsync(InspectorFixtures.Package());

        var queue = new InMemoryAgentWorkQueue();
        await queue.EnqueueAsync(new AgentWorkItem
        {
            SourceMonkey = MonkeyKey.LatencyScoutMonkey,
            PayloadType = AgentWorkPayloadType.InvestigationPackage,
            PayloadId = payloadId ?? package.Id.ToString(),
            DedupKey = package.CaseId,
            Priority = package.PriorityRank,
        });

        var caseRepository = cases ?? new InMemoryForensicCaseRepository();
        var transcripts = new InMemoryAgentTranscriptRepository();
        var recorder = new InspectorRecorder(transcripts, NullLogger<InspectorRecorder>.Instance);
        var budgets = InspectorFixtures.BudgetResolver(resolved.Value);
        var planner = new InspectorPlanner([], llm, recorder, budgets, resolved, NullLogger<InspectorPlanner>.Instance);
        var service = new InspectorMonkeyService(
            caseRepository,
            new FakeClubIdentityResolver(),
            planner,
            new InspectorGroundingService(transcripts, recorder, NullLogger<InspectorGroundingService>.Instance),
            new InspectorVerdictService(resolved),
            new RedAlertPolicy(Options.Create(new RedAlertOptions())),
            InspectorFixtures.OwnerResolver(),
            new CountingNotificationService(),
            InspectorFixtures.RcaComposer(),
            recorder,
            resolved,
            NullLogger<InspectorMonkeyService>.Instance);

        var runner = new InspectorMonkeyRunner(
            queue, packages, caseRepository, service, new InMemoryMonkeyRunTracker(),
            pauseResolver ?? FakeAgentPauseResolver.NothingPaused(), budgets, resolved,
            NullLogger<InspectorMonkeyRunner>.Instance);

        return (runner, queue, caseRepository, llm);
    }
}
