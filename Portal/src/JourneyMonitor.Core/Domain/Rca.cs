namespace JourneyMonitor.Core.Domain;

/// <summary>
/// How much weight a single statement in an RCA carries. The same three-state vocabulary the Scout
/// already uses on its evidence, reused on purpose: the team has seen it before, and an RCA goes to
/// business - sometimes to a partner - so a reader must be able to tell what they can quote.
/// </summary>
public static class RcaConfidence
{
    /// <summary>Measured, or taken from an artifact the Inspector's grounding verified exists.</summary>
    public const string Fact = "fact";

    /// <summary>Arithmetic over what was measured (durations, counts, percentages).</summary>
    public const string Derived = "derived";

    /// <summary>A model's reading, including anything suggested by a similar past incident. Not evidence.</summary>
    public const string Inference = "inference";

    public static readonly IReadOnlyList<string> All = [Fact, Derived, Inference];

    public static bool IsKnown(string? value) =>
        value is not null && All.Any(v => string.Equals(v, value.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Anything unrecognised becomes <see cref="Inference"/>, never <see cref="Fact"/>: when the
    /// label is in doubt the claim must get weaker, not stronger.
    /// </summary>
    public static string NormalizeOrInference(string? value) =>
        IsKnown(value) ? value!.Trim().ToLowerInvariant() : Inference;
}

/// <summary>One sentence of an RCA, carrying its own epistemic label so the document can be complete without being uniformly trusted.</summary>
public sealed record RcaStatement(string Text, string Confidence);

/// <summary>One section of the corporate RCA template.</summary>
public sealed record RcaSection(string Title, IReadOnlyList<RcaStatement> Statements);

/// <summary>
/// One rung of the 5 Whys chain. Present in 40 of the 40 reviewed RCAs, which is why it is modelled
/// explicitly rather than as free text: it is the part a reader follows to judge the reasoning.
/// </summary>
public sealed record RcaWhy(int Level, string Question, string Answer, string Confidence);

/// <summary>
/// A past incident the knowledge base surfaced while composing. Carries its
/// <see cref="SourceType"/> because a reviewed RCA and an exported ticket are not equally
/// authoritative - one states a conclusion, the other a reported symptom.
/// </summary>
public sealed record RcaPrecedent(string Document, string Section, string? Domain, string SourceType);

/// <summary>
/// The composed RCA. Deliberately NOT a blob of markdown: the structure is what lets the UI show
/// each statement's label, and what lets a test assert that an unevidenced case cannot produce
/// statements labelled as fact.
/// </summary>
public sealed class RcaReport
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public required Guid ForensicCaseId { get; init; }

    public required string Title { get; init; }

    public string? Domain { get; init; }

    public string? Stage { get; init; }

    public IReadOnlyList<RcaSection> Sections { get; set; } = [];

    public IReadOnlyList<RcaWhy> FiveWhys { get; set; } = [];

    public IReadOnlyList<RcaPrecedent> Precedents { get; set; } = [];

    /// <summary>
    /// Sections the agent could not support and left for a human (financial impact, agreed
    /// severity, committed action items). Named explicitly so an unfilled section reads as a task
    /// rather than as an oversight.
    /// </summary>
    public IReadOnlyList<string> RequiresHuman { get; set; } = [];

    public int LlmCalls { get; set; }

    public int TokensIn { get; set; }

    public int TokensOut { get; set; }

    public string ModelUsed { get; set; } = "none";

    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Set when composition failed. A failed RCA is reported, never replaced with a plausible-looking one.</summary>
    public string? Error { get; set; }

    public bool IsAvailable => Error is null && Sections.Count > 0;
}

/// <summary>
/// The section titles measured across the 40 reviewed RCAs rather than invented. The first six
/// appear in 40 of 40; the next three in 38 of 40. Anything rarer was left out - a template is
/// only useful if it reflects what this organisation actually writes.
/// </summary>
public static class RcaTemplate
{
    public const string IncidentSnapshot = "Incident Snapshot";
    public const string BusinessImpact = "Business Impact";
    public const string RootCausePlainLanguage = "Root Cause — Plain Language";
    public const string WhatWentWrong = "What Went Wrong";
    public const string TimelineOfEvents = "Timeline of Events";
    public const string HowItWasResolved = "How It Was Resolved";
    public const string WhatLeadershipShouldKnow = "What Leadership Should Know";
    public const string PreventiveMeasures = "Preventive Measures";

    /// <summary>Ordered as the reviewed documents order them - snapshot first, leadership last.</summary>
    public static readonly IReadOnlyList<string> Sections =
    [
        IncidentSnapshot,
        BusinessImpact,
        RootCausePlainLanguage,
        WhatWentWrong,
        TimelineOfEvents,
        HowItWasResolved,
        PreventiveMeasures,
        WhatLeadershipShouldKnow,
    ];
}
