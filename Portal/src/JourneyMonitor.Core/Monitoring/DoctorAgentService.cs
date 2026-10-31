using System.Text.Json;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using Microsoft.Extensions.Logging;

namespace JourneyMonitor.Core.Monitoring;

/// <summary>
/// The Doctor Monkey's 4-stage deep-diagnosis pipeline for one Finding: (a) evidence is already
/// in EvidenceJson (populated by the detector at creation time — no separate collector call in
/// this MVP), (b) heuristic error classification, (c) knowledge retrieval over the lightweight
/// KB, (d) a single LLM call with a strict JSON contract. Anti-hallucination rules from the
/// design doc are enforced here, not left to the prompt alone: if the LLM is unreachable or
/// returns unparsable JSON, the report is explicitly "unknown" with confidence 0 — never a
/// fabricated guess. Successful reports self-index into the KB (feedback loop / compounding
/// corpus effect called out by the design's CEO role).
/// </summary>
public sealed class DoctorAgentService(
    IErrorClassifier classifier,
    IKnowledgeRetriever knowledgeRetriever,
    ILlmClient llmClient,
    IDoctorReportsRepository reportsRepository,
    ILlmCallLogRepository llmCallLogRepository,
    IMonkeyRunTracker runTracker,
    ILogger<DoctorAgentService> logger,
    // Trailing and optional, like the provider on RcaAgentService: the composition root builds
    // this type with an explicit `new`, so a required parameter would break files this change
    // does not own. Null means the whole corpus - the behaviour before companies existed.
    IActiveCompanyProfileStore? activeCompany = null) : IDoctorAgent
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<DoctorReport> DiagnoseAsync(Finding finding, CancellationToken ct = default)
    {
        await runTracker.RecordRunAsync(MonkeyKey.DoctorMonkey, DateTimeOffset.UtcNow, ct);

        var heuristicCategory = classifier.Classify(finding);

        // One read for both the lookup and the write-back, so a diagnosis cannot be grounded in
        // one company's history and then filed under another's.
        var profileKey = await ResolveCompanyAsync(ct);

        var query = $"{finding.Domain} {finding.Stage} {heuristicCategory} {finding.Title}";
        var chunks = await knowledgeRetriever.RetrieveAsync(query, topK: 5, ct, profileKey);

        var prompt = BuildPrompt(finding, heuristicCategory, chunks);
        var (content, tokensIn, tokensOut, modelUsed) = await llmClient.CompleteAsync(prompt, maxTokens: 800, ct);

        var report = string.IsNullOrWhiteSpace(content)
            ? UnknownReport(finding.Id, "LLM provider unavailable or not configured — no diagnosis produced.", modelUsed, 0, 0)
            : ParseLlmResponse(content, finding.Id, heuristicCategory, modelUsed, tokensIn, tokensOut, chunks.Count);

        var saved = await reportsRepository.AddAsync(report, ct);

        // Best-effort: the DoctorReport (the source of truth for the UI) is already saved above -
        // if this secondary audit-log write fails, swallow it rather than throw. Finding dedup
        // means a thrown exception here would never let the diagnosis be retried, losing the real
        // report too just because the debug log didn't persist.
        try
        {
            await llmCallLogRepository.AddAsync(new LlmCallLog(
                Id: Guid.NewGuid(),
                DoctorReportId: saved.Id,
                FindingId: finding.Id,
                PromptText: prompt,
                RawResponseText: content,
                ModelUsed: saved.ModelUsed,
                TokensIn: saved.TokensIn,
                TokensOut: saved.TokensOut,
                CreatedAt: saved.GeneratedAt), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to persist LlmCallLog for DoctorReport {ReportId} (Finding {FindingId}) - the report itself was saved.", saved.Id, finding.Id);
        }

        // Self-bootstrap the KB from this report so the NEXT similar finding has a precedent.
        var embedding = await llmClient.GetEmbeddingAsync(
            $"{finding.Title}\n{report.Verdict}\n{report.RootCauseHypothesis}", ct);
        if (embedding.IsAvailable)
        {
            await knowledgeRetriever.IndexAsync(new KnowledgeChunk
            {
                SourceType = KnowledgeSourceType.FindingHistory,
                SourceRef = saved.Id.ToString(),
                Content = $"[{finding.Domain}/{finding.Stage}] {finding.Title}\nVerdict: {report.Verdict}\nRootCause: {report.RootCauseHypothesis}",
                Embedding = embedding.Vector!,
                EmbeddingModel = embedding.Model,
            }, ct, profileKey);
        }

        return saved;
    }

    /// <summary>
    /// Null on anything other than a clean answer: an unattributed diagnosis is worth a warning,
    /// never worth failing the diagnosis the operator is waiting for.
    /// </summary>
    private async Task<string?> ResolveCompanyAsync(CancellationToken ct)
    {
        if (activeCompany is null) return null;

        try
        {
            var key = await activeCompany.GetActiveAsync(ct);
            return string.IsNullOrWhiteSpace(key) ? null : key;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "The active company could not be read, so this diagnosis is neither scoped to one nor attributed to one.");
            return null;
        }
    }

    private static DoctorReport UnknownReport(Guid findingId, string reason, string model, int tokensIn, int tokensOut) => new()
    {
        FindingId = findingId,
        Verdict = TruncateVerdict(reason),
        ErrorCategory = "unknown",
        Confidence = 0,
        ModelUsed = model,
        TokensIn = tokensIn,
        TokensOut = tokensOut,
    };

    private static string BuildPrompt(Finding finding, string heuristicCategory, IReadOnlyList<KnowledgeChunk> chunks)
    {
        var citedChunks = chunks.Count == 0
            ? "(no precedents found in the knowledge base)"
            : string.Join("\n", chunks.Select((c, i) => $"[chunk-{i}] (source: {c.SourceType}/{c.SourceRef})\n{c.Content}"));

        return $$"""
            You are the Doctor Monkey, a reliability diagnosis agent for the JourneyMonitor platform.
            Analyze the following latency finding and respond with STRICT JSON ONLY (no markdown fences,
            no prose outside the JSON object), matching exactly this shape:

            {"verdict": "one line, plain language", "errorCategory": "application_code|server_infrastructure|external_dependency|data_quality|configuration|unknown", "rootCauseHypothesis": "string or null", "confidence": 0.0, "evidenceCited": [{"chunkId": "chunk-N", "why": "..."}], "proposedFix": {"description": "...", "codeSnippet": "... or null", "filePathHint": "... or null"} or null, "unknowns": ["..."]}

            Rules (mandatory):
            - Every claim in "verdict" or "rootCauseHypothesis" must be traceable to evidence below or a cited chunk.
            - If you cannot determine a root cause, use errorCategory "unknown" and a LOW confidence (<=0.3) — this is the PREFERRED honest answer over speculation.
            - Only include "proposedFix" if a cited chunk contains directly relevant source code or a documented fix; otherwise set it to null. NEVER fabricate a fix from general knowledge.
            - "unknowns" must list what you could not determine.

            Finding:
            - Domain: {{finding.Domain}}
            - Stage: {{finding.Stage}}
            - Title: {{finding.Title}}
            - Severity: {{finding.Severity}}
            - Heuristic error category (deterministic, pre-computed from status codes): {{heuristicCategory}}
            - EvidenceJson: {{finding.EvidenceJson}}

            Knowledge base precedents (cite by [chunk-N] id if used):
            {{citedChunks}}
            """;
    }

    private static DoctorReport ParseLlmResponse(string content, Guid findingId, string heuristicCategory, string model, int tokensIn, int tokensOut, int availableChunkCount)
    {
        var jsonText = ExtractJsonObject(content);
        if (jsonText is null)
        {
            return UnknownReport(findingId, "LLM response was not valid JSON — treating as unknown rather than guessing.", model, tokensIn, tokensOut);
        }

        try
        {
            using var doc = JsonDocument.Parse(jsonText);
            var root = doc.RootElement;

            string verdict = root.TryGetProperty("verdict", out var v) ? v.GetString() ?? "" : "";
            string errorCategory = root.TryGetProperty("errorCategory", out var ec) ? ec.GetString() ?? heuristicCategory : heuristicCategory;
            if (!ValidErrorCategories.Contains(errorCategory))
            {
                // The model is expected to pick from a closed taxonomy; an unrecognized value
                // (typo, hallucinated category, etc.) would otherwise leak into persisted reports
                // and break downstream consumers that assume one of these fixed values.
                errorCategory = heuristicCategory;
            }
            string? rootCause = root.TryGetProperty("rootCauseHypothesis", out var rc) && rc.ValueKind != JsonValueKind.Null ? rc.GetString() : null;
            double confidence = root.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetDouble() : 0;
            string? citationsJson = root.TryGetProperty("evidenceCited", out var evCited) ? evCited.GetRawText() : null;
            string? proposedFixJson = root.TryGetProperty("proposedFix", out var pf) && pf.ValueKind != JsonValueKind.Null ? pf.GetRawText() : null;

            // Anti-hallucination enforced in code, not just in the prompt: the model has been
            // caught citing a "chunk-N" that was never actually retrieved when the KB returned
            // zero chunks (e.g. embeddings unavailable). Never trust the prompt alone for this -
            // if there was nothing real to cite, there is no legitimate citation, full stop.
            if (availableChunkCount == 0)
            {
                citationsJson = null;
            }
            // A non-empty KB is still validated: any cited "chunk-N" outside the retrieved range
            // is fabricated and is dropped rather than shown as if it were real evidence. An empty
            // "[]" array is also treated as "no citations" (not a vacuously-valid empty list) so
            // the proposedFix gating below actually drops fixes that cite nothing.
            else if (citationsJson is not null && (!AllCitationsReferenceRetrievedChunks(citationsJson, availableChunkCount) || IsEmptyJsonArray(citationsJson)))
            {
                citationsJson = null;
            }

            if (proposedFixJson is not null && citationsJson is null)
            {
                // A proposed fix is only trustworthy when it traces to real cited evidence (design
                // rule: "proposedFix only if a cited chunk contains directly relevant source code").
                proposedFixJson = null;
            }

            return new DoctorReport
            {
                FindingId = findingId,
                Verdict = TruncateVerdict(string.IsNullOrWhiteSpace(verdict) ? "(no verdict provided)" : verdict),
                ErrorCategory = errorCategory,
                RootCauseHypothesis = rootCause,
                Confidence = Math.Clamp(confidence, 0, 1),
                CitationsJson = citationsJson,
                ProposedFixJson = proposedFixJson,
                ModelUsed = model,
                TokensIn = tokensIn,
                TokensOut = tokensOut,
            };
        }
        catch (JsonException)
        {
            return UnknownReport(findingId, "LLM response JSON did not match the expected contract — treating as unknown rather than guessing.", model, tokensIn, tokensOut);
        }
    }

    /// <summary>The closed error-category taxonomy the prompt asks the model to pick from - see BuildPrompt.</summary>
    private static readonly HashSet<string> ValidErrorCategories = new(StringComparer.Ordinal)
    {
        "application_code", "server_infrastructure", "external_dependency", "data_quality", "configuration", "unknown",
    };

    // Matches monitoring.DoctorReport.Verdict's nvarchar(1024) column (see CreateMonitoringFindings.sql
    // and SqlDoctorReportsRepository) - the LLM has no hard length limit enforced in the prompt, so a
    // valid-but-longer response must be bounded here rather than let SQL reject the whole insert (the
    // Finding would already be saved by then, and dedup would leave it permanently undiagnosed).
    private const int MaxVerdictLength = 1024;

    private static string TruncateVerdict(string verdict) =>
        verdict.Length <= MaxVerdictLength ? verdict : verdict[..MaxVerdictLength];

    private static bool IsEmptyJsonArray(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() == 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>LLMs sometimes wrap JSON in ```json fences despite instructions not to — strip those before parsing.</summary>
    private static string? ExtractJsonObject(string content)
    {
        var text = content.Trim();
        if (text.StartsWith("```"))
        {
            var firstNewline = text.IndexOf('\n');
            if (firstNewline >= 0) text = text[(firstNewline + 1)..];
            var fenceEnd = text.LastIndexOf("```", StringComparison.Ordinal);
            if (fenceEnd >= 0) text = text[..fenceEnd];
            text = text.Trim();
        }

        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        return text[start..(end + 1)];
    }

    /// <summary>True only if every "chunkId" in the citations array matches "chunk-N" for an N actually within [0, availableChunkCount).</summary>
    private static bool AllCitationsReferenceRetrievedChunks(string citationsJson, int availableChunkCount)
    {
        try
        {
            using var doc = JsonDocument.Parse(citationsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return false;

            foreach (var citation in doc.RootElement.EnumerateArray())
            {
                if (!citation.TryGetProperty("chunkId", out var chunkIdEl) || chunkIdEl.ValueKind != JsonValueKind.String)
                {
                    return false;
                }
                var chunkId = chunkIdEl.GetString();
                if (chunkId is null || !chunkId.StartsWith("chunk-", StringComparison.Ordinal)) return false;
                if (!int.TryParse(chunkId["chunk-".Length..], out var index)) return false;
                if (index < 0 || index >= availableChunkCount) return false;
            }
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
