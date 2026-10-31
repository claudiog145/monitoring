using System.Text.Json;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring;
using JourneyMonitor.Infrastructure.Monitoring;
using Microsoft.Extensions.Logging.Abstractions;

namespace JourneyMonitor.Tests.Monitoring;

/// <summary>Returns a fixed chat-completion response and an optional embedding, without calling any real LLM.</summary>
internal sealed class FakeLlmClient(string chatResponse, float[]? embedding = null, string modelUsed = "gpt-4o-mini") : ILlmClient
{
    public Task<(string Content, int InputTokens, int OutputTokens, string ModelUsed)> CompleteAsync(string prompt, int maxTokens, CancellationToken ct = default) =>
        Task.FromResult((chatResponse, 100, 50, modelUsed));

    public Task<EmbeddingResult> GetEmbeddingAsync(string text, CancellationToken ct = default) =>
        Task.FromResult(embedding is null ? EmbeddingResult.Unavailable : new EmbeddingResult(embedding, "fake-embedding-model"));
}

/// <summary>Always returns a fixed, pre-seeded set of chunks (or none), without any similarity computation.</summary>
internal sealed class FakeKnowledgeRetriever(IReadOnlyList<KnowledgeChunk> chunksToReturn) : IKnowledgeRetriever
{
    /// <summary>The company each call was scoped to, so "the Doctor asked for its own company" is observable.</summary>
    public List<string?> RetrievedProfileKeys { get; } = [];

    public List<string?> IndexedProfileKeys { get; } = [];

    public Task<IReadOnlyList<KnowledgeChunk>> RetrieveAsync(string query, int topK, CancellationToken ct = default, string? profileKey = null)
    {
        RetrievedProfileKeys.Add(profileKey);
        return Task.FromResult(chunksToReturn);
    }

    public Task IndexAsync(KnowledgeChunk chunk, CancellationToken ct = default, string? profileKey = null)
    {
        IndexedProfileKeys.Add(profileKey);
        return Task.CompletedTask;
    }

    public Task<IReadOnlySet<string>> GetIndexedHashesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(StringComparer.Ordinal));

    public Task<IReadOnlyList<KnowledgeDocumentSummary>> GetIndexedDocumentsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<KnowledgeDocumentSummary>>([]);

    public Task<IReadOnlyList<KnowledgeDocumentSection>> GetDocumentSectionsAsync(string documentTitle, string? sourceType, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<KnowledgeDocumentSection>>([]);
}

public class DoctorAgentServiceTests
{
    private static Finding SampleFinding() => new()
    {
        Domain = "cars",
        FindingType = FindingType.LatencySpikeCluster,
        Stage = "06 Checkout",
        Title = "266 latency spikes (>100ms) in stage '06 Checkout' (cars)",
        Severity = "high",
        EvidenceJson = """{"statusCodeBreakdown":{"200":262,"401":1}}""",
        OriginMonkeyKey = MonkeyKey.LatencySpikeMonkey,
        CorrelationId = "cars:06 Checkout",
    };

    private static DoctorAgentService BuildService(
        string chatResponse,
        IReadOnlyList<KnowledgeChunk> chunks,
        ILlmCallLogRepository? llmCallLogRepository = null) =>
        new(
            new HeuristicErrorClassifier(),
            new FakeKnowledgeRetriever(chunks),
            new FakeLlmClient(chatResponse, embedding: null),
            new InMemoryDoctorReportsRepository(),
            llmCallLogRepository ?? new InMemoryLlmCallLogRepository(),
            new InMemoryMonkeyRunTracker(),
            NullLogger<DoctorAgentService>.Instance);

    [Fact]
    public async Task PersistsThePromptAndRawResponseForEveryDiagnosis()
    {
        var llmCallLogRepository = new InMemoryLlmCallLogRepository();
        var llmResponse = """
            {"verdict":"Configuration issue","errorCategory":"configuration","rootCauseHypothesis":"bad config","confidence":0.7,"evidenceCited":[],"proposedFix":null,"unknowns":[]}
            """;
        var service = BuildService(llmResponse, chunks: [], llmCallLogRepository);

        var report = await service.DiagnoseAsync(SampleFinding(), CancellationToken.None);

        var log = await llmCallLogRepository.GetByDoctorReportIdAsync(report.Id, CancellationToken.None);
        Assert.NotNull(log);
        Assert.Equal(report.Id, log!.DoctorReportId);
        Assert.Equal(0.7, report.Confidence);
        Assert.Contains("Doctor Monkey", log.PromptText);
        Assert.Contains(SampleFinding().Title, log.PromptText);
        Assert.Equal(llmResponse, log.RawResponseText);
    }

    [Fact]
    public async Task RecordsTheModelReportedByTheCompletionItselfRatherThanResolvingSettingsAgain()
    {
        // Regression guard: DiagnoseAsync used to re-resolve LLM client settings after the call
        // just to learn which model was used, which could race with a concurrent Settings change
        // between the two resolutions. ModelUsed must now come straight from CompleteAsync's own
        // return value - use a model name the constructor's default wouldn't produce to prove it.
        var llmResponse = """
            {"verdict":"ok","errorCategory":"configuration","rootCauseHypothesis":null,"confidence":0.4,"evidenceCited":[],"proposedFix":null,"unknowns":[]}
            """;
        var service = new DoctorAgentService(
            new HeuristicErrorClassifier(),
            new FakeKnowledgeRetriever([]),
            new FakeLlmClient(llmResponse, embedding: null, modelUsed: "gpt-4o-super-custom"),
            new InMemoryDoctorReportsRepository(),
            new InMemoryLlmCallLogRepository(),
            new InMemoryMonkeyRunTracker(),
            NullLogger<DoctorAgentService>.Instance);

        var report = await service.DiagnoseAsync(SampleFinding(), CancellationToken.None);

        Assert.Equal("gpt-4o-super-custom", report.ModelUsed);
    }

    /// <summary>Throws on every call, standing in for a transient SQL failure while writing the LlmCallLog.</summary>
    private sealed class ThrowingLlmCallLogRepository : ILlmCallLogRepository
    {
        public Task<LlmCallLog> AddAsync(LlmCallLog log, CancellationToken ct = default) =>
            throw new InvalidOperationException("Simulated transient write failure.");

        public Task<LlmCallLog?> GetByDoctorReportIdAsync(Guid doctorReportId, CancellationToken ct = default) =>
            throw new InvalidOperationException("Simulated transient write failure.");
    }

    [Fact]
    public async Task StillReturnsTheSavedReportWhenTheLlmCallLogWriteFails()
    {
        // A failure persisting the debug/audit log must never lose the diagnosis itself - Finding
        // dedup means a thrown exception here would never let this Finding be retried.
        var llmResponse = """
            {"verdict":"Configuration issue","errorCategory":"configuration","rootCauseHypothesis":"bad config","confidence":0.7,"evidenceCited":[],"proposedFix":null,"unknowns":[]}
            """;
        var service = BuildService(llmResponse, chunks: [], new ThrowingLlmCallLogRepository());

        var report = await service.DiagnoseAsync(SampleFinding(), CancellationToken.None);

        Assert.Equal("configuration", report.ErrorCategory);
        Assert.Equal(0.7, report.Confidence);
    }

    [Fact]
    public async Task TruncatesAnOverlongVerdictToTheSqlColumnBoundInsteadOfLettingPersistenceFail()
    {
        // monitoring.DoctorReport.Verdict is nvarchar(1024) (see CreateMonitoringFindings.sql /
        // SqlDoctorReportsRepository) - the LLM has no hard length limit enforced in the prompt,
        // so a valid-but-longer response must be bounded here, not fail after the Finding (source
        // of truth) is already saved.
        var overlongVerdict = new string('x', 2000);
        var llmResponse = $$"""
            {"verdict":"{{overlongVerdict}}","errorCategory":"configuration","rootCauseHypothesis":null,"confidence":0.5,"evidenceCited":[],"proposedFix":null,"unknowns":[]}
            """;
        var service = BuildService(llmResponse, chunks: []);

        var report = await service.DiagnoseAsync(SampleFinding(), CancellationToken.None);

        Assert.Equal(1024, report.Verdict.Length);
    }

    [Fact]
    public async Task DropsCitationsThatReferenceAChunkWhenZeroChunksWereRetrieved()
    {
        // Reproduces a real failure observed live: the LLM cited "chunk-1" even though the
        // knowledge base returned zero chunks (e.g. embeddings unavailable) - that citation is
        // fabricated and must never reach the stored report.
        var llmResponse = """
            {"verdict":"Configuration issue","errorCategory":"configuration","rootCauseHypothesis":"bad config","confidence":0.7,"evidenceCited":[{"chunkId":"chunk-1","why":"fabricated"}],"proposedFix":null,"unknowns":[]}
            """;
        var service = BuildService(llmResponse, chunks: []);

        var report = await service.DiagnoseAsync(SampleFinding(), CancellationToken.None);

        Assert.Null(report.CitationsJson);
        // The rest of the diagnosis is still kept - only the fabricated citation is dropped.
        Assert.Equal("configuration", report.ErrorCategory);
        Assert.Equal(0.7, report.Confidence);
    }

    [Fact]
    public async Task KeepsCitationsThatReferenceAnActuallyRetrievedChunk()
    {
        var chunks = new List<KnowledgeChunk>
        {
            new() { SourceType = KnowledgeSourceType.FindingHistory, Content = "precedent", Embedding = [0.1f, 0.2f], EmbeddingModel = "fake-embedding-model" },
        };
        var llmResponse = """
            {"verdict":"Configuration issue","errorCategory":"configuration","rootCauseHypothesis":"bad config","confidence":0.7,"evidenceCited":[{"chunkId":"chunk-0","why":"real precedent"}],"proposedFix":null,"unknowns":[]}
            """;
        var service = BuildService(llmResponse, chunks);

        var report = await service.DiagnoseAsync(SampleFinding(), CancellationToken.None);

        Assert.NotNull(report.CitationsJson);
        using var doc = JsonDocument.Parse(report.CitationsJson!);
        Assert.Equal("chunk-0", doc.RootElement[0].GetProperty("chunkId").GetString());
    }

    [Fact]
    public async Task DropsCitationsThatReferenceAChunkOutsideTheRetrievedRange()
    {
        var chunks = new List<KnowledgeChunk>
        {
            new() { SourceType = KnowledgeSourceType.FindingHistory, Content = "precedent", Embedding = [0.1f, 0.2f], EmbeddingModel = "fake-embedding-model" },
        };
        var llmResponse = """
            {"verdict":"Configuration issue","errorCategory":"configuration","rootCauseHypothesis":"bad config","confidence":0.7,"evidenceCited":[{"chunkId":"chunk-5","why":"out of range"}],"proposedFix":null,"unknowns":[]}
            """;
        var service = BuildService(llmResponse, chunks);

        var report = await service.DiagnoseAsync(SampleFinding(), CancellationToken.None);

        Assert.Null(report.CitationsJson);
    }

    [Fact]
    public async Task DropsAProposedFixWhenItsCitationsWereDropped()
    {
        var llmResponse = """
            {"verdict":"Configuration issue","errorCategory":"configuration","rootCauseHypothesis":"bad config","confidence":0.7,"evidenceCited":[{"chunkId":"chunk-0","why":"fabricated"}],"proposedFix":{"description":"fix it","codeSnippet":null,"filePathHint":null},"unknowns":[]}
            """;
        var service = BuildService(llmResponse, chunks: []);

        var report = await service.DiagnoseAsync(SampleFinding(), CancellationToken.None);

        Assert.Null(report.CitationsJson);
        Assert.Null(report.ProposedFixJson);
    }

    [Fact]
    public async Task FallsBackToTheHeuristicCategoryWhenTheModelReturnsAnUnrecognizedCategory()
    {
        var llmResponse = """
            {"verdict":"Something is wrong","errorCategory":"totally_made_up_category","rootCauseHypothesis":null,"confidence":0.5,"evidenceCited":[],"proposedFix":null,"unknowns":[]}
            """;
        var service = BuildService(llmResponse, chunks: []);

        // SampleFinding()'s EvidenceJson has statusCodeBreakdown {"200":262,"401":1} -> heuristic = "configuration".
        var report = await service.DiagnoseAsync(SampleFinding(), CancellationToken.None);

        Assert.Equal("configuration", report.ErrorCategory);
    }

    [Fact]
    public async Task DropsAProposedFixWhenCitationsIsAnEmptyArray()
    {
        // An empty "[]" is not a real citation - proposedFix must not survive on the strength of nothing.
        var chunks = new List<KnowledgeChunk>
        {
            new() { SourceType = KnowledgeSourceType.FindingHistory, Content = "precedent", Embedding = [0.1f, 0.2f], EmbeddingModel = "fake-embedding-model" },
        };
        var llmResponse = """
            {"verdict":"Configuration issue","errorCategory":"configuration","rootCauseHypothesis":"bad config","confidence":0.7,"evidenceCited":[],"proposedFix":{"description":"fix it","codeSnippet":null,"filePathHint":null},"unknowns":[]}
            """;
        var service = BuildService(llmResponse, chunks);

        var report = await service.DiagnoseAsync(SampleFinding(), CancellationToken.None);

        Assert.Null(report.CitationsJson);
        Assert.Null(report.ProposedFixJson);
    }
}
