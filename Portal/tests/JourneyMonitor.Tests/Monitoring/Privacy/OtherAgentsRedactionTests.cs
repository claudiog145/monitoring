using System.Text.Json;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring;
using JourneyMonitor.Core.Monitoring.Inspector;
using JourneyMonitor.Core.Monitoring.Privacy;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Infrastructure.Monitoring;
using JourneyMonitor.Tests.Company;
using JourneyMonitor.Tests.Spec1000.A5;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JourneyMonitor.Tests.Monitoring.Privacy;

/// <summary>AG6B: the AG6 per-profile redaction rule applied to RCA, the Scout assessor, the Doctor and Triage's persisted correlation.</summary>
public class OtherAgentsRedactionTests
{
    private const string Email = "jane.doe@example.com";
    private const string Card = "4111 1111 1111 1111";
    private const string MemberId = "MBR-998877";
    // Full card fragments, not "4111" alone: random GUIDs in the redacted text can contain "4111".
    private const string Acme = "acme";

    private const string RcaResponse = """
        {"sections":[{"title":"Incident Snapshot","statements":[{"text":"x","confidence":"inference"}]}],"fiveWhys":[],"requiresHuman":[]}
        """;

    private const string DoctorResponse = """
        {"verdict":"Configuration issue","errorCategory":"configuration","rootCauseHypothesis":"bad config","confidence":0.7,"evidenceCited":[],"proposedFix":null,"unknowns":[]}
        """;

    private const string ScoutResponse = """{"symptom":"slow","interpretation":"y","confidence":0.4,"shouldEscalate":true}""";

    [Fact]
    public async Task Rca_PromptIsRedacted()
    {
        var logger = new CollectingLogger<RcaAgentService>();
        var llm = new CapturingLlm(RcaResponse);

        await Rca(llm, Acme, new PrivacySettings(), logger).ComposeAsync(Case(Acme));

        var prompt = Assert.Single(llm.Prompts);
        Assert.DoesNotContain(Email, prompt);
        Assert.DoesNotContain("4111 1111", prompt);
        Assert.DoesNotContain("41111111", prompt);
        Assert.Contains("[REDACTED:email]", prompt);
        Assert.Contains("[REDACTED:card]", prompt);
        AssertAuditHasCountsNotValues(logger.Messages);
    }

    [Fact]
    public async Task ScoutAssessor_PromptIsRedacted()
    {
        var logger = new CollectingLogger<ScoutAssessorService>();
        var llm = new CapturingLlm(ScoutResponse);
        var assessor = Scout(llm, Acme, new PrivacySettings(), logger);

        var result = await assessor.AssessAsync(Row(), Club());

        var prompt = Assert.Single(llm.Prompts);
        Assert.DoesNotContain(Email, prompt);
        Assert.Contains("[REDACTED:email]", prompt);
        Assert.Contains("You are the Scout Monkey", prompt);
        // The call log keeps what was actually sent.
        Assert.Equal(prompt, result.Call.PromptText);
        Assert.True(result.Assessment.Available);
        AssertAuditHasCountsNotValues(logger.Messages);
    }

    [Fact]
    public async Task Doctor_PromptIsRedacted()
    {
        var logger = new CollectingLogger<DoctorAgentService>();
        var llm = new CapturingLlm(DoctorResponse, embedding: [0.1f, 0.2f]);
        var callLog = new InMemoryLlmCallLogRepository();
        var retriever = new CapturingRetriever();
        var doctor = Doctor(llm, Acme, new PrivacySettings(), callLog, logger, retriever);

        var report = await doctor.DiagnoseAsync(SensitiveFinding());

        var prompt = Assert.Single(llm.Prompts);
        Assert.DoesNotContain(Email, prompt);
        Assert.Contains("[REDACTED:email]", prompt);
        Assert.Contains("Doctor Monkey", prompt);
        var log = await callLog.GetByDoctorReportIdAsync(report.Id);
        Assert.Equal(prompt, log!.PromptText);
        var embeddingInput = Assert.Single(llm.EmbeddingInputs);
        Assert.DoesNotContain(Email, embeddingInput);
        // The stored knowledge chunk is redacted and filed under the resolved company.
        var (chunk, chunkKey) = Assert.Single(retriever.Indexed);
        Assert.DoesNotContain(Email, chunk.Content);
        Assert.Contains("[REDACTED:email]", chunk.Content);
        Assert.Equal(Acme, chunkKey);
        AssertAuditHasCountsNotValues(logger.Messages);
    }

    [Fact]
    public async Task Doctor_ProfileReadFailure_RedactsAndDoesNotLeak()
    {
        var llm = new CapturingLlm(DoctorResponse, embedding: [0.1f, 0.2f]);
        var callLog = new InMemoryLlmCallLogRepository();
        var retriever = new CapturingRetriever();
        var logger = new CollectingLogger<DoctorAgentService>();
        // The active company is unreadable; the profile store would say "incumbent" if it were consulted.
        var doctor = Doctor(llm, KpiProfiles.Default, privacy: null, callLog, logger, retriever, new ThrowingActiveCompany());

        var report = await doctor.DiagnoseAsync(SensitiveFinding());

        var prompt = Assert.Single(llm.Prompts);
        Assert.DoesNotContain(Email, prompt);
        Assert.Contains("[REDACTED:email]", prompt);
        Assert.DoesNotContain(Email, (await callLog.GetByDoctorReportIdAsync(report.Id))!.PromptText);
        Assert.DoesNotContain(Email, Assert.Single(retriever.Queries));
        // Unknown company: nothing is embedded or indexed into the shared corpus.
        Assert.Empty(llm.EmbeddingInputs);
        Assert.Empty(retriever.Indexed);
        Assert.Contains(logger.Messages, m => m.Contains("not indexed", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Messages, m => m.Contains(Email, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Doctor_SearchQueryIsRedacted()
    {
        var retriever = new CapturingRetriever();
        await Doctor(new CapturingLlm(DoctorResponse), Acme, new PrivacySettings(), new InMemoryLlmCallLogRepository(), retriever: retriever)
            .DiagnoseAsync(SensitiveFinding());

        var query = Assert.Single(retriever.Queries);
        Assert.DoesNotContain(Email, query);
        Assert.Contains("[REDACTED:email]", query);
        Assert.Contains("cars", query);
    }

    [Fact]
    public async Task Rca_SearchQueryIsRedacted()
    {
        var retriever = new CapturingRetriever();
        var rca = new RcaAgentService(
            new CapturingLlm(RcaResponse),
            retriever,
            Microsoft.Extensions.Options.Options.Create(new RcaOptions()),
            NullLogger<RcaAgentService>.Instance,
            profiles: new InMemoryCompanyProfileStore([Profile(Acme, new PrivacySettings())]),
            redactor: new EvidenceRedactor());

        await rca.ComposeAsync(Case(Acme));

        var query = Assert.Single(retriever.Queries);
        Assert.DoesNotContain(Email, query);
        Assert.DoesNotContain("4111 1111", query);
        Assert.DoesNotContain("41111111", query);
        Assert.Contains("[REDACTED:email]", query);
        Assert.Equal(Acme, Assert.Single(retriever.RetrievedKeys));
    }

    [Fact]
    public async Task Triage_PersistedCorrelation_RedactedForNonIncumbent()
    {
        var logger = new CollectingLogger<TriageMonkeyService>();
        var (store, investigationId, _) = await RunTriageAsync(Acme, new PrivacySettings(PiiDimensions: ["MemberId"]), logger);

        var saved = await store.GetAsync(investigationId);
        var correlation = saved!.Result!.Correlation;
        var app = correlation.ApplicationHealth!.Value;
        var raw = app.GetRawText();
        Assert.DoesNotContain(Email, raw);
        Assert.DoesNotContain("4111 1111", raw);
        Assert.DoesNotContain("41111111", raw);
        Assert.DoesNotContain(MemberId, raw);
        // Still structured JSON: unrelated fields survive and redacted ones are tokens.
        Assert.Equal("checkout", app.GetProperty("customDimensions").GetProperty("Stage").GetString());
        Assert.Equal(EvidenceRedactor.PiiToken, app.GetProperty("customDimensions").GetProperty("MemberId").GetString());
        Assert.Equal("[REDACTED:card]", app.GetProperty("cardNumber").GetString());
        Assert.Equal(42, app.GetProperty("requests").GetInt32());
        Assert.Contains("[REDACTED:email]", app.GetProperty("exception").GetString());
        Assert.Null(correlation.BusinessHealth);

        Assert.Contains(logger.Messages, m => m.Contains("persisted correlation redacted", StringComparison.Ordinal));
        AssertAuditHasCountsNotValues(logger.Messages);
    }

    [Fact]
    public async Task Incumbent_PromptsUnchanged()
    {
        var incumbent = KpiProfiles.Default;

        var rcaLlm = new CapturingLlm(RcaResponse);
        await Rca(rcaLlm, incumbent, privacy: null).ComposeAsync(Case(incumbent));
        Assert.Contains(Email, Assert.Single(rcaLlm.Prompts));
        Assert.Contains(Card, rcaLlm.Prompts[0]);

        // Legacy wiring (no store, no redactor, unattributed case) keeps the raw prompt too.
        var legacyRcaLlm = new CapturingLlm(RcaResponse);
        await new RcaAgentService(legacyRcaLlm, new FakeKnowledgeRetriever([]), Microsoft.Extensions.Options.Options.Create(new RcaOptions()), NullLogger<RcaAgentService>.Instance)
            .ComposeAsync(Case(""));
        Assert.Contains(Email, legacyRcaLlm.Prompts[0]);

        var scoutLlm = new CapturingLlm(ScoutResponse);
        var scoutResult = await Scout(scoutLlm, incumbent, privacy: null).AssessAsync(Row(), Club());
        Assert.Contains(Email, Assert.Single(scoutLlm.Prompts));
        Assert.Equal(scoutLlm.Prompts[0], scoutResult.Call.PromptText);

        var legacyScoutLlm = new CapturingLlm(ScoutResponse);
        await new ScoutAssessorService(legacyScoutLlm, NullLogger<ScoutAssessorService>.Instance).AssessAsync(Row(), Club());
        Assert.Contains(Email, Assert.Single(legacyScoutLlm.Prompts));

        var doctorLlm = new CapturingLlm(DoctorResponse, embedding: [0.1f]);
        await Doctor(doctorLlm, incumbent, privacy: null, new InMemoryLlmCallLogRepository()).DiagnoseAsync(SensitiveFinding());
        Assert.Contains(Email, Assert.Single(doctorLlm.Prompts));
        Assert.Contains(Email, Assert.Single(doctorLlm.EmbeddingInputs));

        // No active company: the configured default (the incumbent) governs.
        var noActiveLlm = new CapturingLlm(DoctorResponse);
        await new DoctorAgentService(
            new HeuristicErrorClassifier(), new FakeKnowledgeRetriever([]), noActiveLlm, new InMemoryDoctorReportsRepository(),
            new InMemoryLlmCallLogRepository(), new InMemoryMonkeyRunTracker(), NullLogger<DoctorAgentService>.Instance)
            .DiagnoseAsync(SensitiveFinding());
        Assert.Contains(Email, Assert.Single(noActiveLlm.Prompts));

        var (store, investigationId, triageLlm) = await RunTriageAsync(incumbent, privacy: null);
        Assert.Contains(Email, Assert.Single(triageLlm.Prompts));
        var app = (await store.GetAsync(investigationId))!.Result!.Correlation.ApplicationHealth!.Value;
        Assert.Equal(SensitiveEvidenceJson().GetRawText(), app.GetRawText());
    }

    [Fact]
    public async Task AllowRawEvidence_KeepsPromptsAndCorrelationRaw()
    {
        var allow = new PrivacySettings(AllowRawEvidence: true);

        var rcaLlm = new CapturingLlm(RcaResponse);
        await Rca(rcaLlm, Acme, allow).ComposeAsync(Case(Acme));
        Assert.Contains(Email, Assert.Single(rcaLlm.Prompts));

        var (store, investigationId, _) = await RunTriageAsync(Acme, allow);
        var app = (await store.GetAsync(investigationId))!.Result!.Correlation.ApplicationHealth!.Value;
        Assert.Equal(SensitiveEvidenceJson().GetRawText(), app.GetRawText());
    }

    [Fact]
    public async Task ScoutAssessor_FailedCompanyResolution_RedactsClosed()
    {
        var llm = new CapturingLlm(ScoutResponse);
        var assessor = new ScoutAssessorService(
            llm,
            NullLogger<ScoutAssessorService>.Instance,
            VocabularyProfileStub.Failing(new InvalidOperationException("profile store down")),
            new InMemoryCompanyProfileStore([]),
            new EvidenceRedactor());

        await assessor.AssessAsync(Row(), Club());

        Assert.DoesNotContain(Email, Assert.Single(llm.Prompts));
    }

    [Fact]
    public void RedactJson_KeepsJsonValidAndCountsOnly()
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var profile = Profile(Acme, new PrivacySettings(PiiDimensions: ["MemberId"]));

        var redacted = PromptPrivacy.RedactJson(new EvidenceRedactor(), SensitiveEvidenceJson(), profile, counts);

        Assert.Equal(JsonValueKind.Object, redacted.ValueKind);
        Assert.DoesNotContain(MemberId, redacted.GetRawText());
        Assert.Equal(1, counts["pii:MemberId"]);
        Assert.True(counts[EvidenceRedactor.Keys.Card] >= 2);
        Assert.DoesNotContain(counts.Keys, k => k.Contains(Email, StringComparison.Ordinal) || k.Contains(MemberId, StringComparison.Ordinal));
        Assert.Equal("[[1,true,null]]", PromptPrivacy.RedactJson(new EvidenceRedactor(), JsonDocument.Parse("[[1,true,null]]").RootElement, profile, counts).GetRawText());
    }

    private static void AssertAuditHasCountsNotValues(IReadOnlyList<string> messages)
    {
        var all = string.Join("\n", messages);
        Assert.Contains("email=", all, StringComparison.Ordinal);
        Assert.DoesNotContain(Email, all);
        Assert.DoesNotContain("4111 1111", all);
        Assert.DoesNotContain("41111111", all);
        Assert.DoesNotContain(MemberId, all);
    }

    private static RcaAgentService Rca(ILlmClient llm, string profileKey, PrivacySettings? privacy, ILogger<RcaAgentService>? logger = null) =>
        new(llm,
            new FakeKnowledgeRetriever([]),
            Microsoft.Extensions.Options.Options.Create(new RcaOptions()),
            logger ?? NullLogger<RcaAgentService>.Instance,
            companyAdapters: null,
            profiles: new InMemoryCompanyProfileStore([Profile(profileKey, privacy)]),
            redactor: new EvidenceRedactor());

    private static ScoutAssessorService Scout(ILlmClient llm, string profileKey, PrivacySettings? privacy, ILogger<ScoutAssessorService>? logger = null)
    {
        var profile = Profile(profileKey, privacy);
        return new ScoutAssessorService(
            llm,
            logger ?? NullLogger<ScoutAssessorService>.Instance,
            VocabularyProfileStub.Serving(profile),
            new InMemoryCompanyProfileStore([profile]),
            new EvidenceRedactor());
    }

    private static DoctorAgentService Doctor(
        ILlmClient llm, string profileKey, PrivacySettings? privacy, ILlmCallLogRepository callLog, ILogger<DoctorAgentService>? logger = null,
        IKnowledgeRetriever? retriever = null, IActiveCompanyProfileStore? activeCompany = null) =>
        new(new HeuristicErrorClassifier(),
            retriever ?? new FakeKnowledgeRetriever([]),
            llm,
            new InMemoryDoctorReportsRepository(),
            callLog,
            new InMemoryMonkeyRunTracker(),
            logger ?? NullLogger<DoctorAgentService>.Instance,
            activeCompany ?? new FixedActiveCompany(profileKey),
            new InMemoryCompanyProfileStore([Profile(profileKey, privacy)]),
            new EvidenceRedactor());

    private static async Task<(A5InMemoryTriageStore Store, Guid InvestigationId, A5FakeLlmClient Llm)> RunTriageAsync(
        string profileKey, PrivacySettings? privacy, ILogger<TriageMonkeyService>? logger = null)
    {
        var store = new A5InMemoryTriageStore();
        var signal = A5TriageFixtures.Signal() with { ProfileKey = profileKey, Detection = $"Latency doubled for {Email}" };
        var investigation = A5TriageFixtures.Queued(signal: signal);
        await store.CreateAsync(investigation);
        var llm = A5FakeLlmClient.Returning(A5TriageFixtures.AmberJson);

        var service = new TriageMonkeyService(
            store,
            [new SensitiveEvidenceProvider(TriageDimensions.ApplicationHealth)],
            llm,
            new A5PauseResolver(),
            new A5BudgetResolver(),
            new A5RecordingNotificationService(),
            Microsoft.Extensions.Options.Options.Create(new TriageOptions { Mode = TriageMode.Shadow }),
            new A5FixedTimeProvider(A5TriageFixtures.Now),
            logger ?? NullLogger<TriageMonkeyService>.Instance,
            profiles: new InMemoryCompanyProfileStore([Profile(profileKey, privacy)]),
            redactor: new EvidenceRedactor());

        await service.InvestigateAsync(investigation.InvestigationId, CancellationToken.None);
        return (store, investigation.InvestigationId, llm);
    }

    private static JsonElement SensitiveEvidenceJson() => JsonSerializer.SerializeToElement(new
    {
        exception = $"Payment failed for {Email} with card {Card}",
        cardNumber = 4111111111111111,
        requests = 42,
        customDimensions = new Dictionary<string, string> { ["MemberId"] = MemberId, ["Stage"] = "checkout" },
    });

    private static ForensicCase Case(string profileKey) => new()
    {
        InvestigationPackageId = Guid.NewGuid(),
        ProfileKey = profileKey,
        CaseId = "case-1",
        EntityKey = "club:1",
        Vertical = "Hotels",
        Stage = "checkout",
        Verdict = $"Checkout failed for {Email}",
        ProbableRootCause = $"Card {Card} declined by the vendor",
        Decision = "escalate",
        Confidence = 0.5,
        CitationsTotal = 1,
        CitationsVerified = 1,
        CompletedAt = DateTimeOffset.UtcNow,
    };

    private static Finding SensitiveFinding() => new()
    {
        Domain = "cars",
        FindingType = FindingType.LatencySpikeCluster,
        Stage = "06 Checkout",
        Title = $"Checkout errors for {Email}",
        Severity = "high",
        EvidenceJson = """{"statusCodeBreakdown":{"500":3}}""",
        OriginMonkeyKey = MonkeyKey.LatencySpikeMonkey,
        CorrelationId = "cars:06 Checkout",
    };

    private static ClubIdentity Club() =>
        new("host:example.com", ClubEntityKind.Host, "15639", "308", "Brand", "Brand Travel", ClubResolution.HostExactMatch);

    private static ScoutBoardRow Row()
    {
        var path = $"/users/{Email}/reservations";
        var payload = JsonSerializer.Serialize(new
        {
            Paths = new[] { path },
            SlowPaths = new[] { path },
            Operations = new[] { "POST /reservations/purchase" },
            Users = Array.Empty<string>(),
            Correlations = new[] { "corr-1" },
            Ops = new[] { "op-1" },
        });

        return new ScoutBoardRow(
            ScoutBoard.Latency, "case-a", "group", "Hotels", "host:example.com", "07 Booking", "SOR",
            "Investigate now", 3, "P95 >=60s with >=20 requests",
            DateTimeOffset.Parse("2026-10-07T15:00:00Z"), DateTimeOffset.Parse("2026-10-07T15:05:00Z"),
            payload, """{"Requests":174}""");
    }

    private static CompanyProfile Profile(string key, PrivacySettings? privacy) => new(
        key,
        key,
        new CompanyLabels("Service", "Flow", "Step", "Entity", "Owner"),
        [],
        [],
        new CompanyGlossary("service", "flow", "step", "entity", "owner"),
        Privacy: privacy);

    private sealed class SensitiveEvidenceProvider(string dimension) : IEvidenceProvider
    {
        public string Dimension => dimension;

        public Task<EvidenceSection> CollectAsync(TriageSignal signal, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken ct = default) =>
            Task.FromResult(new EvidenceSection(dimension, true, SensitiveEvidenceJson(), null));
    }

    private sealed class CapturingLlm(string response, float[]? embedding = null) : ILlmClient
    {
        public List<string> Prompts { get; } = [];

        public List<string> EmbeddingInputs { get; } = [];

        public Task<(string Content, int InputTokens, int OutputTokens, string ModelUsed)> CompleteAsync(string prompt, int maxTokens, CancellationToken ct = default)
        {
            Prompts.Add(prompt);
            return Task.FromResult((response, 100, 50, "fake-model"));
        }

        public Task<EmbeddingResult> GetEmbeddingAsync(string text, CancellationToken ct = default)
        {
            EmbeddingInputs.Add(text);
            return Task.FromResult(embedding is null ? EmbeddingResult.Unavailable : new EmbeddingResult(embedding, "fake-embedding"));
        }
    }

    private sealed class ThrowingActiveCompany : IActiveCompanyProfileStore
    {
        public Task<string?> GetActiveAsync(CancellationToken ct = default) => throw new InvalidOperationException("SQL transient failure");

        public Task<ActiveCompanyProfile?> GetActiveDetailAsync(CancellationToken ct = default) => throw new InvalidOperationException("SQL transient failure");

        public Task SetActiveAsync(string profileKey, string changedBy, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class CapturingRetriever : IKnowledgeRetriever
    {
        public List<string> Queries { get; } = [];

        public List<string?> RetrievedKeys { get; } = [];

        public List<(KnowledgeChunk Chunk, string? ProfileKey)> Indexed { get; } = [];

        public Task<IReadOnlyList<KnowledgeChunk>> RetrieveAsync(string query, int topK, CancellationToken ct = default, string? profileKey = null)
        {
            Queries.Add(query);
            RetrievedKeys.Add(profileKey);
            return Task.FromResult<IReadOnlyList<KnowledgeChunk>>([]);
        }

        public Task IndexAsync(KnowledgeChunk chunk, CancellationToken ct = default, string? profileKey = null)
        {
            Indexed.Add((chunk, profileKey));
            return Task.CompletedTask;
        }

        public Task<IReadOnlySet<string>> GetIndexedHashesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(StringComparer.Ordinal));

        public Task<IReadOnlyList<KnowledgeDocumentSummary>> GetIndexedDocumentsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<KnowledgeDocumentSummary>>([]);

        public Task<IReadOnlyList<KnowledgeDocumentSection>> GetDocumentSectionsAsync(string documentTitle, string? sourceType, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<KnowledgeDocumentSection>>([]);
    }

    private sealed class FixedActiveCompany(string profileKey) : IActiveCompanyProfileStore
    {
        public Task<string?> GetActiveAsync(CancellationToken ct = default) => Task.FromResult<string?>(profileKey);

        public Task<ActiveCompanyProfile?> GetActiveDetailAsync(CancellationToken ct = default) =>
            Task.FromResult<ActiveCompanyProfile?>(new ActiveCompanyProfile(profileKey, DateTimeOffset.UtcNow, null));

        public Task SetActiveAsync(string profileKey, string changedBy, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class CollectingLogger<T> : ILogger<T>
    {
        private readonly List<string> _messages = [];

        public IReadOnlyList<string> Messages
        {
            get { lock (_messages) return _messages.ToList(); }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_messages) _messages.Add(formatter(state, exception));
        }
    }
}
