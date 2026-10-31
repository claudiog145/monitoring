namespace JourneyMonitor.Core.Domain;

/// <summary>
/// A single detected reliability problem, raised by a monkey (today only "latency-spike-monkey").
/// Mirrors the "Finding" concept from the AI Reliable Platform Portal reference design, scoped down
/// for this repo's MVP: no dedicated findings database yet (see <see cref="IFindingsRepositoryMarker"/>
/// note in Abstractions), in-memory only until a connection string is configured.
/// </summary>
public sealed class Finding
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>hotels | cars | cruises | air.</summary>
    public required string Domain { get; init; }

    /// <summary>Only finding type today: "latency_spike_cluster".</summary>
    public required string FindingType { get; init; }

    public required string Stage { get; init; }

    /// <summary>Refreshed in place on later detector cycles (see IFindingsRepository.UpdateAsync) so an already-open finding reflects newly configured thresholds instead of staying frozen at whatever was true when it was first created.</summary>
    public required string Title { get; set; }

    /// <summary>low | medium | high | critical. Refreshed alongside Title.</summary>
    public required string Severity { get; set; }

    /// <summary>
    /// Latest evidence for this cluster (never empty) — the Doctor Monkey has nothing to reason
    /// about without this. Refreshed alongside Title on later detector cycles, not frozen at
    /// creation time. See EvidenceJson shape in the design doc.
    /// </summary>
    public required string EvidenceJson { get; set; }

    /// <summary>open | acknowledged | resolved.</summary>
    public string Status { get; set; } = "open";

    public required string OriginMonkeyKey { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Dedup key so the same cluster isn't re-created every detector cycle: "{domain}:{stage}".</summary>
    public required string CorrelationId { get; init; }
}

/// <summary>The Doctor Monkey's deep-diagnosis output for one <see cref="Finding"/>.</summary>
public sealed class DoctorReport
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public required Guid FindingId { get; init; }

    /// <summary>One line, plain language.</summary>
    public required string Verdict { get; init; }

    /// <summary>application_code | server_infrastructure | external_dependency | data_quality | configuration | unknown.</summary>
    public required string ErrorCategory { get; init; }

    public string? RootCauseHypothesis { get; init; }

    /// <summary>0.0-1.0. Low confidence + "unknown" category is a valid, preferred-over-guessing outcome.</summary>
    public required double Confidence { get; init; }

    /// <summary>JSON array of {chunkId, why} — every claim in Verdict/RootCauseHypothesis must trace to one of these.</summary>
    public string? CitationsJson { get; init; }

    /// <summary>JSON {description, codeSnippet, filePathHint} — only emitted when the LLM cited real evidence, never fabricated.</summary>
    public string? ProposedFixJson { get; init; }

    public required string ModelUsed { get; init; }

    public int TokensIn { get; init; }

    public int TokensOut { get; init; }

    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>useful | not_useful | wrong | null (no feedback yet).</summary>
    public string? HumanFeedback { get; set; }
}

/// <summary>
/// One chunk of the (MVP-lightweight) knowledge base the Doctor Monkey retrieves from.
/// No vector DB engine: embeddings are stored as JSON float arrays and compared with in-process
/// cosine similarity, which is fine at the corpus size expected for this MVP (self-bootstrapped
/// from this repo's own past findings/reports — see <see cref="KnowledgeSourceType"/>).
/// </summary>
public sealed class KnowledgeChunk
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>See <see cref="KnowledgeSourceType"/>.</summary>
    public required string SourceType { get; init; }

    /// <summary>e.g. the FindingId or DoctorReportId this chunk was derived from.</summary>
    public string? SourceRef { get; init; }

    public required string Content { get; init; }

    /// <summary>Embedding vector as a plain float array (no native vector column — see class remarks).</summary>
    public required float[] Embedding { get; init; }

    /// <summary>
    /// The model that produced <see cref="Embedding"/>. Required because vectors from two different
    /// models cannot be compared: cosine similarity across them is arithmetic without meaning, and
    /// with different dimensions it cannot be computed at all. Retrieval filters on this, so a
    /// provider/model switch loses reach visibly instead of returning confident nonsense.
    /// Chunks written before this field existed carry <see cref="UnknownEmbeddingModel"/>.
    /// </summary>
    public required string EmbeddingModel { get; init; }

    /// <summary>Marker for rows stored before the embedding model was recorded - never matches a live model, so they are excluded from retrieval until re-indexed.</summary>
    public const string UnknownEmbeddingModel = "unknown";

    /// <summary>What an import records when the provider was unreachable. Distinct from <see cref="UnknownEmbeddingModel"/> only by history: both mean "no comparable vector".</summary>
    public const string NoEmbeddingModel = "none";

    /// <summary>
    /// Whether this row carries a vector that can actually be compared. Two sentinels reach the
    /// column for historical reasons, so every caller must ask here rather than test one of them
    /// and silently report an unembedded chunk as retrievable by meaning.
    /// </summary>
    public static bool HasComparableEmbedding(string? embeddingModel) =>
        !string.IsNullOrWhiteSpace(embeddingModel)
        && !string.Equals(embeddingModel, UnknownEmbeddingModel, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(embeddingModel, NoEmbeddingModel, StringComparison.OrdinalIgnoreCase);

    /// <summary>Business area this chunk belongs to (Air, Cruise, NG-Hotel, Telecom...). For imported documents it comes from the source folder, the only domain label the corpus carries.</summary>
    public string? Domain { get; init; }

    /// <summary>The source document this chunk came from - what a citation has to name for a human to go read it.</summary>
    public string? DocumentTitle { get; init; }

    /// <summary>Which section of that document ("Root Cause", "Timeline", "5 Whys Analysis"...).</summary>
    public string? SectionTitle { get; init; }

    /// <summary>
    /// Hash of the normalized body, used to keep duplicated documents from counting twice in
    /// cosine retrieval. Null for chunks written before this existed (the self-bootstrapped
    /// finding history), which is why the uniqueness constraint is filtered rather than global.
    /// </summary>
    public string? ContentHash { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public static class KnowledgeSourceType
{
    /// <summary>Self-bootstrapping: past Findings + DoctorReports from this same repo.</summary>
    public const string FindingHistory = "finding_history";

    /// <summary>
    /// A formal post-incident RCA written and reviewed by a human. The only source in which a
    /// stated cause can be treated as a CONCLUSION rather than a report.
    /// </summary>
    public const string RcaDocument = "rca_document";

    /// <summary>
    /// An exported incident ticket. Records what was observed and done - a symptom and a timeline,
    /// not a validated cause. Citing one as if it were an RCA presents a report as a finding.
    /// </summary>
    public const string IncidentTicket = "incident_ticket";

    /// <summary>Imported incident material that matches neither template. Labelled honestly instead of being forced into the nearest bucket.</summary>
    public const string IncidentDocument = "incident_document";

    /// <summary>The three types that come from the imported corpus rather than from this system's own output.</summary>
    public static readonly IReadOnlyList<string> ImportedTypes = [RcaDocument, IncidentTicket, IncidentDocument];
}

public static class FindingType
{
    public const string LatencySpikeCluster = "latency_spike_cluster";

    /// <summary>Raised by BurnRateDetector/SloBurnRateMonkeyFunction - see Domain/SloAlerting.cs.</summary>
    public const string SloBurnRate = "slo_burn_rate";
}

public static class MonkeyKey
{
    public const string LatencySpikeMonkey = "latency-spike-monkey";
    public const string DoctorMonkey = "doctor-monkey";

    /// <summary>SLO burn-rate engine - see docs/product/wi-238036-threshold-research-and-wi-package-2026-09-29.md.</summary>
    public const string SloBurnRateMonkey = "slo-burn-rate-monkey";

    /// <summary>
    /// Detection phase over the latency priority board - see Domain/Scout.cs. Split from the
    /// exception scout so each board gets its own cadence, its own per-cycle cap and its own
    /// minimum rank: one flat cap shared by two boards meant whichever board was read first could
    /// spend the whole budget.
    /// </summary>
    public const string LatencyScoutMonkey = "latency-scout-monkey";

    /// <summary>Detection phase over the exception priority board. Identical pipeline to <see cref="LatencyScoutMonkey"/>, different board.</summary>
    public const string ExceptionScoutMonkey = "exception-scout-monkey";

    /// <summary>L3 analysis phase: consumes the scouts' packages off the work queue and runs the multi-agent investigation - see Domain/Inspector.cs.</summary>
    public const string InspectorMonkey = "inspector-monkey";

    /// <summary>
    /// Writes the publishable RCA for a finished investigation - see Domain/Rca.cs. Has no timer:
    /// the Inspector hands it a case the moment one closes escalated with a probable root cause,
    /// and anything that did not qualify can still be composed on demand from the case file.
    /// </summary>
    public const string RcaMonkey = "rca-monkey";
}
