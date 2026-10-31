using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring.Inspector;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Infrastructure.Monitoring;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Tests.Monitoring;

/// <summary>
/// The debate is 64% of this system's token spend and rounds 1..N are 43% on their own, so every
/// branch that decides whether they run is worth a test that counts calls.
/// </summary>
public class InspectorDebateBudgetTests
{
    private const string DebateAnswer = """{"status":"answer","hypothesis":"vendor timeouts","supportingEvidence":["op-aaa"],"confidence":0.8,"unknowns":[]}""";

    private static InspectorOptions Options3Models(int? skipBelow = null, int rounds = 2) => new()
    {
        DebateModels = "model-a,model-b,model-c",
        PlannerModel = "planner",
        DebateRounds = rounds,
        MaxHelpHops = 0,
        SkipDebateWhenContributorsBelow = skipBelow,
    };

    private static InspectorPlanner BuildPlanner(ScriptedToolCallingLlmClient llm, InspectorOptions options)
    {
        var resolved = Microsoft.Extensions.Options.Options.Create(options);
        var recorder = new InspectorRecorder(new InMemoryAgentTranscriptRepository(), NullLogger<InspectorRecorder>.Instance);
        return new InspectorPlanner([], llm, recorder, InspectorFixtures.BudgetResolver(options), resolved, NullLogger<InspectorPlanner>.Instance);
    }

    private static InspectorContext ContextWith(int contributing, int silent)
    {
        var entries = new List<BlackboardEntry>();
        for (var i = 0; i < contributing; i++)
        {
            entries.Add(new BlackboardEntry($"Agent{i}", AgentSourceStatus.Contributed, "found something", null));
        }
        for (var i = 0; i < silent; i++)
        {
            entries.Add(BlackboardEntry.NoSource($"Silent{i}", "no source is wired up"));
        }
        return InspectorFixtures.Context(InspectorFixtures.Package(), [.. entries]);
    }

    private static ScriptedToolCallingLlmClient AnsweringClient() =>
        new((model, _) => InspectorFixtures.Answer(model, DebateAnswer));

    [Fact]
    public async Task ByDefaultEveryRoundRunsSoNothingChangesForAnExistingDeployment()
    {
        var llm = AnsweringClient();

        await BuildPlanner(llm, Options3Models()).DebateAsync(ContextWith(contributing: 1, silent: 4));

        // 3 models x (round 0 + 2 debate rounds).
        Assert.Equal(9, llm.Calls);
    }

    [Fact]
    public async Task ADebateOverAnEmptyBlackboardStopsAfterTheIndependentRound()
    {
        var llm = AnsweringClient();

        var proposals = await BuildPlanner(llm, Options3Models(skipBelow: 3))
            .DebateAsync(ContextWith(contributing: 1, silent: 4));

        // Only round 0: three models, one call each. Six completions saved.
        Assert.Equal(3, llm.Calls);
        // The synthesis still gets one independent reading per model - the point is to drop the
        // rounds where they read each other, not to leave the Planner with nothing.
        Assert.Equal(3, proposals.Count);
    }

    [Fact]
    public async Task EnoughEvidenceStillEarnsTheFullDebate()
    {
        var llm = AnsweringClient();

        await BuildPlanner(llm, Options3Models(skipBelow: 3)).DebateAsync(ContextWith(contributing: 3, silent: 2));

        Assert.Equal(9, llm.Calls);
    }

    [Fact]
    public async Task OnlyContributedCountsTowardsTheFloor()
    {
        // "no_source" and "empty" are not evidence. Counting them would make the floor unreachable
        // in exactly the cases it exists for.
        var llm = AnsweringClient();

        await BuildPlanner(llm, Options3Models(skipBelow: 2)).DebateAsync(ContextWith(contributing: 1, silent: 5));

        Assert.Equal(3, llm.Calls);
    }

    [Fact]
    public async Task SettingTheRoundsToZeroIsTheCheapestHonestDebate()
    {
        var llm = AnsweringClient();

        await BuildPlanner(llm, Options3Models(rounds: 0)).DebateAsync(ContextWith(contributing: 4, silent: 1));

        Assert.Equal(3, llm.Calls);
    }

    [Fact]
    public async Task ASingleModelRosterNeverDebatesWithItself()
    {
        var llm = AnsweringClient();
        var options = Options3Models();
        options.DebateModels = "model-a";

        await BuildPlanner(llm, options).DebateAsync(ContextWith(contributing: 4, silent: 1));

        Assert.Equal(1, llm.Calls);
    }
}
