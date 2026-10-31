using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring;
using JourneyMonitor.Core.Monitoring.Inspector;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Infrastructure.Monitoring;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Tests.Monitoring;

public class InspectorMonkeyServiceTests
{
    private const string DebateAnswer = """{"status":"answer","hypothesis":"vendor timeouts","supportingEvidence":["op-aaa"],"confidence":0.8,"unknowns":[]}""";
    private const string NeedsHelp = """{"status":"needs_help","targetAgent":"LogsAgent","question":"what is this club's baseline?"}""";

    // ---------------------------------------------------------------------------------------------
    // Trap 1: the retry vs the unique index on InvestigationPackageId
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task ResumesACaseThatAlreadyExistsForThePackageInsteadOfOpeningASecondOne()
    {
        // A first attempt that crashed leaves a row behind. Without the lookup-before-insert the
        // retry dies on the unique index, burns an attempt, and the item ends up abandoned having
        // never been investigated.
        var package = InspectorFixtures.Package();
        var cases = new InMemoryForensicCaseRepository();
        var crashed = await cases.AddAsync(new ForensicCase
        {
            InvestigationPackageId = package.Id,
            CaseId = package.CaseId,
            EntityKey = package.EntityKey,
            Vertical = package.Vertical,
            Stage = package.Stage,
            Status = ForensicCaseStatus.Running,
            Phase = ForensicCasePhase.Agents,
            LlmCalls = 4,
            TokensIn = 900,
        });

        var result = await BuildService(HappyClient(), cases).InvestigateAsync(package, CancellationToken.None);

        Assert.Equal(crashed.Id, result.Id);
        Assert.NotEqual(ForensicCaseStatus.Failed, result.Status);
        Assert.Equal(1, (await cases.GetStatsAsync()).Investigations);
        // What the crashed attempt really spent stays on the case; a retry that reports itself as
        // cheaper than it was defeats the only cost figure there is.
        Assert.True(result.LlmCalls > 4);
        Assert.True(result.TokensIn > 900);
    }

    [Fact]
    public async Task ResumesTheCaseWhenTheRowAppearsBetweenTheLookupAndTheInsert()
    {
        // The other half of the same trap: the index can also fire when the lookup legitimately
        // found nothing a moment earlier.
        var package = InspectorFixtures.Package();
        var cases = new RacingForensicCaseRepository(InspectorFixtures.Case(package));

        var result = await BuildService(HappyClient(), cases).InvestigateAsync(package, CancellationToken.None);

        Assert.Equal(1, cases.AddAttempts);
        Assert.Equal(2, cases.Lookups);
        Assert.NotEqual(ForensicCaseStatus.Failed, result.Status);
    }

    // ---------------------------------------------------------------------------------------------
    // Agents without a source
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnAgentWithoutASourceReportsNoSourceAndSpendsNothing()
    {
        // A model asked to investigate without a source can only answer "nothing found", which
        // reads as evidence of absence - and it would be charged for saying it.
        var llm = new ScriptedToolCallingLlmClient((model, _) => InspectorFixtures.Answer(model, DebateAnswer));
        var planner = BuildPlanner(llm, [new CodeAgentSpecialist(), new SourcelessDocsAgentSpecialist("no document store is wired up.")]);

        var blackboard = await planner.GatherAsync(InspectorFixtures.Context(InspectorFixtures.Package()));

        Assert.Equal(0, llm.Calls);
        Assert.Equal(2, blackboard.Count);
        Assert.All(blackboard, e => Assert.Equal(AgentSourceStatus.NoSource, e.Status));
        Assert.All(blackboard, e => Assert.NotEqual("", e.Summary));
        // The scope description has to say so too, or the reader cannot tell a silent agent from an absent one.
        Assert.Contains("NO SOURCE", planner.DescribeScope());
    }

    [Fact]
    public async Task ASynthesisCutOffAtTheTokenCeilingSaysSoRatherThanJustNotValidJson()
    {
        // Found in production the day the CodeAgent and DocsAgent were wired up: the blackboard
        // grew by ten documents and fourteen source locations, the synthesis hit the output ceiling
        // exactly and was cut mid-sentence, and thirteen LLM calls landed as "inconclusive".
        // Truncated and malformed look identical from the parser, but the fix is opposite: one
        // needs a bigger budget, the other needs a better prompt.
        var truncated = new LlmCompletion(
            """{"conclusion":"root_cause","summary":"vendor timeouts","runbook":[{"step":1,"action":"pull the remaining""",
            [], "length", TokensIn: 9000, TokensOut: 1500, "planner", Error: null);
        var llm = new ScriptedToolCallingLlmClient((model, _) =>
            model == "planner" ? truncated : InspectorFixtures.Answer(model, DebateAnswer));

        var result = await BuildService(llm, new InMemoryForensicCaseRepository())
            .InvestigateAsync(InspectorFixtures.Package(), CancellationToken.None);

        Assert.Equal(ForensicCaseStatus.Failed, result.Status);
        Assert.Contains("cut off", result.ErrorMessage);
        Assert.Contains("MaxSynthesisTokens", result.ErrorMessage);
        // The investigation itself ran; saying only "not valid JSON" would send the reader to the prompt.
        Assert.Contains("only the write-up did not fit", result.ErrorMessage);
    }

    // ---------------------------------------------------------------------------------------------
    // Degraded modes
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task ASingleConfiguredModelProducesAResultWithoutDebatingWithItself()
    {
        var llm = HappyClient();
        var options = DefaultOptions();
        options.DebateModels = "only-model";
        options.DebateRounds = 2;

        var result = await BuildService(llm, new InMemoryForensicCaseRepository(), options)
            .InvestigateAsync(InspectorFixtures.Package(), CancellationToken.None);

        Assert.NotEqual(ForensicCaseStatus.Failed, result.Status);
        Assert.NotNull(result.Decision);
        // Round 0 for the single model plus the Planner's synthesis - the two debate rounds are
        // skipped rather than run against an empty peer list.
        Assert.Equal(2, llm.Calls);
        Assert.Equal(new[] { "only-model", "planner" }, llm.ModelsCalled);
    }

    [Fact]
    public async Task AModelThatOnlyEverAsksForHelpStopsAtTheHopLimitInsteadOfLooping()
    {
        var logsAgent = new FakeSpecialist(InspectorAgent.Logs, hasSource: true, answer: "the baseline is 358 median / 2243 P95");
        var llm = new ScriptedToolCallingLlmClient((model, _) =>
            InspectorFixtures.Answer(model, model == "planner" ? InspectorFixtures.GoodSynthesis : NeedsHelp));
        var options = DefaultOptions();
        options.DebateModels = "model-a";
        options.MaxHelpHops = 2;

        var planner = BuildPlanner(llm, [logsAgent], options);
        var proposals = await planner.DebateAsync(InspectorFixtures.Context(InspectorFixtures.Package()));

        // Hops 0 and 1 are routed; the last hop is told help has run out and still asks, so the
        // model is excluded with a visible reason rather than left to loop.
        Assert.Equal(3, llm.Calls);
        Assert.Equal(2, logsAgent.AnswerCalls);
        var proposal = Assert.Single(proposals);
        Assert.False(proposal.Available);
        Assert.Contains("help hops", proposal.Reason);
    }

    // ---------------------------------------------------------------------------------------------
    // Trap 4: a case must never end ambiguous
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheLogsAgentStopsCallingToolsAtTheConfiguredRoundCapAndStillAnswers()
    {
        // Without the cap a model that keeps asking for one more query spends the budget on the
        // same case. The last round deliberately offers no tools so there is always an answer.
        var tool = new CountingAgentTool();
        var llm = new ScriptedToolCallingLlmClient((model, messages) =>
            messages.Count(m => m.Role == LlmRole.Tool) < 2
                ? new LlmCompletion("", [new LlmToolCall($"call-{Guid.NewGuid():N}", "fake_tool", "{}")], "tool_calls", 100, 20, model, null)
                : InspectorFixtures.Answer(model, """{"summary":"the vendor times out","findings":[{"observation":"timeouts","measurement":"1683"}],"artifacts":[],"unknowns":[]}"""));

        var options = DefaultOptions();
        options.MaxToolRounds = 2;
        var recorder = new InspectorRecorder(new InMemoryAgentTranscriptRepository(), NullLogger<InspectorRecorder>.Instance);
        var specialist = new LogsAgentSpecialist([tool], llm, recorder, Options.Create(options), NullLogger<LogsAgentSpecialist>.Instance);

        var entry = await specialist.GatherAsync(InspectorFixtures.Context(InspectorFixtures.Package()));

        Assert.Equal(2, tool.Executions);
        Assert.Equal(3, llm.Calls);
        Assert.Equal(AgentSourceStatus.Contributed, entry.Status);
        Assert.Equal("the vendor times out", entry.Summary);
    }

    [Fact]
    public async Task AnUnreachableProviderClosesTheCaseAsFailedRatherThanLeavingItRunningForever()
    {
        var cases = new InMemoryForensicCaseRepository();

        var result = await BuildService(ScriptedToolCallingLlmClient.AlwaysUnavailable(), cases)
            .InvestigateAsync(InspectorFixtures.Package(), CancellationToken.None);

        Assert.Equal(ForensicCaseStatus.Failed, result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
        Assert.NotNull(result.CompletedAt);
        Assert.Null(result.Phase);
        // A provider that never answered cost nothing, and must not be counted as if it had.
        Assert.Equal(0, result.LlmCalls);
    }

    // ---------------------------------------------------------------------------------------------
    // Trap 5: phases
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task EveryPhaseIsPublishedOnceInOrderAndNeverMovesBackwards()
    {
        // The Scout's cycle used to die on "triage" because the later phases were only reported
        // inside a loop the normal path skipped. Same failure mode, same guard.
        var cases = new PhaseRecordingForensicCaseRepository(new InMemoryForensicCaseRepository());

        var result = await BuildService(HappyClient(), cases).InvestigateAsync(InspectorFixtures.Package(), CancellationToken.None);

        Assert.Equal(ForensicCasePhase.All, cases.Phases);
        var indexes = cases.Phases.Select(ForensicCasePhase.IndexOf).ToList();
        Assert.Equal(indexes.OrderBy(i => i), indexes);
        Assert.Equal(indexes.Distinct(), indexes);
        // Null once finished, so an in-flight case stays distinguishable from a finished one.
        Assert.Null(result.Phase);
    }

    [Fact]
    public async Task TheCaseCarriesTheRealTokenUsageOfTheCallsItMade()
    {
        var result = await BuildService(HappyClient(), new InMemoryForensicCaseRepository())
            .InvestigateAsync(InspectorFixtures.Package(), CancellationToken.None);

        // Three debate models plus the Planner, each scripted at 100 in / 20 out.
        Assert.Equal(4, result.LlmCalls);
        Assert.Equal(400, result.TokensIn);
        Assert.Equal(80, result.TokensOut);
    }

    [Fact]
    public async Task AFinishedCaseNotifiesWithTheForensicCaseSourceType()
    {
        var notifications = new CountingNotificationService();

        await BuildService(HappyClient(), new InMemoryForensicCaseRepository(), notifications: notifications)
            .InvestigateAsync(InspectorFixtures.Package(), CancellationToken.None);

        var request = Assert.Single(notifications.Requests);
        Assert.Equal(NotificationSourceType.ForensicCase, request.SourceType);
        // Nothing this agent produces pages anyone yet.
        Assert.NotEqual(NotificationSeverity.Critical, request.Severity);
    }

    // ---------------------------------------------------------------------------------------------
    // Hand-off to the RCA Agent
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnEscalatedCaseWithARootCauseHandsItselfToTheRcaAgent()
    {
        var agent = new CountingRcaAgent();
        var reports = new InMemoryRcaReportRepository();
        var service = BuildService(
            SynthesisClient(InspectorFixtures.EscalatingSynthesisWithARootCause),
            new InMemoryForensicCaseRepository(),
            rcaComposer: InspectorFixtures.RcaComposer(agent, reports, options: new RcaOptions()));

        var result = await service.InvestigateAsync(InspectorFixtures.Package(), CancellationToken.None);

        Assert.Equal(ForensicCaseStatus.Escalated, result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.ProbableRootCause));
        Assert.Equal([result.Id], agent.ComposedFor);
        Assert.NotNull(await reports.GetByForensicCaseIdAsync(result.Id, CancellationToken.None));
    }

    [Fact]
    public async Task AnEscalatedCaseThatNamedNothingDoesNotReachTheRcaAgent()
    {
        var agent = new CountingRcaAgent();
        var service = BuildService(
            SynthesisClient(InspectorFixtures.EscalatingSynthesisWithoutARootCause),
            new InMemoryForensicCaseRepository(),
            rcaComposer: InspectorFixtures.RcaComposer(agent, options: new RcaOptions()));

        var result = await service.InvestigateAsync(InspectorFixtures.Package(), CancellationToken.None);

        Assert.Equal(ForensicCaseStatus.Escalated, result.Status);
        Assert.Empty(agent.ComposedFor);
    }

    [Fact]
    public async Task AnRcaFailureStillLeavesTheInvestigationFinished()
    {
        // The RCA runs after the case is persisted on purpose. If composing could undo the case,
        // a provider outage would erase an investigation that cost ~25 completions.
        var cases = new InMemoryForensicCaseRepository();
        var service = BuildService(
            SynthesisClient(InspectorFixtures.EscalatingSynthesisWithARootCause),
            cases,
            rcaComposer: InspectorFixtures.RcaComposer(new ExplodingRcaAgent(), options: new RcaOptions()));

        var result = await service.InvestigateAsync(InspectorFixtures.Package(), CancellationToken.None);

        Assert.Equal(ForensicCaseStatus.Escalated, result.Status);
        Assert.NotNull(result.CompletedAt);
        Assert.Null(result.Phase);
        Assert.Null((await cases.GetByIdAsync(result.Id))!.ErrorMessage);
    }

    private sealed class ExplodingRcaAgent : IRcaAgent
    {
        public Task<RcaReport> ComposeAsync(ForensicCase forensicCase, CancellationToken ct = default) =>
            throw new InvalidOperationException("the provider refused the request");
    }

    // ---------------------------------------------------------------------------------------------
    // Fixtures
    // ---------------------------------------------------------------------------------------------

    private static InspectorOptions DefaultOptions() => new()
    {
        DebateModels = "model-a,model-b,model-c",
        PlannerModel = "planner",
        DebateRounds = 0,
        MaxHelpHops = 2,
    };

    private static ScriptedToolCallingLlmClient HappyClient() =>
        new((model, _) => InspectorFixtures.Answer(model, model == "planner" ? InspectorFixtures.GoodSynthesis : DebateAnswer));

    private static ScriptedToolCallingLlmClient SynthesisClient(string synthesis) =>
        new((model, _) => InspectorFixtures.Answer(model, model == "planner" ? synthesis : DebateAnswer));
    private static InspectorPlanner BuildPlanner(
        ScriptedToolCallingLlmClient llm,
        IReadOnlyList<IInspectorSpecialist> specialists,
        InspectorOptions? options = null)
    {
        var resolved = Options.Create(options ?? DefaultOptions());
        var recorder = new InspectorRecorder(new InMemoryAgentTranscriptRepository(), NullLogger<InspectorRecorder>.Instance);
        return new InspectorPlanner(specialists, llm, recorder, InspectorFixtures.BudgetResolver(options ?? DefaultOptions()), resolved, NullLogger<InspectorPlanner>.Instance);
    }

    private static InspectorMonkeyService BuildService(
        ScriptedToolCallingLlmClient llm,
        IForensicCaseRepository cases,
        InspectorOptions? options = null,
        INotificationService? notifications = null,
        RcaAutoComposer? rcaComposer = null)
    {
        var resolved = Options.Create(options ?? DefaultOptions());
        var transcripts = new InMemoryAgentTranscriptRepository();
        var recorder = new InspectorRecorder(transcripts, NullLogger<InspectorRecorder>.Instance);
        var planner = new InspectorPlanner([], llm, recorder, InspectorFixtures.BudgetResolver(options ?? DefaultOptions()), resolved, NullLogger<InspectorPlanner>.Instance);

        return new InspectorMonkeyService(
            cases,
            new FakeClubIdentityResolver(),
            planner,
            new InspectorGroundingService(transcripts, recorder, NullLogger<InspectorGroundingService>.Instance),
            new InspectorVerdictService(resolved),
            new RedAlertPolicy(Options.Create(new RedAlertOptions())),
            InspectorFixtures.OwnerResolver(),
            notifications ?? new CountingNotificationService(),
            rcaComposer ?? InspectorFixtures.RcaComposer(),
            recorder,
            resolved,
            NullLogger<InspectorMonkeyService>.Instance);
    }
}
