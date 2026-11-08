using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring;
using JourneyMonitor.Core.Monitoring.Inspector;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Core.UseCases;
using JourneyMonitor.Infrastructure.Monitoring;
using JourneyMonitor.Tests.Monitoring;
using Microsoft.Extensions.Logging.Abstractions;

namespace JourneyMonitor.Tests.Spec1000.A5;

/// <summary>
/// InspectorMonkeyRunner holds a static per-process run lock, so these tests must not overlap with
/// any other class that runs the Inspector; this collection runs on its own.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class A5InspectorRunnerCollection
{
    public const string Name = "A5 Inspector runner (serial)";
}

[Collection(A5InspectorRunnerCollection.Name)]
public class TriageRunnerTests
{
    private const string DebateAnswer = """{"status":"answer","hypothesis":"vendor timeouts","supportingEvidence":["op-aaa"],"confidence":0.8,"unknowns":[]}""";

    [Fact]
    public async Task Runner_LeasesTriageSignalBeforeInvestigationPackages()
    {
        var fixture = await BuildAsync(withPackage: true);
        var investigation = A5TriageFixtures.Queued();
        await fixture.TriageStore.CreateAsync(investigation);
        await fixture.Queue.EnqueueAsync(new AgentWorkItem
        {
            SourceMonkey = MonkeyKey.ApplicationHealthMonitor,
            PayloadType = AgentWorkPayloadType.TriageSignal,
            PayloadId = investigation.InvestigationId.ToString(),
            DedupKey = investigation.DedupKey,
            // Lower than the package on purpose: priority between payload types is the runner's, not the queue's.
            Priority = 0,
        });

        var first = await fixture.Runner.RunAsync(CancellationToken.None);

        Assert.Equal(AgentWorkPayloadType.TriageSignal, fixture.Queue.LeaseAttempts.First());
        Assert.Equal(1, first.Diagnosed);
        Assert.Equal(1, fixture.TriageLlm.Calls);
        Assert.Equal(0, fixture.InspectorLlm.Calls);
        Assert.Equal(TriageInvestigationStatus.Completed, (await fixture.TriageStore.GetAsync(investigation.InvestigationId))!.Status);
        var triageStats = await fixture.Queue.GetStatsAsync(AgentWorkPayloadType.TriageSignal);
        Assert.Equal(1, triageStats.DoneToday);
        Assert.Equal(0, triageStats.Leased);

        // With the triage queue drained the next run goes back to Scout packages.
        await fixture.Runner.RunAsync(CancellationToken.None);

        var packageStats = await fixture.Queue.GetStatsAsync(AgentWorkPayloadType.InvestigationPackage);
        Assert.Equal(1, packageStats.DoneToday);
        Assert.Equal(
            [AgentWorkPayloadType.TriageSignal, AgentWorkPayloadType.TriageSignal, AgentWorkPayloadType.InvestigationPackage],
            fixture.Queue.LeaseAttempts.Take(3).ToArray());
    }

    [Fact]
    public async Task Runner_ExistingInvestigationPackageFlowUnchanged()
    {
        var fixture = await BuildAsync(withPackage: true);

        var summary = await fixture.Runner.RunAsync(CancellationToken.None);

        Assert.Equal(MonkeyKey.InspectorMonkey, summary.MonkeyKey);
        Assert.Equal(1, summary.NewFindings);
        Assert.Equal(1, summary.Diagnosed);
        var stats = await fixture.Queue.GetStatsAsync(AgentWorkPayloadType.InvestigationPackage);
        Assert.Equal(1, stats.DoneToday);
        Assert.Equal(0, stats.Queued);
        var forensicCase = Assert.Single(await fixture.Cases.GetRecentAsync(10));
        Assert.NotEqual(ForensicCaseStatus.Failed, forensicCase.Status);
        Assert.Equal(0, fixture.TriageLlm.Calls);
        Assert.Empty(fixture.TriageStore.StatusHistory);
    }

    [Fact]
    public async Task Runner_TriageFailure_FailsTheWorkItemWithTheSameLease()
    {
        var fixture = await BuildAsync(withPackage: false, failingTriageStore: true);
        var investigation = A5TriageFixtures.Queued();
        await fixture.TriageStore.CreateAsync(investigation);
        await fixture.Queue.EnqueueAsync(new AgentWorkItem
        {
            SourceMonkey = MonkeyKey.ApplicationHealthMonitor,
            PayloadType = AgentWorkPayloadType.TriageSignal,
            PayloadId = investigation.InvestigationId.ToString(),
            DedupKey = investigation.DedupKey,
        });

        var summary = await fixture.Runner.RunAsync(CancellationToken.None);

        Assert.Equal(0, summary.Diagnosed);
        var stats = await fixture.Queue.GetStatsAsync(AgentWorkPayloadType.TriageSignal);
        Assert.Equal(1, stats.FailedToday);
        Assert.Equal(0, stats.Leased);
    }

    [Fact]
    public async Task Runner_MissingTriageInvestigation_CompletesTheWorkItem()
    {
        var fixture = await BuildAsync(withPackage: false);
        await fixture.Queue.EnqueueAsync(new AgentWorkItem
        {
            SourceMonkey = MonkeyKey.ApplicationHealthMonitor,
            PayloadType = AgentWorkPayloadType.TriageSignal,
            PayloadId = Guid.NewGuid().ToString(),
            DedupKey = "anomaly:999",
        });

        await fixture.Runner.RunAsync(CancellationToken.None);

        var stats = await fixture.Queue.GetStatsAsync(AgentWorkPayloadType.TriageSignal);
        Assert.Equal(1, stats.DoneToday);
        Assert.Equal(0, fixture.TriageLlm.Calls);
    }

    private sealed record Fixture(
        InspectorMonkeyRunner Runner,
        A5RecordingQueue Queue,
        IForensicCaseRepository Cases,
        A5InMemoryTriageStore TriageStore,
        A5FakeLlmClient TriageLlm,
        ScriptedToolCallingLlmClient InspectorLlm);

    private static async Task<Fixture> BuildAsync(bool withPackage, bool failingTriageStore = false)
    {
        var inspectorOptions = Microsoft.Extensions.Options.Options.Create(new InspectorOptions
        {
            DebateModels = "model-a",
            PlannerModel = "planner",
            DebateRounds = 0,
            Concurrency = 1,
        });
        var inspectorLlm = new ScriptedToolCallingLlmClient((model, _) =>
            InspectorFixtures.Answer(model, model == "planner" ? InspectorFixtures.GoodSynthesis : DebateAnswer));

        var packages = new InMemoryInvestigationPackageRepository();
        var queue = new A5RecordingQueue(new InMemoryAgentWorkQueue());
        if (withPackage)
        {
            var package = await packages.AddAsync(InspectorFixtures.Package());
            await queue.EnqueueAsync(new AgentWorkItem
            {
                SourceMonkey = MonkeyKey.LatencyScoutMonkey,
                PayloadType = AgentWorkPayloadType.InvestigationPackage,
                PayloadId = package.Id.ToString(),
                DedupKey = package.CaseId,
                Priority = package.PriorityRank,
            });
        }

        var cases = new InMemoryForensicCaseRepository();
        var transcripts = new InMemoryAgentTranscriptRepository();
        var recorder = new InspectorRecorder(transcripts, NullLogger<InspectorRecorder>.Instance);
        var budgets = InspectorFixtures.BudgetResolver(inspectorOptions.Value);
        var planner = new InspectorPlanner([], inspectorLlm, recorder, budgets, inspectorOptions, NullLogger<InspectorPlanner>.Instance);
        var inspector = new InspectorMonkeyService(
            cases,
            new FakeClubIdentityResolver(),
            planner,
            new InspectorGroundingService(transcripts, recorder, NullLogger<InspectorGroundingService>.Instance),
            new InspectorVerdictService(inspectorOptions),
            new RedAlertPolicy(Microsoft.Extensions.Options.Options.Create(new RedAlertOptions())),
            InspectorFixtures.OwnerResolver(),
            new CountingNotificationService(),
            InspectorFixtures.RcaComposer(),
            recorder,
            inspectorOptions,
            NullLogger<InspectorMonkeyService>.Instance);

        var triageStore = new A5InMemoryTriageStore();
        var triageLlm = A5FakeLlmClient.Returning(A5TriageFixtures.AmberJson);
        ITriageInvestigationStore serviceStore = failingTriageStore ? new A5FailingCompleteTriageStore(triageStore) : triageStore;
        var triage = new TriageMonkeyService(
            serviceStore,
            A5TriageFixtures.AllAvailableProviders(),
            triageLlm,
            new A5PauseResolver(),
            new A5BudgetResolver(),
            new A5RecordingNotificationService(),
            Microsoft.Extensions.Options.Options.Create(new TriageOptions()),
            new A5FixedTimeProvider(A5TriageFixtures.Now),
            NullLogger<TriageMonkeyService>.Instance);

        var runner = new InspectorMonkeyRunner(
            queue, packages, cases, inspector, new InMemoryMonkeyRunTracker(),
            new A5PauseResolver(), budgets, inspectorOptions,
            NullLogger<InspectorMonkeyRunner>.Instance,
            triage);

        return new Fixture(runner, queue, cases, triageStore, triageLlm, inspectorLlm);
    }
}
