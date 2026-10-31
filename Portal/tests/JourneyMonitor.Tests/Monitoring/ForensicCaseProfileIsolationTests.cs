using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring;
using JourneyMonitor.Core.Monitoring.Inspector;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Infrastructure.Monitoring;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Tests.Monitoring;

/// <summary>
/// One deployment now serves several companies, and a precedent is a real past incident of a real
/// customer. These tests are about the boundary that keeps company B out of company A's RCA
/// documents - a leak here is a data breach, not a relevance problem.
/// </summary>
public class ForensicCaseProfileIsolationTests
{
    private const string Arrivia = "arrivia";
    private const string Demo = "demo";

    // ---------------------------------------------------------------------------------------------
    // Attribution: a case is an investigation OF a package, so the company is copied, not resolved
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task ACaseInheritsTheCompanyOfThePackageItWasOpenedFrom()
    {
        var package = InspectorFixtures.Package();
        var cases = new InMemoryForensicCaseRepository();

        var result = await BuildService(cases).InvestigateAsync(package, CancellationToken.None);

        Assert.Equal(package.ProfileKey, result.ProfileKey);
        Assert.Equal(package.ProfileKey, (await cases.GetByIdAsync(result.Id))!.ProfileKey);
    }

    // ---------------------------------------------------------------------------------------------
    // Reading cases
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task ReadingCasesScopedToACompanyReturnsOnlyThatCompanys()
    {
        var cases = await TwoCompaniesAsync();

        var forDemo = await cases.GetRecentAsync(50, profileKey: Demo);

        Assert.Equal(Demo, Assert.Single(forDemo).ProfileKey);
    }

    [Fact]
    public async Task ReadingCasesWithNoCompanyFilterStillReturnsEveryCompanys()
    {
        // The pre-multi-company behaviour, kept on purpose: the portal's own unscoped views and
        // the deployment-wide counters would otherwise silently start reporting one company.
        var cases = await TwoCompaniesAsync();

        Assert.Equal(2, (await cases.GetRecentAsync(50)).Count);
        Assert.Equal(2, (await cases.GetStatsAsync()).Investigations);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ABlankCompanyFilterMeansAbsentNotMatchNothing(string blank)
    {
        var cases = await TwoCompaniesAsync();

        Assert.Equal(2, (await cases.GetRecentAsync(50, profileKey: blank)).Count);
    }

    [Fact]
    public async Task StatsScopedToACompanyCountOnlyThatCompanysCases()
    {
        var cases = await TwoCompaniesAsync();

        Assert.Equal(1, (await cases.GetStatsAsync(Arrivia)).Investigations);
    }

    // ---------------------------------------------------------------------------------------------
    // The corpus boundary
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task RetrievalNeverReturnsAnotherCompanysPrecedentsButDoesReturnTheSharedOnes()
    {
        // The test this whole change exists for. "" is the shared corpus; a concrete key is private
        // to that company. The in-memory retriever filters for real precisely so this cannot pass
        // over a retriever that forgot to.
        var retriever = new InMemoryKnowledgeRetriever(new FakeLlmClient("unused", embedding: null));
        await retriever.IndexAsync(Chunk("arrivia timeout precedent"), CancellationToken.None, Arrivia);
        await retriever.IndexAsync(Chunk("demo timeout precedent"), CancellationToken.None, Demo);
        await retriever.IndexAsync(Chunk("shared timeout precedent"), CancellationToken.None, profileKey: "");

        var hits = await retriever.RetrieveAsync("timeout", topK: 10, CancellationToken.None, Demo);

        Assert.Equal(2, hits.Count);
        Assert.Contains(hits, h => h.Content.StartsWith("demo", StringComparison.Ordinal));
        Assert.Contains(hits, h => h.Content.StartsWith("shared", StringComparison.Ordinal));
        Assert.DoesNotContain(hits, h => h.Content.StartsWith("arrivia", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnUnscopedRetrievalStillSeesTheWholeCorpus()
    {
        // Null is "no filter", which is what the callers that are not per-company (the portal's
        // knowledge search) mean - and what makes this change additive for them.
        var retriever = new InMemoryKnowledgeRetriever(new FakeLlmClient("unused", embedding: null));
        await retriever.IndexAsync(Chunk("arrivia timeout precedent"), CancellationToken.None, Arrivia);
        await retriever.IndexAsync(Chunk("demo timeout precedent"), CancellationToken.None, Demo);

        var hits = await retriever.RetrieveAsync("timeout", topK: 10, CancellationToken.None);

        Assert.Equal(2, hits.Count);
    }

    [Fact]
    public async Task AChunkIndexedWithoutACompanyLandsInTheSharedCorpus()
    {
        var retriever = new InMemoryKnowledgeRetriever(new FakeLlmClient("unused", embedding: null));
        await retriever.IndexAsync(Chunk("unscoped timeout precedent"), CancellationToken.None);

        Assert.Single(await retriever.RetrieveAsync("timeout", topK: 10, CancellationToken.None, Demo));
    }

    // ---------------------------------------------------------------------------------------------
    // The RCA, which is where the precedents are actually read
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheRcaOfOneCompanysCaseAsksOnlyForThatCompanysPrecedents()
    {
        var retriever = new ProfileCapturingKnowledgeRetriever();

        await BuildRca(retriever).ComposeAsync(Case(Arrivia), CancellationToken.None);

        Assert.Equal(Arrivia, Assert.Single(retriever.RequestedProfileKeys));
    }

    [Fact]
    public async Task AnUnattributedCaseRetrievesUnscopedAndSaysSoOutLoud()
    {
        // Silence is the dangerous failure mode here: an unscoped lookup looks exactly like a
        // scoped one from the outside, so the warning is the only thing that tells an operator the
        // precedents behind this RCA were not narrowed to anybody.
        var retriever = new ProfileCapturingKnowledgeRetriever();
        var logger = new RecordingLogger<RcaAgentService>();

        await BuildRca(retriever, logger).ComposeAsync(Case(profileKey: "  "), CancellationToken.None);

        Assert.Null(Assert.Single(retriever.RequestedProfileKeys));
        Assert.Contains(logger.Messages, m => m.Contains("no company attribution", StringComparison.OrdinalIgnoreCase));
    }

    // ---------------------------------------------------------------------------------------------
    // Fixtures
    // ---------------------------------------------------------------------------------------------

    private static KnowledgeChunk Chunk(string content) => new()
    {
        SourceType = KnowledgeSourceType.RcaDocument,
        Content = content,
        Embedding = [],
        EmbeddingModel = KnowledgeChunk.NoEmbeddingModel,
    };

    private static ForensicCase Case(string profileKey) => new()
    {
        InvestigationPackageId = Guid.NewGuid(),
        ProfileKey = profileKey,
        CaseId = "case-1",
        EntityKey = "host:booking.example.com",
        Vertical = "Hotels",
        Stage = "07 Booking",
        Verdict = "A single search request took ~62s.",
        ProbableRootCause = "Vendor search API stopped responding",
        CompletedAt = DateTimeOffset.UtcNow,
    };

    private static async Task<InMemoryForensicCaseRepository> TwoCompaniesAsync()
    {
        var cases = new InMemoryForensicCaseRepository();
        await cases.AddAsync(Case(Arrivia));
        await cases.AddAsync(Case(Demo));
        return cases;
    }

    private static RcaAgentService BuildRca(IKnowledgeRetriever retriever, ILogger<RcaAgentService>? logger = null) =>
        new(new FakeLlmClient("""{"sections":[],"fiveWhys":[],"requiresHuman":[]}"""),
            retriever,
            Options.Create(new RcaOptions()),
            logger ?? NullLogger<RcaAgentService>.Instance);

    private static InspectorMonkeyService BuildService(IForensicCaseRepository cases)
    {
        var options = Options.Create(new InspectorOptions
        {
            DebateModels = "model-a",
            PlannerModel = "planner",
            DebateRounds = 0,
            MaxHelpHops = 1,
        });
        var llm = new ScriptedToolCallingLlmClient((model, _) =>
            InspectorFixtures.Answer(model, model == "planner" ? InspectorFixtures.GoodSynthesis : """{"status":"answer","hypothesis":"vendor timeouts","supportingEvidence":["op-aaa"],"confidence":0.8,"unknowns":[]}"""));
        var transcripts = new InMemoryAgentTranscriptRepository();
        var recorder = new InspectorRecorder(transcripts, NullLogger<InspectorRecorder>.Instance);

        return new InspectorMonkeyService(
            cases,
            new FakeClubIdentityResolver(),
            new InspectorPlanner([], llm, recorder, InspectorFixtures.BudgetResolver(options.Value), options, NullLogger<InspectorPlanner>.Instance),
            new InspectorGroundingService(transcripts, recorder, NullLogger<InspectorGroundingService>.Instance),
            new InspectorVerdictService(options),
            new RedAlertPolicy(Options.Create(new RedAlertOptions())),
            InspectorFixtures.OwnerResolver(),
            new CountingNotificationService(),
            InspectorFixtures.RcaComposer(),
            recorder,
            options,
            NullLogger<InspectorMonkeyService>.Instance);
    }

    /// <summary>Records the scope it was asked for: the assertion is about what the RCA requested, not about what a store happened to hold.</summary>
    private sealed class ProfileCapturingKnowledgeRetriever : IKnowledgeRetriever
    {
        public List<string?> RequestedProfileKeys { get; } = [];

        public Task<IReadOnlyList<KnowledgeChunk>> RetrieveAsync(string query, int topK, CancellationToken ct = default, string? profileKey = null)
        {
            RequestedProfileKeys.Add(profileKey);
            return Task.FromResult<IReadOnlyList<KnowledgeChunk>>([]);
        }

        public Task IndexAsync(KnowledgeChunk chunk, CancellationToken ct = default, string? profileKey = null) => Task.CompletedTask;

        public Task<IReadOnlySet<string>> GetIndexedHashesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(StringComparer.Ordinal));

        public Task<IReadOnlyList<KnowledgeDocumentSummary>> GetIndexedDocumentsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<KnowledgeDocumentSummary>>([]);

        public Task<IReadOnlyList<KnowledgeDocumentSection>> GetDocumentSectionsAsync(string documentTitle, string? sourceType, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<KnowledgeDocumentSection>>([]);
    }
}
