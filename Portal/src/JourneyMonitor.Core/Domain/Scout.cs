namespace JourneyMonitor.Core.Domain;

/// <summary>
/// One row of the Scout Monkey's priority board, already parsed out of the raw KQL result.
/// The board itself (Infrastructure/Kql/scout-*-priorities.kql.txt) does the deterministic
/// triage - comparing every bucket against the preceding one and assigning PriorityRank
/// (3 = investigate now, 2 = investigate next, 1 = review, 0 = fewer than 5 requests) - so the
/// Scout never re-derives thresholds in C# or in a prompt.
/// CaseId is "{Source} | {Vertical} | {Entity} | {Stage} | {Bucket}" and is the dedup key;
/// GroupKey is the same without the bucket. EntityKey is "club:{id}" | "host:{host}" |
/// "[host/club unavailable]". GroupPayloadJson is the board's own bag (ops, users, sessions,
/// correlations, paths, operations, slow paths) and RawRowJson keeps every projected column
/// verbatim, so nothing the board measured is dropped on the way to L3.
/// </summary>
public sealed record ScoutBoardRow(
    string Board,
    string CaseId,
    string GroupKey,
    string Vertical,
    string EntityKey,
    string Stage,
    string Source,
    string Priority,
    int PriorityRank,
    string Reason,
    DateTimeOffset WindowFrom,
    DateTimeOffset WindowTo,
    string GroupPayloadJson,
    string RawRowJson);

public static class ScoutBoard
{
    public const string Latency = "latency";
    public const string Exception = "exception";

    public static readonly string[] All = [Latency, Exception];

    /// <summary>Which agent owns a board. One scout per board - see <see cref="MonkeyKey.LatencyScoutMonkey"/>.</summary>
    public static string MonkeyKeyFor(string board) => board == Exception
        ? MonkeyKey.ExceptionScoutMonkey
        : MonkeyKey.LatencyScoutMonkey;
}

/// <summary>
/// Who the <c>Entity</c> column of the board actually refers to. The board emits either
/// "club:{numeric id}" or "host:{host}" depending on whether the telemetry carried a club id,
/// so the same brand can appear under either shape and neither is guaranteed to resolve:
/// measured against brand.Information, only ~10% of the entities seen in a 4h window match a
/// known brand. ResolvedBy is therefore part of the contract - an unresolved club is reported
/// honestly (BrandName null, ResolvedBy "unresolved") rather than guessed.
/// </summary>
public sealed record ClubIdentity(
    string EntityKey,
    string Kind,
    string? ClubId,
    string? IcePartnerId,
    string? BrandName,
    /// <summary>The club's own display name (brand.ClubInventory.ClubName) - BrandName is the chain, this is the specific property.</summary>
    string? ClubName,
    string ResolvedBy)
{
    public bool IsResolved => BrandName is { Length: > 0 };
}

public static class ClubEntityKind
{
    public const string Club = "club";
    public const string Host = "host";
    /// <summary>The board could not attribute the traffic to a club or a host at all.</summary>
    public const string Unknown = "unknown";
}

public static class ClubResolution
{
    public const string ClubIdMatch = "club-id";
    /// <summary>Cruise telemetry has no VacationClubId - its partner id IS the club, so "club:140" is an IcePartnerId.</summary>
    public const string IcePartnerIdMatch = "ice-partner-id";
    public const string HostExactMatch = "host-exact";
    public const string HostBaseDomainMatch = "host-base-domain";
    public const string Unresolved = "unresolved";
}

/// <summary>
/// The Scout's own reading of a board row, kept strictly separate from the board's facts so a
/// reader can always tell inference from measurement. Symptom is one plain-language line;
/// Interpretation is what the evidence suggests, grounded in the paths/operations the board
/// supplied; ShouldEscalate is the Scout's call, and WhyNotEscalate is populated when it vetoes a
/// row the board ranked high enough. When the LLM is unavailable Available is false and the
/// package still ships, deterministic-only - the Scout never fabricates a symptom, the same
/// contract the Doctor Monkey already honours.
/// </summary>
public sealed record ScoutAssessment(
    bool Available,
    string Symptom,
    string Interpretation,
    double Confidence,
    bool ShouldEscalate,
    string? WhyNotEscalate,
    string ModelUsed,
    int TokensIn,
    int TokensOut)
{
    /// <summary>Why no assessment exists (LLM not configured, call failed, response unparseable).</summary>
    public string? UnavailableReason { get; init; }

    public static ScoutAssessment Unavailable(string reason) =>
        new(false, "", "", 0.0, ShouldEscalate: true, WhyNotEscalate: null, "none", 0, 0)
        {
            UnavailableReason = reason,
        };
}

/// <summary>
/// What the assessor produces: the parsed opinion plus the raw call behind it. They travel
/// together because the log is only meaningful next to the result it produced, but they are
/// persisted apart - the assessment is part of the handoff contract, the call is an audit record.
/// </summary>
public sealed record ScoutAssessmentResult(ScoutAssessment Assessment, ScoutLlmCall Call);

/// <summary>
/// One LLM call made while reading a board row: the exact prompt sent and the raw response before
/// any parsing. Same purpose as monitoring.LlmCallLog for the Doctor Monkey - without it, "the
/// Scout said X" is unauditable, and a malformed response is indistinguishable from a bad one.
/// </summary>
public sealed class ScoutLlmCall
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Set once the package it belongs to has been built.</summary>
    public Guid InvestigationPackageId { get; set; }

    public required string PromptText { get; init; }

    /// <summary>Exactly what came back, before JSON extraction. Empty when the provider returned nothing.</summary>
    public required string RawResponseText { get; init; }

    public required string ModelUsed { get; init; }

    public int TokensIn { get; init; }

    public int TokensOut { get; init; }

    public long DurationMs { get; init; }

    /// <summary>Why the call did not produce a usable assessment, when it did not.</summary>
    public string? ErrorMessage { get; init; }

    public DateTimeOffset CalledAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// The individual requests and exceptions behind one board row, plus the query that fetched them
/// so the L3 team can re-run it unchanged. Capped on purpose (see ScoutOptions.MaxRawEvidenceRows)
/// - a busy club can produce hundreds of rows in a single 5-minute bucket, and <see cref="Truncated"/>
/// says so explicitly rather than letting a partial list look complete.
/// Never throws its way out of a cycle: a failed collection is reported in <see cref="Error"/> and
/// the package still ships with the aggregate evidence.
/// </summary>
public sealed record ScoutRawEvidence(string Kql, int RowCount, bool Truncated, string RowsJson, string? Error)
{
    public static ScoutRawEvidence Skipped(string reason) => new("", 0, false, "[]", reason);
}

/// <summary>
/// What the Scout hands to L3: everything Azure had about one problem, at the moment it was
/// detected. This is the contract between the detection phase and the (not yet built) forensics
/// phase - it is deliberately self-contained, so L3 never has to re-run the board to know what
/// the Scout saw.
/// </summary>
public sealed class InvestigationPackage
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Dedup key - the same bucket of the same group is only ever packaged once.</summary>
    public required string CaseId { get; init; }

    /// <summary>
    /// Which company profile classified this package. Required rather than defaulted: two companies
    /// reading the same telemetry produce the same CaseId shape, so an unattributed package is both
    /// unfilterable and a dedup collision waiting to happen - one customer's work would silently
    /// suppress another's.
    /// </summary>
    public required string ProfileKey { get; init; }

    /// <summary>See <see cref="ScoutBoard"/>.</summary>
    public required string Board { get; init; }

    public required string Vertical { get; init; }

    public required string Stage { get; init; }

    public required string Source { get; init; }

    public required string EntityKey { get; init; }

    public required string EntityKind { get; init; }

    public string? ClubId { get; init; }

    public string? IcePartnerId { get; init; }

    /// <summary>Null when the entity does not resolve - never a placeholder brand.</summary>
    public string? BrandName { get; init; }

    /// <summary>The club's own display name, from brand.ClubInventory. Null under the same conditions as <see cref="BrandName"/>.</summary>
    public string? ClubName { get; init; }

    /// <summary>See <see cref="ClubResolution"/>.</summary>
    public required string ClubResolvedBy { get; init; }

    public required string Priority { get; init; }

    public required int PriorityRank { get; init; }

    public required string Reason { get; init; }

    public required DateTimeOffset WindowFrom { get; init; }

    public required DateTimeOffset WindowTo { get; init; }

    /// <summary>
    /// The board's own measurements, verbatim: the full projected row plus its GroupPayload
    /// (ops, users, sessions, correlations, paths, operations, slow paths). Facts only.
    /// </summary>
    public required string DeterministicJson { get; init; }

    /// <summary>The Scout's <see cref="ScoutAssessment"/> as JSON. Inference only - never mixed with the above.</summary>
    public required string AssessmentJson { get; init; }

    /// <summary>
    /// The drill-down query for this exact case, already parameterised - the L3 team can paste it
    /// straight into Azure. Null when raw evidence collection is switched off.
    /// </summary>
    public string? DrillDownKql { get; init; }

    /// <summary>
    /// <see cref="ScoutRawEvidence"/> as JSON: the individual requests/exceptions behind the
    /// aggregate row. Null when collection is switched off.
    /// </summary>
    public string? RawEvidenceJson { get; init; }

    /// <summary>Volume proxy for business importance while brand.Information covers only a fraction of the real clubs.</summary>
    public int RequestCount { get; init; }

    public int FailedRequestCount { get; init; }

    public int ExceptionCount { get; init; }

    /// <summary>Distinct member ids the board could attribute; often 0 - the board cannot always identify users.</summary>
    public int AffectedUserCount { get; init; }

    /// <summary>See <see cref="InvestigationPackageStatus"/>.</summary>
    public string Status { get; set; } = InvestigationPackageStatus.PendingHandoff;

    public required Guid ScoutRunId { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public static class InvestigationPackageStatus
{
    /// <summary>Built and persisted; nothing has picked it up yet. The only status the detection phase produces.</summary>
    public const string PendingHandoff = "pending_handoff";

    /// <summary>Reserved for the forensics phase, which does not exist yet.</summary>
    public const string HandedOff = "handed_off";

    public const string Dismissed = "dismissed";
}

/// <summary>
/// How many times the same group has been packaged, and when. Answers "is this still happening?",
/// which a single 5-minute package cannot.
/// </summary>
public sealed record PackageRecurrence(int Occurrences, DateTimeOffset? FirstSeen, DateTimeOffset? LastSeen)
{
    public static readonly PackageRecurrence None = new(0, null, null);
}

/// <summary>
/// One Scout cycle, recorded whether or not it found anything - "registrar todo OK y seguir
/// vigilando". Without this a quiet cycle is indistinguishable from a cycle that never ran.
/// </summary>
public sealed class ScoutRun
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>
    /// Which board this cycle read - see <see cref="ScoutBoard"/>. A cycle covers exactly one
    /// board since the scouts were split, so this is also what tells the two cards apart.
    /// </summary>
    public required string Board { get; init; }

    /// <summary>
    /// Which company profile this cycle classified under. Settable because it is only known once
    /// the profile resolves, which happens after the run row exists - a run that failed to resolve
    /// a profile still has to be recorded, and recorded as having classified under nothing.
    /// </summary>
    public string ProfileKey { get; set; } = "";

    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>See <see cref="ScoutRunStatus"/>.</summary>
    public string Status { get; set; } = ScoutRunStatus.Running;

    public DateTimeOffset WindowFrom { get; init; }

    public DateTimeOffset WindowTo { get; init; }

    public int BoardRowsEvaluated { get; set; }

    /// <summary>
    /// Distinct clubs/hosts seen on this cycle's board. Surfaced in the UI because "read
    /// 2 670 rows across 868 clubs" is evidence of work, where a status label is not.
    /// </summary>
    public int ClubsWatched { get; set; }

    /// <summary>
    /// What the cycle is doing right now (see <see cref="ScoutRunPhase"/>), updated as it
    /// progresses so an in-flight cycle is visible instead of looking idle. Null once finished.
    /// </summary>
    public string? Phase { get; set; }

    /// <summary>Rows at or above the configured escalation rank, before dedup.</summary>
    public int CandidatesFound { get; set; }

    public int PackagesCreated { get; set; }

    public int PackagesSkippedDuplicate { get; set; }

    /// <summary>Candidates dropped because MaxNewCasesPerRun was reached - makes the cap visible instead of silent.</summary>
    public int PackagesSkippedCap { get; set; }

    /// <summary>Candidates the Scout itself vetoed despite the board ranking them high enough.</summary>
    public int PackagesVetoedByScout { get; set; }

    public long DurationMs { get; set; }

    /// <summary>
    /// The fully substituted KQL of each board this cycle ran, keyed by board name. Kept on the
    /// run rather than on every package because the text is identical for all packages of a
    /// cycle; a package points here through its ScoutRunId.
    /// </summary>
    public string? BoardQueriesJson { get; set; }

    /// <summary>Populated on failure - a board query that cannot run (e.g. expired credentials) must be visible, not silent.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// The CaseIds this cycle skipped because they were already packaged. The count alone cannot
    /// answer "which ones?", which is what a reader asks when a cycle hands off nothing.
    /// </summary>
    public string? SkippedCaseIdsJson { get; set; }
}

public static class ScoutRunStatus
{
    public const string Running = "running";
    /// <summary>Completed and found nothing to escalate.</summary>
    public const string Clear = "clear";
    /// <summary>Completed and produced at least one package.</summary>
    public const string Detected = "detected";
    public const string Failed = "failed";
    /// <summary>The cycle fired but Scout:Enabled is false.</summary>
    public const string Disabled = "disabled";
}

/// <summary>
/// Steps of a cycle, in order. Persisted as the cycle advances so the UI can show what the agent
/// is doing, instead of only that it is busy.
/// </summary>
public static class ScoutRunPhase
{
    public const string LatencyBoard = "latency-board";
    public const string ExceptionBoard = "exception-board";
    public const string Triage = "triage";
    public const string Analysis = "analysis";
    public const string Evidence = "evidence";

    public static string BoardPhaseFor(string board) => board == ScoutBoard.Exception ? ExceptionBoard : LatencyBoard;

    /// <summary>In UI order - the card renders one step per entry. A cycle reads one board, so only that board's phase appears.</summary>
    public static string[] PhasesFor(string board) => [BoardPhaseFor(board), Triage, Analysis, Evidence];

    /// <summary>
    /// Share of a typical cycle, measured: ~48s (latency) or ~45s (exception) on the board,
    /// triage in milliseconds, then ~3s per new package for analysis and evidence. Equal-width
    /// steps would imply the phases cost the same and make the bar look stuck on the board for
    /// most of the run. Analysis and evidence scale with the number of new packages, so these are
    /// the shape of a quiet cycle, not a guarantee.
    /// </summary>
    public static IReadOnlyDictionary<string, int> WeightsFor(string board) => new Dictionary<string, int>
    {
        [BoardPhaseFor(board)] = board == ScoutBoard.Exception ? 45 : 48,
        [Triage] = 1,
        [Analysis] = 3,
        [Evidence] = 3,
    };
}

/// <summary>
/// Cumulative proof of work across cycles. Tokens are part of it on purpose: they are the one
/// number that cannot exist without a model having actually run.
/// </summary>
public sealed record ScoutStats(
    int Cycles,
    int ClearCycles,
    int DetectedCycles,
    int FailedCycles,
    long BoardRowsEvaluated,
    int CandidatesFound,
    int PackagesCreated,
    int DuplicatesAvoided,
    int MaxClubsWatched,
    int LlmCalls,
    long TokensIn,
    long TokensOut,
    DateTimeOffset? FirstCycleAt,
    DateTimeOffset? LastCycleAt)
{
    public static ScoutStats Empty => new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, null, null);
}
