namespace JourneyMonitor.Core.Company;

/// <summary>
/// How an answer was reached. Part of every resolution contract in this namespace, for the same
/// reason <see cref="Abstractions.ResolvedServiceOwner.ResolvedBy"/> carries it: an exact match and
/// a wildcard fallback are both "an answer", but only one of them means somebody actually mapped
/// this specific thing.
///
/// Strings rather than an enum, matching <c>TelemetrySourceKind</c>: a new strategy should be a
/// value, not a deployment.
/// </summary>
public static class CompanyResolution
{
    /// <summary>A rule named this exact thing.</summary>
    public const string ExactMatch = "exact-match";

    /// <summary>A matcher pattern (url/operation/page) claimed it.</summary>
    public const string PatternMatch = "pattern-match";

    /// <summary>A catch-all rule for the service claimed it. Weaker than a named rule, and the caller must be able to tell.</summary>
    public const string FallbackWildcard = "fallback-wildcard";

    /// <summary>Nothing in the profile covers this. THEIR gap: the telemetry carries something the profile never described.</summary>
    public const string Unresolved = "unresolved";

    /// <summary>The profile itself could not be read. OUR outage, never to be reported as "unresolved" - see the remark on <see cref="StepMatch"/>.</summary>
    public const string Unavailable = "unavailable";
}

/// <summary>
/// Which step of which flow a telemetry row belongs to.
///
/// <see cref="CompanyResolution.Unavailable"/> and <see cref="CompanyResolution.Unresolved"/> are
/// deliberately distinct and must never be collapsed: "we could not read the profile" is our
/// outage and should be fixed by us, "the profile does not describe this URL" is a mapping gap and
/// should be fixed by whoever onboarded the company. Reporting the first as the second sends the
/// wrong person to look, which is the same failure <c>ServiceOwnerResolver</c> already guards
/// against.
/// </summary>
public sealed record StepMatch(
    bool IsResolved,
    string ResolvedBy,
    string? Service,
    string? Flow,
    string? Step)
{
    public static StepMatch Resolved(string service, string? flow, string step, string resolvedBy) =>
        new(true, resolvedBy, service, flow, step);

    public static StepMatch NotResolved(string? service = null) =>
        new(false, CompanyResolution.Unresolved, service, null, null);

    public static StepMatch Unavailable(string? service = null) =>
        new(false, CompanyResolution.Unavailable, service, null, null);
}

/// <summary>
/// Who is affected. The counterpart of <see cref="Abstractions.ResolvedServiceOwner"/>, which says
/// who fixes it; conflating the two is what routed code defects at brand managers before
/// <c>ServiceOwnerResolver</c> split them.
///
/// <see cref="SourceKey"/> records WHICH identity source produced the value (a custom dimension
/// name, a query parameter, the host). A company usually has several, tried in order, and an
/// identity found by the fourth fallback is not worth the same as one stamped explicitly by the
/// application - the caller has to be able to tell them apart without re-deriving it.
/// </summary>
public sealed record EntityIdentity(
    bool IsResolved,
    string ResolvedBy,
    string? Value,
    string? Kind,
    string? SourceKey,
    string? DisplayLabel)
{
    public static EntityIdentity Resolved(string value, string kind, string sourceKey, string resolvedBy, string? displayLabel = null) =>
        new(true, resolvedBy, value, kind, sourceKey, displayLabel);

    public static EntityIdentity NotResolved(string? rawKey = null) =>
        new(false, CompanyResolution.Unresolved, rawKey, null, null, null);

    public static EntityIdentity Unavailable() =>
        new(false, CompanyResolution.Unavailable, null, null, null, null);
}
