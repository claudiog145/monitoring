using System.Text.Json;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Options;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Core.Monitoring.Inspector;

/// <summary>
/// Turns a grounded synthesis into a decision, with arithmetic only.
///
/// Deliberately not a prompt and not another model opinion: resolve-vs-escalate is a policy. It
/// has to be auditable ("why did this one close?"), testable without a provider, and changeable by
/// editing a number in configuration rather than a paragraph of English that a model may or may
/// not honour this week.
///
/// The three outcomes a reader should be able to tell apart afterwards:
/// - <c>resolve</c> needs confidence AND a verified-citation ratio AND at least one citation.
/// - <c>escalate</c> is a real problem this agent cannot close, with BlockedBy saying what is missing.
/// - <c>inconclusive</c> is the honest answer when nothing was cited or nothing verified, and is
///   preferred over a confident guess.
/// </summary>
public sealed class InspectorVerdictService(IOptions<InspectorOptions> options) : IVerdictService
{
    private const int MaxSummaryLength = 1024;
    private const int MaxBlockedByLength = 1024;
    private const int MaxRootCauseLength = 2048;

    public InspectorVerdict Decide(InspectorContext context, string synthesisJson, IReadOnlyList<CitedArtifact> artifacts)
    {
        var limits = options.Value;

        using var doc = InspectorJson.TryParseObject(synthesisJson);
        if (doc is null)
        {
            return InspectorVerdict.Inconclusive(
                "the synthesis could not be read, so there is no conclusion to decide on.");
        }

        var root = doc.RootElement;
        var conclusion = InspectorJson.ReadString(root, "conclusion", "insufficient_evidence").Trim().ToLowerInvariant();
        var summary = Trim(InspectorJson.ReadString(root, "summary", "(the synthesis produced no summary)"), MaxSummaryLength);
        var confidence = Math.Clamp(InspectorJson.ReadDouble(root, "confidence"), 0, 1);
        var unknowns = InspectorJson.ReadStringArray(root, "unknowns");
        var statedBlocker = InspectorJson.ReadString(root, "blockedBy");
        var runbookJson = ReadRunbook(root);
        var rootCause = ReadRootCause(root);

        var total = artifacts.Count;
        var verified = artifacts.Count(a => a.Verification == ArtifactVerification.Verified);
        var fabricated = artifacts.Count(a => a.Verification == ArtifactVerification.NotFound);
        var unverifiable = artifacts.Count(a => a.Verification == ArtifactVerification.NotVerifiable);
        var ratio = total == 0 ? 0 : (double)verified / total;

        if (total == 0)
        {
            return new InspectorVerdict(
                InspectorDecision.Inconclusive,
                summary,
                Math.Min(confidence, 0.3),
                Blocker(statedBlocker, "the synthesis cited no artifact at all, so none of it could be checked against the real system."),
                unknowns,
                null);
        }

        if (verified == 0)
        {
            return new InspectorVerdict(
                InspectorDecision.Inconclusive,
                summary,
                Math.Min(confidence, 0.3),
                Blocker(statedBlocker, DescribeCitations(total, verified, fabricated, unverifiable)
                    + " Nothing it pointed at could be confirmed, so there is no grounded evidence to decide on."),
                unknowns,
                null);
        }

        // "No real problem" is a result, not an absence of one - but only once something it cited
        // was actually confirmed. Closing a case on an unverifiable all-clear is the most expensive
        // kind of wrong this agent can be.
        if (conclusion == "no_issue")
        {
            return new InspectorVerdict(InspectorDecision.CloseNoIssue, summary, confidence, null, unknowns, runbookJson);
        }

        var confidenceOk = confidence >= limits.ResolvedMinConfidenceToResolve;
        var ratioOk = ratio >= limits.ResolvedMinVerifiedCitationRatioToResolve;

        if (conclusion == "root_cause" && confidenceOk && ratioOk)
        {
            return new InspectorVerdict(InspectorDecision.Resolve, summary, confidence, null, unknowns, runbookJson)
            {
                ProbableRootCause = rootCause,
            };
        }

        return new InspectorVerdict(
            InspectorDecision.Escalate,
            summary,
            confidence,
            Blocker(statedBlocker, BuildBlocker(conclusion, confidence, confidenceOk, ratio, ratioOk, total, verified, fabricated, unverifiable, limits)),
            unknowns,
            runbookJson)
        {
            // Carried on an escalation too: naming the likely cause and being able to close the
            // case are different claims, and withholding the first because we cannot make the
            // second is how an investigation ends up looking emptier than it was.
            ProbableRootCause = rootCause,
        };
    }

    /// <summary>The model's own "what is missing" first - it is the specific one. The computed sentence always follows, because it is the one that cannot be omitted by a model that would rather look certain.</summary>
    private static string Blocker(string stated, string computed) =>
        Trim(string.IsNullOrWhiteSpace(stated) ? computed : $"{stated.Trim()} | {computed}", MaxBlockedByLength);

    private static string BuildBlocker(
        string conclusion,
        double confidence,
        bool confidenceOk,
        double ratio,
        bool ratioOk,
        int total,
        int verified,
        int fabricated,
        int unverifiable,
        InspectorOptions limits)
    {
        var reasons = new List<string>();

        if (conclusion != "root_cause")
        {
            reasons.Add($"the synthesis itself concluded \"{conclusion}\" rather than a root cause");
        }
        if (!confidenceOk)
        {
            reasons.Add($"confidence {confidence:0.##} is below the {limits.ResolvedMinConfidenceToResolve:0.##} required to resolve");
        }
        if (!ratioOk)
        {
            reasons.Add($"only {ratio:P0} of the citations verified, below the {limits.ResolvedMinVerifiedCitationRatioToResolve:P0} required to resolve");
        }

        return $"{DescribeCitations(total, verified, fabricated, unverifiable)} Cannot resolve because {string.Join("; ", reasons)}.";
    }

    /// <summary>
    /// Fabricated and unverifiable are reported separately and never summed. One says a model
    /// invented an identifier; the other says we have no index to check it against, which is a
    /// statement about this platform and not about the model.
    /// </summary>
    private static string DescribeCitations(int total, int verified, int fabricated, int unverifiable)
    {
        var parts = new List<string> { $"{verified} of {total} cited artifacts verified" };
        if (fabricated > 0) parts.Add($"{fabricated} did not exist");
        if (unverifiable > 0) parts.Add($"{unverifiable} could not be checked because no index for them exists here");
        return string.Join(", ", parts) + ".";
    }

    /// <summary>An empty runbook array is "no steps the evidence supports", which must not be stored as a list the UI renders as guidance.</summary>
    private static string? ReadRunbook(JsonElement root)
    {
        if (!InspectorKqlGuards.TryGetProperty(root, "runbook", out var runbook)) return null;
        if (runbook.ValueKind != JsonValueKind.Array || runbook.GetArrayLength() == 0) return null;
        return runbook.GetRawText();
    }

    /// <summary>
    /// The Planner is asked for <c>rootCause</c> as "string or null", and null is the answer it is
    /// supposed to give when it does not have one. Blank and whitespace are treated the same way:
    /// an empty root cause rendered as a populated field reads as a conclusion nobody reached.
    /// </summary>
    private static string? ReadRootCause(JsonElement root)
    {
        var value = InspectorJson.ReadString(root, "rootCause");
        return string.IsNullOrWhiteSpace(value) ? null : Trim(value.Trim(), MaxRootCauseLength);
    }

    private static string Trim(string value, int max) => value.Length <= max ? value : value[..max];
}
