using System.Text;
using System.Text.Json;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Core.Monitoring;

/// <summary>
/// Writes the corporate RCA for a finished investigation, in the template measured across the 40
/// reviewed RCAs (see <see cref="RcaTemplate"/>), with every statement carrying an epistemic label.
///
/// The document is always complete - a leadership-facing RCA with holes in it gets filled in by
/// whoever reads it, usually wrongly. What keeps that honest is the label: a financial impact the
/// agent could not measure is still written, but marked <c>inference</c>, because this system has
/// no revenue data anywhere in its schema and saying otherwise would be fabrication.
///
/// Two rules live in CODE rather than in the prompt, because a prompt is not a boundary:
/// 1. A case whose citations never verified cannot produce a single <c>fact</c>.
/// 2. A precedent the model names that was not actually retrieved is dropped.
/// </summary>
public sealed class RcaAgentService(
    ILlmClient llmClient,
    IKnowledgeRetriever knowledgeRetriever,
    IOptions<RcaOptions> options,
    ILogger<RcaAgentService> logger,
    // Trailing and optional: the composition root builds this type with an explicit `new`, so a
    // required parameter here would be a compile break in a file this change does not own. Null
    // means the neutral vocabulary - see the note in the report accompanying this change.
    ICompanyAdapterProvider? companyAdapters = null) : IRcaAgent
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    public async Task<RcaReport> ComposeAsync(ForensicCase forensicCase, CancellationToken ct = default)
    {
        var settings = options.Value;
        var report = new RcaReport
        {
            ForensicCaseId = forensicCase.Id,
            Title = BuildTitle(forensicCase),
            Domain = forensicCase.Vertical,
            Stage = forensicCase.Stage,
        };

        var precedents = await RetrievePrecedentsAsync(forensicCase, settings.ResolvedMaxPrecedents, ct);
        report.Precedents = precedents.Select(p => new RcaPrecedent(
            p.DocumentTitle ?? "(untitled)", p.SectionTitle ?? "(whole document)", p.Domain, p.SourceType)).ToList();

        var prompt = BuildPrompt(forensicCase, precedents, await CompanyVocabulary.ResolveAsync(companyAdapters, logger, ct));
        var (content, tokensIn, tokensOut, modelUsed) = await llmClient.CompleteAsync(prompt, settings.ResolvedMaxTokens, ct);

        report.LlmCalls = 1;
        report.TokensIn = tokensIn;
        report.TokensOut = tokensOut;
        report.ModelUsed = modelUsed;

        if (string.IsNullOrWhiteSpace(content))
        {
            report.Error = "The LLM provider returned nothing, so no RCA was written. This is reported rather than replaced with a generated-looking document.";
            return report;
        }

        try
        {
            Populate(report, content, forensicCase, precedents);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "RCA composition for case {CaseId} produced unreadable JSON.", forensicCase.Id);
            report.Error = $"The model's response could not be read as JSON: {ex.Message}";
        }

        return report;
    }

    private static string BuildTitle(ForensicCase forensicCase)
    {
        var entity = forensicCase.ClubName ?? forensicCase.BrandName;
        entity = string.IsNullOrWhiteSpace(entity) ? forensicCase.EntityKey : entity;
        return $"{forensicCase.Vertical} — {forensicCase.Stage} ({entity})";
    }

    /// <summary>
    /// Queries the knowledge base with what the case is actually about. The query is built in code,
    /// never by the model: the same rule the LogsAgent follows, for the same reason.
    ///
    /// Scoped to the company the case belongs to, because a precedent is a real past incident of a
    /// real customer: handing company B the RCA documents of company A is a data leak, not noise.
    /// </summary>
    private async Task<IReadOnlyList<KnowledgeChunk>> RetrievePrecedentsAsync(ForensicCase forensicCase, int top, CancellationToken ct)
    {
        var query = string.Join(' ', new[]
        {
            forensicCase.Vertical,
            forensicCase.Stage,
            forensicCase.Verdict,
            forensicCase.ProbableRootCause,
        }.Where(part => !string.IsNullOrWhiteSpace(part)));

        if (string.IsNullOrWhiteSpace(query)) return [];

        // An unattributed case cannot be scoped, so it gets the whole corpus - and says so out
        // loud. Silence here is the dangerous failure mode: it looks exactly like a scoped lookup.
        var profileKey = string.IsNullOrWhiteSpace(forensicCase.ProfileKey) ? null : forensicCase.ProfileKey.Trim();
        if (profileKey is null)
        {
            logger.LogWarning(
                "Case {CaseId} carries no company attribution, so its precedents could not be scoped to one company and were retrieved from the whole knowledge base.",
                forensicCase.Id);
        }

        try
        {
            return await knowledgeRetriever.RetrieveAsync(query, top, ct, profileKey);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A knowledge base outage must not stop the RCA; it just means no precedents.
            logger.LogWarning(ex, "Precedent lookup failed for case {CaseId}; composing without precedents.", forensicCase.Id);
            return [];
        }
    }

    private string BuildPrompt(ForensicCase forensicCase, IReadOnlyList<KnowledgeChunk> precedents, CompanyVocabulary vocabulary)
    {
        var sb = new StringBuilder();

        sb.AppendLine("You are writing a post-incident Root Cause Analysis for the leadership of the company described below.");
        sb.AppendLine("Write for executives, not engineers: plain language, no jargon that needs a glossary.");
        sb.AppendLine();
        sb.AppendLine(vocabulary.PromptBlock);
        sb.AppendLine();
        sb.AppendLine("EVERY statement you write must carry a confidence label:");
        sb.AppendLine("  \"fact\"      - measured, or taken from an artifact listed as VERIFIED below.");
        sb.AppendLine("  \"derived\"   - arithmetic over those measurements (durations, counts, percentages).");
        sb.AppendLine("  \"inference\" - your reading, or anything suggested by a past incident. NOT evidence.");
        sb.AppendLine();
        sb.AppendLine("Rules you must not break:");
        sb.AppendLine("- Never label something \"fact\" unless it appears in the evidence below.");
        sb.AppendLine("- This platform stores NO revenue or price data anywhere. Any financial impact is \"inference\".");
        sb.AppendLine("- Incident severity and committed remediation dates are human decisions; label them \"inference\".");
        sb.AppendLine("- Write every section. If evidence is thin, say so plainly in the statement and label it \"inference\".");
        sb.AppendLine();

        sb.AppendLine("=== THE INVESTIGATION ===");
        sb.AppendLine($"{vocabulary.ServiceLabel}: {forensicCase.Vertical}");
        sb.AppendLine($"{vocabulary.StepLabel}: {forensicCase.Stage}");
        sb.AppendLine($"Affected {vocabulary.EntityNoun}: {forensicCase.EntityKey}");
        if (!string.IsNullOrWhiteSpace(forensicCase.BrandName)) sb.AppendLine($"{vocabulary.EntityLabel} name: {forensicCase.BrandName}");
        sb.AppendLine($"Detected at: {forensicCase.StartedAt:u}");
        if (forensicCase.CompletedAt is { } completed) sb.AppendLine($"Investigation finished: {completed:u}");
        sb.AppendLine($"Investigation duration: {forensicCase.DurationMs} ms");
        sb.AppendLine($"Verdict: {forensicCase.Verdict ?? "(none recorded)"}");
        sb.AppendLine($"Probable root cause: {forensicCase.ProbableRootCause ?? "(none established)"}");
        sb.AppendLine($"Decision: {forensicCase.Decision ?? "(none)"}");
        sb.AppendLine($"Confidence: {forensicCase.Confidence:F2}");
        if (!string.IsNullOrWhiteSpace(forensicCase.BlockedBy)) sb.AppendLine($"Blocked by: {forensicCase.BlockedBy}");
        sb.AppendLine($"Citations: {forensicCase.CitationsVerified} of {forensicCase.CitationsTotal} VERIFIED to exist in the real system.");
        if (forensicCase.CitationsVerified == 0)
        {
            sb.AppendLine("WARNING: nothing in this investigation was verified. No statement may be labelled \"fact\".");
        }
        sb.AppendLine();

        AppendIfPresent(sb, "=== WHAT EACH SPECIALIST FOUND ===", forensicCase.BlackboardJson);
        AppendIfPresent(sb, "=== RECONCILED SYNTHESIS ===", forensicCase.SynthesisJson);
        AppendIfPresent(sb, "=== CITED ARTIFACTS AND THEIR VERIFICATION ===", forensicCase.GroundingJson);
        AppendIfPresent(sb, "=== SUGGESTED OPERATOR STEPS ===", forensicCase.RunbookJson);

        if (precedents.Count > 0)
        {
            sb.AppendLine("=== SIMILAR PAST INCIDENTS (context only - these are NOT evidence about this one) ===");
            foreach (var p in precedents)
            {
                var authority = p.SourceType == KnowledgeSourceType.RcaDocument
                    ? "reviewed RCA"
                    : "incident ticket (a reported symptom, not a confirmed cause)";
                sb.AppendLine($"- [{authority}] {p.DocumentTitle} / {p.SectionTitle}");
                sb.AppendLine(Truncate(p.Content, 700));
            }
            sb.AppendLine();
        }

        sb.AppendLine("=== OUTPUT ===");
        sb.AppendLine("Reply with ONLY this JSON, no prose around it:");
        sb.AppendLine("""
            {"sections":[{"title":"<one of the section titles listed below>","statements":[{"text":"...","confidence":"fact|derived|inference"}]}],
             "fiveWhys":[{"level":1,"question":"...","answer":"...","confidence":"fact|derived|inference"}],
             "requiresHuman":["<section titles a person must complete or approve>"]}
            """);
        sb.AppendLine();
        sb.AppendLine("Write these sections, in this order:");
        foreach (var section in RcaTemplate.Sections) sb.AppendLine($"  - {section}");
        sb.AppendLine();
        sb.AppendLine("The 5 Whys chain must have exactly 5 levels, each answering the previous one.");

        return sb.ToString();
    }

    private static void AppendIfPresent(StringBuilder sb, string heading, string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        sb.AppendLine(heading);
        sb.AppendLine(Truncate(json, 4000));
        sb.AppendLine();
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "\n…(truncated)";

    private void Populate(RcaReport report, string content, ForensicCase forensicCase, IReadOnlyList<KnowledgeChunk> precedents)
    {
        using var doc = JsonDocument.Parse(ExtractJson(content));
        var root = doc.RootElement;

        // Nothing in this case was verified, so nothing in the document may claim to be measured.
        // Enforced here rather than trusted to the prompt - a prompt is not a boundary.
        var factAllowed = forensicCase.CitationsVerified > 0;

        var sections = new List<RcaSection>();
        if (root.TryGetProperty("sections", out var sectionsEl) && sectionsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var sectionEl in sectionsEl.EnumerateArray())
            {
                var title = ReadString(sectionEl, "title");
                if (string.IsNullOrWhiteSpace(title)) continue;

                var statements = new List<RcaStatement>();
                if (sectionEl.TryGetProperty("statements", out var statementsEl) && statementsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var statementEl in statementsEl.EnumerateArray())
                    {
                        var text = ReadString(statementEl, "text");
                        if (string.IsNullOrWhiteSpace(text)) continue;

                        statements.Add(new RcaStatement(text!, Downgrade(ReadString(statementEl, "confidence"), factAllowed, text)));
                    }
                }

                if (statements.Count > 0) sections.Add(new RcaSection(title!, statements));
            }
        }

        var whys = new List<RcaWhy>();
        if (root.TryGetProperty("fiveWhys", out var whysEl) && whysEl.ValueKind == JsonValueKind.Array)
        {
            var level = 1;
            foreach (var whyEl in whysEl.EnumerateArray())
            {
                var question = ReadString(whyEl, "question");
                var answer = ReadString(whyEl, "answer");
                if (string.IsNullOrWhiteSpace(question) || string.IsNullOrWhiteSpace(answer)) continue;

                whys.Add(new RcaWhy(level++, question!, answer!, Downgrade(ReadString(whyEl, "confidence"), factAllowed, answer)));
            }
        }

        var requiresHuman = new List<string>();
        if (root.TryGetProperty("requiresHuman", out var humanEl) && humanEl.ValueKind == JsonValueKind.Array)
        {
            requiresHuman.AddRange(humanEl.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!)
                .Where(s => !string.IsNullOrWhiteSpace(s)));
        }

        report.Sections = sections;
        report.FiveWhys = whys;
        report.RequiresHuman = requiresHuman;

        if (sections.Count == 0)
        {
            report.Error = "The model returned no usable sections.";
        }

        // Precedents are reported as RETRIEVED, not as cited: the model cannot add one that the
        // knowledge base did not return, which is the same grounding rule the Inspector applies to
        // artifacts. report.Precedents was already set from the retrieval itself.
        _ = precedents;
    }

    /// <summary>A model asked for JSON sometimes wraps it in prose or a code fence; take the outermost object.</summary>
    private static string ExtractJson(string content)
    {
        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        return start >= 0 && end > start ? content[start..(end + 1)] : content;
    }

    private static string Downgrade(string? confidence, bool factAllowed) =>
        Downgrade(confidence, factAllowed, text: null);

    /// <summary>
    /// Normalises the label, then weakens it where the claim cannot be supported.
    ///
    /// The money rule is enforced here and not left to the prompt. Asked not to, a real run still
    /// produced "No financial or booking impact occurred" labelled as fact - and this platform has
    /// no revenue or price column anywhere in its schema, so that is unknowable by construction.
    /// An RCA that states a financial outcome as measured is the kind of sentence that gets quoted
    /// back in a partner meeting.
    /// </summary>
    private static string Downgrade(string? confidence, bool factAllowed, string? text)
    {
        var normalized = RcaConfidence.NormalizeOrInference(confidence);
        if (normalized != RcaConfidence.Fact) return normalized;

        if (!factAllowed) return RcaConfidence.Inference;

        return MentionsMoney(text) ? RcaConfidence.Inference : RcaConfidence.Fact;
    }

    /// <summary>
    /// Deliberately over-eager: it also catches "the platform stores no revenue data", which IS a
    /// true statement about the system. Weakening a true claim costs a reader nothing; presenting
    /// an unknowable one as measured costs trust.
    /// </summary>
    private static bool MentionsMoney(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        string[] markers =
        [
            "revenue", "financial", "monetary", "dollar", "cost us", "lost sales",
            "booking impact", "bookings lost", "conversion rate", "refund",
        ];

        return markers.Any(m => text.Contains(m, StringComparison.OrdinalIgnoreCase));
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
