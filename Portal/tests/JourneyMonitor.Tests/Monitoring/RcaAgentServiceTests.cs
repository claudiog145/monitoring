using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Tests.Company;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Tests.Monitoring;

public class RcaAgentServiceTests
{
    private const string WellFormedResponse = """
        {"sections":[
          {"title":"Incident Snapshot","statements":[
            {"text":"Booking latency on NG Air exceeded the threshold for 90 minutes.","confidence":"fact"},
            {"text":"The trigger was most likely a degraded API node.","confidence":"inference"}]},
          {"title":"Business Impact","statements":[
            {"text":"Revenue impact was likely minimal.","confidence":"fact"}]}],
         "fiveWhys":[
          {"level":1,"question":"Why were flights slow?","answer":"The API server was degraded.","confidence":"fact"},
          {"level":2,"question":"Why was it degraded?","answer":"Unknown - still under investigation.","confidence":"inference"}],
         "requiresHuman":["Business Impact"]}
        """;

    private static ForensicCase Case(int citationsVerified = 2, int citationsTotal = 2) => new()
    {
        InvestigationPackageId = Guid.NewGuid(),
        CaseId = "case-1",
        EntityKey = "host:booking.travorium.com",
        Vertical = "Hotels",
        Stage = "04 Hotel Details",
        Verdict = "A single search request took ~62s, pushing P95 over threshold.",
        ProbableRootCause = "Vendor search API stopped responding",
        Decision = "escalate",
        Confidence = 0.55,
        CitationsTotal = citationsTotal,
        CitationsVerified = citationsVerified,
        CompletedAt = DateTimeOffset.UtcNow,
    };

    private static RcaAgentService Build(ILlmClient llm, IKnowledgeRetriever? retriever = null) =>
        new(llm,
            retriever ?? new FakeKnowledgeRetriever([]),
            Options.Create(new RcaOptions()),
            NullLogger<RcaAgentService>.Instance);

    [Fact]
    public async Task ComposesTheTemplateSectionsAndTheWhysChain()
    {
        var report = await Build(new FakeLlmClient(WellFormedResponse)).ComposeAsync(Case(), CancellationToken.None);

        Assert.True(report.IsAvailable);
        Assert.Contains(report.Sections, s => s.Title == RcaTemplate.IncidentSnapshot);
        Assert.Equal(2, report.FiveWhys.Count);
        Assert.Equal(1, report.FiveWhys[0].Level);
    }

    [Fact]
    public async Task AnInvestigationThatVerifiedNothingCannotProduceASingleFact()
    {
        // The rule lives in code, not in the prompt: a prompt is not a boundary. An RCA goes to
        // leadership, sometimes to a partner, and "fact" there means somebody can quote it.
        var report = await Build(new FakeLlmClient(WellFormedResponse)).ComposeAsync(Case(citationsVerified: 0), CancellationToken.None);

        var allStatements = report.Sections.SelectMany(s => s.Statements).ToList();
        Assert.NotEmpty(allStatements);
        Assert.DoesNotContain(allStatements, s => s.Confidence == RcaConfidence.Fact);
        Assert.DoesNotContain(report.FiveWhys, w => w.Confidence == RcaConfidence.Fact);
    }

    [Fact]
    public async Task AVerifiedInvestigationKeepsItsFacts()
    {
        var report = await Build(new FakeLlmClient(WellFormedResponse)).ComposeAsync(Case(citationsVerified: 2), CancellationToken.None);

        Assert.Contains(report.Sections.SelectMany(s => s.Statements), s => s.Confidence == RcaConfidence.Fact);
    }

    [Fact]
    public async Task AFinancialClaimCanNeverBeAFactBecauseNoRevenueDataExistsAnywhere()
    {
        // Observed in a real run: asked not to, the model still labelled "No financial or booking
        // impact occurred" as fact. The prompt said otherwise and was ignored, so the rule moved
        // into code - this platform has no revenue or price column in any schema.
        var report = await Build(new FakeLlmClient(WellFormedResponse)).ComposeAsync(Case(citationsVerified: 2), CancellationToken.None);

        var financial = report.Sections
            .SelectMany(s => s.Statements)
            .Single(s => s.Text.Contains("Revenue", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(RcaConfidence.Inference, financial.Confidence);
    }

    [Fact]
    public async Task AnUnrecognisedConfidenceLabelBecomesInferenceNotFact()
    {
        const string response = """
            {"sections":[{"title":"Incident Snapshot","statements":[
              {"text":"Something happened.","confidence":"certain"},
              {"text":"Something else happened.","confidence":""}]}],
             "fiveWhys":[],"requiresHuman":[]}
            """;

        var report = await Build(new FakeLlmClient(response)).ComposeAsync(Case(), CancellationToken.None);

        Assert.All(report.Sections[0].Statements, s => Assert.Equal(RcaConfidence.Inference, s.Confidence));
    }

    [Fact]
    public async Task PrecedentsComeFromTheKnowledgeBaseNotFromTheModel()
    {
        // Same grounding rule the Inspector applies to artifacts: the model cannot introduce a
        // precedent that was never retrieved, so it cannot cite a document that does not exist.
        var retrieved = new KnowledgeChunk
        {
            SourceType = KnowledgeSourceType.RcaDocument,
            Content = "The API server entered a degraded state.",
            Embedding = [0.1f],
            EmbeddingModel = "fake",
            Domain = "Air",
            DocumentTitle = "NG Air latency",
            SectionTitle = "Root Cause",
        };

        var report = await Build(new FakeLlmClient(WellFormedResponse), new FakeKnowledgeRetriever([retrieved]))
            .ComposeAsync(Case(), CancellationToken.None);

        var precedent = Assert.Single(report.Precedents);
        Assert.Equal("NG Air latency", precedent.Document);
        Assert.Equal(KnowledgeSourceType.RcaDocument, precedent.SourceType);
    }

    [Fact]
    public async Task AnEmptyProviderResponseIsReportedRatherThanReplacedWithAPlausibleDocument()
    {
        var report = await Build(new FakeLlmClient("")).ComposeAsync(Case(), CancellationToken.None);

        Assert.False(report.IsAvailable);
        Assert.NotNull(report.Error);
        Assert.Empty(report.Sections);
    }

    [Fact]
    public async Task UnreadableJsonIsReportedWithItsReason()
    {
        var report = await Build(new FakeLlmClient("{ this is not json")).ComposeAsync(Case(), CancellationToken.None);

        Assert.False(report.IsAvailable);
        Assert.NotNull(report.Error);
    }

    [Fact]
    public async Task ProseWrappedAroundTheJsonIsToleratedBecauseModelsDoThat()
    {
        var response = $"Here is the RCA you asked for:\n```json\n{WellFormedResponse}\n```\nLet me know if you need changes.";

        var report = await Build(new FakeLlmClient(response)).ComposeAsync(Case(), CancellationToken.None);

        Assert.True(report.IsAvailable);
    }

    [Fact]
    public async Task ThePromptAddressesTheCompanyOnTheProfileRatherThanATravelPlatform()
    {
        var llm = new PromptCapturingLlmClient(WellFormedResponse);
        var service = new RcaAgentService(
            llm,
            new FakeKnowledgeRetriever([]),
            Options.Create(new RcaOptions()),
            NullLogger<RcaAgentService>.Instance,
            VocabularyProfileStub.Serving(FreightProfile()));

        await service.ComposeAsync(Case(), CancellationToken.None);

        var prompt = Assert.Single(llm.Prompts);
        Assert.Contains("for the leadership of the company described below", prompt);
        Assert.Contains("ABOUT THE COMPANY THIS PLATFORM MONITORS", prompt);
        Assert.Contains("Northwind Freight moves palletised cargo", prompt);
        Assert.Contains("\"shipper\"", prompt);
        Assert.Contains("Lane: Hotels", prompt);        // the LABEL comes from the profile; the VALUE is still the case's own
        Assert.Contains("Checkpoint: 04 Hotel Details", prompt);
        Assert.Contains("Affected shipper: host:booking.travorium.com", prompt);
        Assert.DoesNotContain("airline/hotel/cruise travel platform", prompt);

        // The labelling rules that keep an RCA honest are untouched.
        Assert.Contains("Never label something \"fact\" unless it appears in the evidence below.", prompt);
        Assert.Contains("stores NO revenue or price data anywhere", prompt);
        Assert.Contains("VERIFIED to exist in the real system", prompt);
    }

    [Fact]
    public async Task TheVerifiedCitationRuleSurvivesTheProfileDrivenPrompt()
    {
        // The one rule that lives in code rather than in the prompt must not become
        // profile-dependent: a company could otherwise be onboarded into being allowed to lie.
        var service = new RcaAgentService(
            new FakeLlmClient(WellFormedResponse),
            new FakeKnowledgeRetriever([]),
            Options.Create(new RcaOptions()),
            NullLogger<RcaAgentService>.Instance,
            VocabularyProfileStub.Serving(FreightProfile()));

        var report = await service.ComposeAsync(Case(citationsVerified: 0), CancellationToken.None);

        Assert.DoesNotContain(report.Sections.SelectMany(s => s.Statements), s => s.Confidence == RcaConfidence.Fact);
    }

    private static CompanyProfile FreightProfile() =>
        new(
            "northwind",
            "Northwind Freight",
            new CompanyLabels("Lane", "Route", "Checkpoint", "Shipper", "Ops crew"),
            [],
            [],
            new CompanyGlossary("lane", "route", "checkpoint", "shipper", "ops crew",
                "Northwind Freight moves palletised cargo between regional depots."));

    [Fact]
    public async Task SectionsNeedingAHumanAreNamedSoAGapReadsAsATaskNotAnOversight()
    {
        var report = await Build(new FakeLlmClient(WellFormedResponse)).ComposeAsync(Case(), CancellationToken.None);

        Assert.Contains(RcaTemplate.BusinessImpact, report.RequiresHuman);
    }

    [Fact]
    public async Task TheReportRecordsWhichModelAndHowManyTokensItCost()
    {
        var report = await Build(new FakeLlmClient(WellFormedResponse, modelUsed: "claude-sonnet-5")).ComposeAsync(Case(), CancellationToken.None);

        Assert.Equal("claude-sonnet-5", report.ModelUsed);
        Assert.Equal(1, report.LlmCalls);
        Assert.True(report.TokensIn > 0);
    }

    [Fact]
    public async Task AKnowledgeBaseOutageDoesNotStopTheRcaItJustMeansNoPrecedents()
    {
        var report = await Build(new FakeLlmClient(WellFormedResponse), new ThrowingKnowledgeRetriever())
            .ComposeAsync(Case(), CancellationToken.None);

        Assert.True(report.IsAvailable);
        Assert.Empty(report.Precedents);
    }

    private sealed class ThrowingKnowledgeRetriever : IKnowledgeRetriever
    {
        public Task<IReadOnlyList<KnowledgeChunk>> RetrieveAsync(string query, int topK, CancellationToken ct = default, string? profileKey = null) =>
            throw new InvalidOperationException("knowledge base unavailable");

        public Task IndexAsync(KnowledgeChunk chunk, CancellationToken ct = default, string? profileKey = null) => Task.CompletedTask;

        public Task<IReadOnlySet<string>> GetIndexedHashesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(StringComparer.Ordinal));

        public Task<IReadOnlyList<KnowledgeDocumentSummary>> GetIndexedDocumentsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<KnowledgeDocumentSummary>>([]);

        public Task<IReadOnlyList<KnowledgeDocumentSection>> GetDocumentSectionsAsync(string documentTitle, string? sourceType, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<KnowledgeDocumentSection>>([]);
    }
}
