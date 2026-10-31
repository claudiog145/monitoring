using System.Text.RegularExpressions;
using JourneyMonitor.Core.Configuration;

namespace JourneyMonitor.Core.Onboarding;

/// <summary>
/// Read-only profile of one Application Insights resource, produced during onboarding so the
/// question "can this resource even tell us WHO was affected?" is answered on day one instead of
/// months in. In Arrivia's own production 79% of observed entities could not be attributed to a
/// customer, and that was discovered late; this component exists to make that number visible
/// before any mapping work is committed.
///
/// Measurement only. It proposes nothing and calls no model: turning these numbers into a
/// <see cref="Company.CompanyProfile"/> is a later phase, and mixing the two would make it
/// impossible to tell a measured fact from a generated guess.
/// </summary>
public interface ITelemetryResourceProfiler
{
    Task<TelemetryResourceProfile> ProfileAsync(TelemetryProfileRequest request, CancellationToken ct = default);
}

/// <summary>
/// Outcome of a single probe query. The three failure-ish states are deliberately NOT collapsed
/// into "empty list": "the table holds nothing", "this resource has no such table" and "we could
/// not ask" demand opposite responses from whoever is onboarding the customer, and a caller that
/// only sees an empty list will read all three as "all clear".
/// </summary>
public enum TelemetryProbeState
{
    /// <summary>The query ran and returned at least one row.</summary>
    Ok,

    /// <summary>The query ran against an existing table and the window held zero rows.</summary>
    NoData,

    /// <summary>The query was rejected because the table does not exist in this resource.</summary>
    TableAbsent,

    /// <summary>The query could not be completed (auth, throttling, timeout, syntax, transport).</summary>
    Failed,

    /// <summary>Not attempted, because an earlier probe already showed there was nothing to ask about.</summary>
    Skipped,
}

/// <summary>Report-level outcome. <see cref="Degraded"/> means at least one probe failed, so the absence of a finding is not evidence of absence.</summary>
public enum TelemetryProfileStatus
{
    Complete,
    Empty,
    Degraded,
    SourceUnresolved,
}

/// <summary>
/// Caps on what a probe may ask for. A separate type because a record's primary-constructor
/// defaults cannot reference constants declared in its own body.
/// </summary>
public static class TelemetryProfileDefaults
{
    public const int WindowHours = 24;
    public const int MinWindowHours = 1;
    public const int MaxWindowHours = 168;
    public const int MaxSampledRows = 50_000;
    public const int MinSampledRows = 100;
    public const int MaxSampledRowsCeiling = 200_000;
    public const int MaxDimensionKeys = 200;
    public const int MaxHostRows = 200;
}

/// <summary>
/// What to probe. Every cap is clamped by <see cref="Normalize"/> and echoed back on the report:
/// a probe must never be able to turn into an expensive query, and the reader has to be able to
/// see which cap the numbers were measured under.
/// </summary>
public sealed record TelemetryProfileRequest(
    string Alias,
    string Environment = InsightsEnvironment.Production,
    int WindowHours = TelemetryProfileDefaults.WindowHours,
    DateTimeOffset? WindowTo = null,
    IReadOnlyList<string>? Tables = null,
    int MaxSampledRows = TelemetryProfileDefaults.MaxSampledRows,
    int MaxDimensionKeys = TelemetryProfileDefaults.MaxDimensionKeys,
    int MaxHostRows = TelemetryProfileDefaults.MaxHostRows)
{
    public TelemetryProfileRequest Normalize() => this with
    {
        Alias = (Alias ?? "").Trim(),
        WindowHours = Math.Clamp(WindowHours, TelemetryProfileDefaults.MinWindowHours, TelemetryProfileDefaults.MaxWindowHours),
        MaxSampledRows = Math.Clamp(MaxSampledRows, TelemetryProfileDefaults.MinSampledRows, TelemetryProfileDefaults.MaxSampledRowsCeiling),
        MaxDimensionKeys = Math.Clamp(MaxDimensionKeys, 1, 1_000),
        MaxHostRows = Math.Clamp(MaxHostRows, 1, 1_000),
    };
}

/// <summary>
/// The tables a probe is allowed to touch. An allow-list rather than a sanitiser because the
/// table name is concatenated into KQL text: anything else would be a query-injection point on an
/// endpoint whose whole job is to accept a caller-supplied target.
/// </summary>
public static class TelemetryProbeTables
{
    public const string Requests = "requests";
    public const string Exceptions = "exceptions";
    public const string PageViews = "pageViews";
    public const string CustomEvents = "customEvents";
    public const string Dependencies = "dependencies";
    public const string Traces = "traces";

    public static IReadOnlyList<string> Default { get; } =
        [Requests, Exceptions, PageViews, CustomEvents, Dependencies, Traces];

    /// <summary>Tables that carry a <c>url</c> column, so a host can be parsed out of them.</summary>
    public static IReadOnlyList<string> HostBearing { get; } = [Requests, PageViews];

    /// <summary>Returns the canonically-cased table name, or null when the name is not on the allow-list.</summary>
    public static string? Canonical(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var trimmed = name.Trim();
        return Default.FirstOrDefault(t => t.Equals(trimmed, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// Validates the alias before it is written into <c>{{SOURCE:alias}}</c>. Same character class
/// <see cref="KqlSourceResolver.PlaceholderPattern"/> accepts - an alias outside it either fails
/// to substitute (leaving a literal placeholder in the query) or, with the wrong characters,
/// closes the <c>app('...')</c> call and appends operators of the caller's choosing.
/// </summary>
public static class TelemetrySourceAlias
{
    private static readonly Regex Allowed = new(@"^[A-Za-z0-9._-]{1,64}$", RegexOptions.Compiled);

    public static bool IsValid(string? alias) => !string.IsNullOrWhiteSpace(alias) && Allowed.IsMatch(alias.Trim());
}

public sealed record TelemetryResourceProfile(
    string Alias,
    string Environment,
    DateTimeOffset WindowFrom,
    DateTimeOffset WindowTo,
    int WindowHours,
    int MaxSampledRows,
    TelemetryProfileStatus Status,
    /// <summary>Set only when <see cref="Status"/> is <see cref="TelemetryProfileStatus.SourceUnresolved"/>; nothing was queried.</summary>
    string? Error,
    IReadOnlyList<TelemetryTableProfile> Tables,
    /// <summary>Ranked across every probed table. Never filtered by score - see <see cref="EntityIdentityCandidate"/>.</summary>
    IReadOnlyList<EntityIdentityCandidate> IdentityCandidates,
    TelemetryHostProfile Hosts,
    /// <summary>Plain-language notes a human needs in order to read the numbers correctly (sampling hit the cap, a table could not be asked, nothing was attributable at all).</summary>
    IReadOnlyList<string> Warnings,
    DateTimeOffset GeneratedAt);

public sealed record TelemetryTableProfile(
    string Table,
    TelemetryProbeState RowCountState,
    long RowCount,
    string? RowCountError,
    string RowCountKql,
    TelemetryProbeState DimensionState,
    /// <summary>Denominator of every coverage percentage on this table: rows actually examined, which is <c>min(RowCount, MaxSampledRows)</c>.</summary>
    long SampledRows,
    IReadOnlyList<CustomDimensionProfile> Dimensions,
    string? DimensionError,
    string DimensionKql);

/// <summary>
/// One <c>customDimensions</c> key as measured, with nothing dropped. A key present on 4% of rows
/// comes back as a 4% key: the unwelcome number is the product.
/// </summary>
public sealed record CustomDimensionProfile(
    string Key,
    /// <summary>Sampled rows where the key is present at all, including rows whose value is empty.</summary>
    long OccurrenceCount,
    /// <summary>Sampled rows where the key is present AND carries a non-empty value.</summary>
    long NonEmptyCount,
    /// <summary>Approximate (<c>dcount</c> accuracy 1) - good enough to tell a per-row id from a tenant id, not an exact count.</summary>
    long DistinctValueCount,
    int MaxValueLength,
    double CoveragePercent,
    double NonEmptyCoveragePercent);

/// <summary>
/// A <c>customDimensions</c> key ranked as a possible answer to "who is affected", ready to
/// become an <see cref="Company.EntityIdentitySource"/> entry.
///
/// <see cref="EntityKind"/> is deliberately absent: what the value identifies (a club, a partner,
/// a tenant) is a judgement about the customer's business, not a measurement, and this component
/// only measures. <see cref="Reasons"/> carries the arithmetic so a low score can be argued with.
/// </summary>
public sealed record EntityIdentityCandidate(
    string Table,
    string Key,
    /// <summary><see cref="Company.EntityIdentitySourceKind"/>.</summary>
    string Kind,
    double Score,
    double CoveragePercent,
    double NonEmptyCoveragePercent,
    long NonEmptyCount,
    long DistinctValueCount,
    int MaxValueLength,
    bool LooksFreeText,
    IReadOnlyList<string> Reasons);

public sealed record TelemetryHostProfile(
    TelemetryProbeState State,
    string? Error,
    string Kql,
    /// <summary>Candidates for <see cref="Company.ServiceDefinition.HostFilters"/>.</summary>
    IReadOnlyList<ObservedHost> Hosts,
    IReadOnlyList<ObservedRoleName> RoleNames);

public sealed record ObservedHost(string Host, long ObservationCount);

public sealed record ObservedRoleName(string RoleName, long ObservationCount);
