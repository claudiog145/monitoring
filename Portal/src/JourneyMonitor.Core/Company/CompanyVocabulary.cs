using System.Text;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Monitoring;
using Microsoft.Extensions.Logging;

namespace JourneyMonitor.Core.Company;

/// <summary>
/// The words the agents are allowed to put in a prompt for one company, already sanitised and
/// already capped.
///
/// It exists because <see cref="CompanyGlossary"/> and <see cref="CompanyLabels"/> are edited by
/// whoever onboards a customer and then travel, unattended, into an LLM prompt - the same trust
/// level as a wiki page, which this repo already runs through <see cref="PromptSanitizer"/> for
/// exactly this reason. Doing that at each of the dozen call sites is how one of them eventually
/// forgets, so it is done once, here, at construction.
///
/// Three properties are load-bearing and should not be "simplified":
/// - Every field is bounded. An unbounded description is paid for on every single call of every
///   agent, and it is the largest attacker-controlled surface in the prompt.
/// - A value that trips the injection detector is replaced by the neutral default AND recorded in
///   <see cref="Notes"/>. Silently falling back would hide a profile somebody tampered with.
/// - The rendered block says out loud that this is configuration, not evidence. The nouns tell a
///   model what to CALL things; they are never a measurement and must never be cited as one.
/// </summary>
public sealed record CompanyVocabulary
{
    public const int MaxNounLength = 40;
    public const int MaxLabelLength = 40;
    public const int MaxCompanyNameLength = 80;

    /// <summary>One or two sentences. Past that it is documentation, and documentation does not belong in every prompt of every agent.</summary>
    public const int MaxDescriptionLength = 280;

    /// <summary>What <see cref="PromptSanitizer"/> returns instead of text that matched an injection pattern.</summary>
    private const string RedactionPrefix = "[redacted";

    /// <summary>
    /// What the agents say when no profile could be resolved. Deliberately generic rather than
    /// travel-flavoured: a prompt that invents a business domain is worse than one that has none.
    /// </summary>
    public static readonly CompanyVocabulary Default = new()
    {
        ProfileKey = "(none)",
        CompanyName = "the company this platform monitors",
        ServiceNoun = "service",
        FlowNoun = "flow",
        StepNoun = "step",
        EntityNoun = "entity",
        OwnerNoun = "owner",
        ServiceLabel = "Service",
        FlowLabel = "Flow",
        StepLabel = "Step",
        EntityLabel = "Entity",
        OwnerLabel = "Owner",
        Description = "(no product description is on file for this company)",
    };

    /// <summary>Which profile these words came from. Travels into the prompt so a wrong vocabulary is attributable.</summary>
    public required string ProfileKey { get; init; }

    public required string CompanyName { get; init; }

    /// <summary>What this company calls one addressable part of its product.</summary>
    public required string ServiceNoun { get; init; }

    public required string FlowNoun { get; init; }

    public required string StepNoun { get; init; }

    /// <summary>What this company calls the party a problem affects.</summary>
    public required string EntityNoun { get; init; }

    public required string OwnerNoun { get; init; }

    /// <summary>Title-case form used when the noun labels a field in a rendered block.</summary>
    public required string ServiceLabel { get; init; }

    public required string FlowLabel { get; init; }

    public required string StepLabel { get; init; }

    public required string EntityLabel { get; init; }

    public required string OwnerLabel { get; init; }

    public required string Description { get; init; }

    /// <summary>What had to be rejected or rewritten to make the profile safe to send. Empty on a well-formed profile.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>
    /// The block every agent prepends to its prompt. Ends by stating that none of this is
    /// evidence - a model handed a product description will otherwise happily reason from it and
    /// present the result as something the telemetry showed.
    /// </summary>
    public string PromptBlock
    {
        get
        {
            var sb = new StringBuilder();
            sb.AppendLine("ABOUT THE COMPANY THIS PLATFORM MONITORS (configuration, not evidence):");
            sb.AppendLine($"- Company: {CompanyName} (profile \"{ProfileKey}\")");
            sb.AppendLine($"- What it does: {Description}");
            sb.AppendLine(
                $"- It calls one addressable part of its product a \"{ServiceNoun}\", a measured journey through it a \"{FlowNoun}\", " +
                $"one point in that journey a \"{StepNoun}\", the party a problem affects a \"{EntityNoun}\", " +
                $"and the team accountable for fixing it the \"{OwnerNoun}\".");
            foreach (var note in Notes)
            {
                sb.AppendLine($"- NOTE: {note}");
            }
            sb.Append("These words tell you what to CALL things. They are configuration, never a measurement, and nothing here may be cited as evidence.");
            return sb.ToString();
        }
    }

    public static CompanyVocabulary From(CompanyProfile profile) =>
        Build(profile.ProfileKey, profile.DisplayName, profile.Labels, profile.Glossary);

    /// <summary>
    /// The adapter seam carries no display name, so the profile key stands in for it and the
    /// company is identified in prose by its description instead. Inventing a prettier name for a
    /// prompt is the same class of mistake as inventing a brand - which is why no caller splices
    /// <see cref="CompanyName"/> into an English sentence; they emit <see cref="PromptBlock"/>.
    /// </summary>
    public static CompanyVocabulary From(ICompanyAdapter adapter) =>
        Build(adapter.ProfileKey, adapter.ProfileKey, adapter.Labels, adapter.Glossary);

    /// <summary>
    /// The vocabulary for the default profile, or <see cref="Default"/> when there is not one.
    ///
    /// Deliberately does NOT propagate the provider's exception, unlike every other caller of it.
    /// <c>CompanyAdapterProvider</c> throws loudly because a board classified against a missing
    /// profile is indistinguishable from a healthy board - but that argument is about
    /// CLASSIFICATION. This is about what to call things in a sentence, and failing an entire
    /// investigation over a label would turn a cosmetic misconfiguration into an outage of the
    /// thing that detects outages.
    /// </summary>
    public static async Task<CompanyVocabulary> ResolveAsync(
        ICompanyAdapterProvider? adapters,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        if (adapters is null) return Default;

        try
        {
            return From(await adapters.GetAsync(null, ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "[Company] no company profile resolved; the agents will use the neutral vocabulary.");
            return Default;
        }
    }

    private static CompanyVocabulary Build(string profileKey, string? displayName, CompanyLabels labels, CompanyGlossary glossary)
    {
        var notes = new List<string>();

        return new CompanyVocabulary
        {
            ProfileKey = Phrase(profileKey, Default.ProfileKey, MaxCompanyNameLength, "profile key", notes),
            CompanyName = Phrase(displayName, Default.CompanyName, MaxCompanyNameLength, "company name", notes),
            ServiceNoun = Phrase(glossary.ServiceNoun, Default.ServiceNoun, MaxNounLength, "service noun", notes),
            FlowNoun = Phrase(glossary.FlowNoun, Default.FlowNoun, MaxNounLength, "flow noun", notes),
            StepNoun = Phrase(glossary.StepNoun, Default.StepNoun, MaxNounLength, "step noun", notes),
            EntityNoun = Phrase(glossary.EntityNoun, Default.EntityNoun, MaxNounLength, "entity noun", notes),
            OwnerNoun = Phrase(glossary.OwnerNoun, Default.OwnerNoun, MaxNounLength, "owner noun", notes),
            ServiceLabel = Phrase(labels.Service, Default.ServiceLabel, MaxLabelLength, "service label", notes),
            FlowLabel = Phrase(labels.Flow, Default.FlowLabel, MaxLabelLength, "flow label", notes),
            StepLabel = Phrase(labels.Step, Default.StepLabel, MaxLabelLength, "step label", notes),
            EntityLabel = Phrase(labels.Entity, Default.EntityLabel, MaxLabelLength, "entity label", notes),
            OwnerLabel = Phrase(labels.Owner, Default.OwnerLabel, MaxLabelLength, "owner label", notes),
            Description = Phrase(glossary.Description, Default.Description, MaxDescriptionLength, "description", notes),
            Notes = notes,
        };
    }

    /// <summary>
    /// Sanitises one profile value into something that can be dropped mid-sentence into a prompt.
    ///
    /// Three outcomes, all deliberate: blank falls back silently (an unset field is not an
    /// incident), an injection falls back WITH a note (a tampered field is), and anything longer
    /// than the cap keeps <see cref="PromptSanitizer"/>'s visible truncation marker so a reader
    /// can tell a short description from a cut one.
    /// </summary>
    private static string Phrase(string? value, string fallback, int maxLength, string role, List<string> notes)
    {
        var cleaned = PromptSanitizer.Sanitize(value, maxLength, whenEmpty: "");
        if (cleaned.Length == 0) return fallback;

        if (cleaned.StartsWith(RedactionPrefix, StringComparison.Ordinal))
        {
            notes.Add($"the profile's {role} was rejected as a possible prompt injection and replaced with \"{fallback}\".");
            return fallback;
        }

        var flattened = Flatten(cleaned);
        if (flattened.Length == 0) return fallback;

        if (!string.Equals(flattened, cleaned, StringComparison.Ordinal))
        {
            notes.Add($"the profile's {role} contained line breaks or code fences, which were stripped.");
        }

        return flattened;
    }

    /// <summary>
    /// One line, no fences. A noun or a two-sentence description has no legitimate use for a
    /// newline or a backtick, and both of them are how text smuggles structure into a prompt that
    /// is otherwise plain prose.
    /// </summary>
    private static string Flatten(string value)
    {
        var sb = new StringBuilder(value.Length);
        var lastWasSpace = false;

        foreach (var c in value)
        {
            var normalized = c is '\n' or '\r' or '\t' or '`' ? ' ' : c;
            if (normalized == ' ')
            {
                if (lastWasSpace || sb.Length == 0) continue;
                lastWasSpace = true;
                sb.Append(' ');
                continue;
            }

            lastWasSpace = false;
            sb.Append(normalized);
        }

        return sb.ToString().TrimEnd();
    }
}
