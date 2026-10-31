using JourneyMonitor.Core.Domain;

namespace JourneyMonitor.Core.Abstractions;

/// <summary>
/// Runs the KQL spike query for one domain, groups per-transaction latency spikes by stage
/// within a configurable window, and creates a <see cref="Finding"/> (with EvidenceJson
/// populated at creation time) when the occurrence threshold is met. Dedups by CorrelationId
/// so the same open cluster isn't re-created every detector cycle.
/// </summary>
public interface ILatencySpikeDetector
{
    Task<IReadOnlyList<Finding>> RunAsync(string domain, string environment, CancellationToken ct = default);
}

/// <summary>
/// Deep-diagnosis pipeline for one Finding: collect evidence -&gt; classify error type
/// (heuristic first) -&gt; retrieve knowledge -&gt; ask the LLM for a cited verdict. Never fabricates
/// a proposed fix without cited evidence; "unknown" is a valid, honest outcome.
/// </summary>
public interface IDoctorAgent
{
    Task<DoctorReport> DiagnoseAsync(Finding finding, CancellationToken ct = default);
}

/// <summary>Heuristic-first error classification (status codes / exception text), LLM only as a tie-breaker upstream.</summary>
public interface IErrorClassifier
{
    string Classify(Finding finding);
}

/// <summary>
/// Evaluates every enabled <see cref="Domain.SloDefinition"/> against recent
/// <see cref="Domain.GoldenSignalSample"/> buckets using Google SRE's multiwindow multi-burn-rate
/// method (see Domain/SloAlerting.cs) and raises a <see cref="Finding"/> (FindingType
/// "slo_burn_rate") when both windows of a combo exceed its multiplier. Dedups by CorrelationId
/// like <see cref="ILatencySpikeDetector"/> so an already-open burn stays open (refreshed in
/// place) instead of re-creating a Finding every cycle.
/// </summary>
public interface IBurnRateDetector
{
    Task<IReadOnlyList<Finding>> RunAsync(string domain, CancellationToken ct = default);
}

/// <summary>
/// One concrete email transport (SMTP / Azure Communication Services / SendGrid) - see
/// Infrastructure/Notifications. Never throws; a transport-level failure is reported via
/// <c>Error</c> so EmailAlertNotifier can fall through to the next enabled provider.
/// </summary>
public interface IEmailSender
{
    Task<(bool Success, string? Error)> SendAsync(IReadOnlyList<string> recipients, string subject, string body, CancellationToken ct = default);

    /// <summary>Whether this transport is enabled/configured given the current settings - used to skip (and not log a phantom attempt for) a transport the operator never turned on.</summary>
    bool IsEnabled(AlertChannelSetting settings);
}

/// <summary>
/// Records when each monkey actually ran, independent of whether that run produced a
/// Finding/DoctorReport - used by GetMonkeysFunction for an honest "lastRun"/"nextRun" instead of
/// inferring it from the most recent Finding/DoctorReport timestamp (which is stale/misleading
/// whenever a cycle runs on schedule but finds nothing new).
/// </summary>
public interface IMonkeyRunTracker
{
    Task RecordRunAsync(string monkeyKey, DateTimeOffset at, CancellationToken ct = default);

    /// <summary>Null when the monkey hasn't run yet in this process (e.g. right after a cold start).</summary>
    Task<DateTimeOffset?> GetLastRunAsync(string monkeyKey, CancellationToken ct = default);
}

/// <summary>Hybrid-ish retrieval over the lightweight KnowledgeChunks store (in-process cosine similarity, no vector DB).</summary>
public interface IKnowledgeRetriever
{
    /// <summary>
    /// <paramref name="profileKey"/> is the company the precedents are for: it returns that
    /// company's chunks plus the shared corpus (stored ProfileKey ""), and nothing else. Null means
    /// no filter - the whole corpus - which is what the non-per-company callers want.
    ///
    /// It trails <paramref name="ct"/> rather than preceding it so the existing callers that pass
    /// the token positionally keep compiling; implementors still have to handle it, which is the
    /// point - a retriever that silently ignored the filter would make a green test out of a leak.
    /// </summary>
    Task<IReadOnlyList<KnowledgeChunk>> RetrieveAsync(string query, int topK, CancellationToken ct = default, string? profileKey = null);

    /// <summary>
    /// Adds a chunk to the store (used to self-bootstrap the KB from this app's own findings/reports).
    /// <paramref name="profileKey"/> null or blank stores it as shared: a chunk nobody scoped is
    /// readable by everyone by definition, so making a document private has to be explicit.
    /// </summary>
    Task IndexAsync(KnowledgeChunk chunk, CancellationToken ct = default, string? profileKey = null);

    /// <summary>
    /// Every <see cref="KnowledgeChunk.ContentHash"/> already stored. Loaded once by the corpus
    /// import so a re-run skips what is already there instead of indexing the same precedent twice
    /// - a duplicate silently doubles that precedent's weight in cosine retrieval.
    /// </summary>
    Task<IReadOnlySet<string>> GetIndexedHashesAsync(CancellationToken ct = default);

    /// <summary>
    /// One row per imported document rather than per chunk, so the portal can show what the corpus
    /// actually holds. A chunk count alone cannot answer the only question a reader asks of an
    /// import - "is the document I expected in there?" - and without this the corpus is invisible
    /// outside the database.
    /// </summary>
    Task<IReadOnlyList<KnowledgeDocumentSummary>> GetIndexedDocumentsAsync(CancellationToken ct = default);

    /// <summary>
    /// The indexed text of one document, in order. This is what the agent actually reads, not the
    /// original file: consecutive sections overlap by design so a sentence split across a boundary
    /// is still retrievable, which means stitching them together would repeat text. They are kept
    /// as sections for that reason.
    /// </summary>
    Task<IReadOnlyList<KnowledgeDocumentSection>> GetDocumentSectionsAsync(string documentTitle, string? sourceType, CancellationToken ct = default);
}

/// <summary>One indexed passage. Carries no vector: a reader cannot use one, and it is large.</summary>
public sealed record KnowledgeDocumentSection(string SectionTitle, string Content, string SourceRef);

/// <summary>
/// What the knowledge base holds for one document. <paramref name="ChunksWithEmbedding"/> is
/// reported separately because a chunk stored without a vector is reachable only by keyword: it is
/// indexed, but it will not surface as a precedent on meaning alone, and a total that hides this
/// would overstate what the agent can actually recall.
/// </summary>
public sealed record KnowledgeDocumentSummary(
    string DocumentTitle,
    string SourceType,
    string Domain,
    int Chunks,
    int ChunksWithEmbedding,
    DateTimeOffset IndexedAt);

/// <summary>
/// One embedding, together with the model that produced it. The model travels with the vector
/// because vectors from different models are not comparable - cosine similarity across two models
/// is arithmetic without meaning, and when the dimensions differ it cannot even be computed. The
/// knowledge base stores this alongside the vector so a provider switch degrades visibly (fewer
/// comparable chunks) instead of silently returning nonsense.
/// </summary>
public sealed record EmbeddingResult(float[]? Vector, string Model)
{
    /// <summary>Embeddings are not configured/reachable - callers skip KB retrieval rather than fabricate.</summary>
    public static readonly EmbeddingResult Unavailable = new(null, "none");

    public bool IsAvailable => Vector is { Length: > 0 };
}

/// <summary>Minimal chat-completion abstraction so JourneyMonitor.Core stays free of any specific LLM SDK/HTTP client.</summary>
public interface ILlmClient
{
    /// <summary>Returns (content, inputTokens, outputTokens, modelUsed). Content is empty when the provider is not configured/unavailable - callers must treat that as "unknown", never fabricate. modelUsed is "none" in that case, otherwise the exact model this specific call resolved and sent - callers must record this instead of re-resolving settings afterward, which could race with a concurrent Settings change.</summary>
    Task<(string Content, int InputTokens, int OutputTokens, string ModelUsed)> CompleteAsync(string prompt, int maxTokens, CancellationToken ct = default);

    /// <summary>Returns <see cref="EmbeddingResult.Unavailable"/> when embeddings are not available — callers fall back gracefully (e.g. skip KB retrieval).</summary>
    Task<EmbeddingResult> GetEmbeddingAsync(string text, CancellationToken ct = default);
}

public interface IFindingsRepository
{
    Task<Finding> AddAsync(Finding finding, CancellationToken ct = default);

    /// <summary>Persists a refreshed Title/Severity/EvidenceJson for an already-open finding (see LatencySpikeDetectorService).</summary>
    Task<Finding> UpdateAsync(Finding finding, CancellationToken ct = default);

    /// <summary>Looks up an existing OPEN finding by its dedup CorrelationId, or null if none exists yet.</summary>
    Task<Finding?> GetOpenByCorrelationIdAsync(string correlationId, CancellationToken ct = default);

    /// <summary>
    /// Every currently OPEN finding for a domain, regardless of stage/correlation - used by the
    /// Latency Spike Monkey detectors to reconcile stale findings whose stage no longer qualifies
    /// under the current thresholds (see LatencySpikeDetectorService/SqlLatencySpikeDetectorService).
    /// </summary>
    Task<IReadOnlyList<Finding>> GetOpenByDomainAsync(string domain, CancellationToken ct = default);

    Task<IReadOnlyList<Finding>> GetAllAsync(CancellationToken ct = default);

    Task<Finding?> GetByIdAsync(Guid id, CancellationToken ct = default);
}

public interface IDoctorReportsRepository
{
    Task<DoctorReport> AddAsync(DoctorReport report, CancellationToken ct = default);

    Task<DoctorReport?> GetByFindingIdAsync(Guid findingId, CancellationToken ct = default);

    /// <summary>All reports, most-recently-generated first. Used by GetMonkeysFunction's activity feed to avoid an N+1 GetByFindingIdAsync per finding.</summary>
    Task<IReadOnlyList<DoctorReport>> GetAllAsync(CancellationToken ct = default);
}
