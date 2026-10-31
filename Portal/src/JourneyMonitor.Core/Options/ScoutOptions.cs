namespace JourneyMonitor.Core.Options;

/// <summary>
/// Configuration of one scout. Bound twice as NAMED options - once per board - so the latency and
/// the exception scout each get their own window, cadence, per-cycle cap and minimum rank. A
/// single shared section meant the two boards split one budget, and whichever ran first could
/// spend all of it.
///
/// All values nullable so a missing setting falls back to the documented default instead of
/// silently using zero, matching <see cref="MonitoringOptions"/>.
/// </summary>
public sealed class ScoutOptions
{
    /// <summary>Config section of the latency scout. The options instance is named <c>ScoutBoard.Latency</c>.</summary>
    public const string LatencySectionName = "ScoutLatency";

    /// <summary>Config section of the exception scout. The options instance is named <c>ScoutBoard.Exception</c>.</summary>
    public const string ExceptionSectionName = "ScoutException";

    /// <summary>
    /// Master switch for the timer. Default true. Set to false to stop the cycle without a
    /// redeploy - the run is still recorded (status "disabled") so a silenced Scout is visible
    /// rather than indistinguishable from a dead one.
    /// </summary>
    public bool? Enabled { get; set; }

    /// <summary>
    /// How far back each board query looks. Default 4 (the Azure workbook's own range). Each
    /// query scans this window across three Application Insights resources and takes ~40s, so
    /// raising it has a real cost; 0.25 (15 min) is the cheaper alternative kept available.
    /// </summary>
    public double? WindowHours { get; set; }

    /// <summary>Bucket size the board compares against the preceding bucket. Default 5.</summary>
    public int? BucketMinutes { get; set; }

    /// <summary>
    /// Rows kept per club per board. Default 3. The workbook keeps 10 per vertical globally,
    /// which lets the highest-volume club crowd out everyone else - measured on real data, that
    /// hid 112 clubs with rank&gt;=2 problems. Raising this past 3 roughly doubles volume while
    /// barely moving the rank=3 count.
    /// </summary>
    public int? TakePerClub { get; set; }

    /// <summary>"All" or a single vertical (Hotels | Cars | Cruise | Shared / unclassified). Default "All".</summary>
    public string? Vertical { get; set; }

    /// <summary>"*" for every club, or a single club id / host. Default "*".</summary>
    public string? ClubFilter { get; set; }

    /// <summary>
    /// Minimum board PriorityRank that produces an InvestigationPackage. Default 3
    /// ("Investigate now") in EVERY environment, production included: rank 2 yields ~350
    /// candidates per cycle and must not be switched on until the real per-investigation cost has
    /// been measured.
    /// </summary>
    public int? InvestigationMinPriorityRank { get; set; }

    /// <summary>
    /// Hard cap on packages created in a single cycle. Default 25. Protects the very first cycle
    /// (and any cycle after a long outage), where the whole window is new and nothing has been
    /// deduped yet. Candidates dropped by the cap are counted on the run, never discarded quietly.
    /// Per scout since the split - the two boards no longer share one budget.
    /// </summary>
    public int? MaxNewCasesPerRun { get; set; }

    /// <summary>
    /// Whether the Scout asks the LLM to read each candidate. Default true. When false - or when
    /// the LLM is unavailable - packages still ship with the board's deterministic evidence and
    /// an explicitly unavailable assessment.
    /// </summary>
    public bool? UseLlmAssessment { get; set; }

    /// <summary>
    /// Whether a Scout veto can stop a package from being created. Default false: the Scout's
    /// opinion is recorded on every package, but while the assessment is unproven the board's
    /// deterministic rank is what decides. Flip to true only once the veto has been evaluated
    /// against real cases.
    /// </summary>
    public bool? AllowScoutVeto { get; set; }

    /// <summary>
    /// Which <c>Insights</c> configuration entry supplies the app id/tenant used as the ENTRY
    /// POINT of the board's cross-resource union. Default "hotels" (the SOR app). The board text
    /// itself names the other resources by full ARM path, so this only decides which app the
    /// query is submitted to - not which data it covers.
    /// </summary>
    public string? EntryDomain { get; set; }

    /// <summary>Environment of that same Insights entry. Default "production" - the board's embedded resource paths are production.</summary>
    public string? EntryEnvironment { get; set; }

    /// <summary>
    /// Which <c>monitoring.CompanyProfile</c> row supplies this scout's vocabulary: which service
    /// and step a board row belongs to, and how to tell who is affected. Per board like every
    /// other setting here, so one scout can be moved onto a new profile without moving the other.
    ///
    /// Blank falls through to <c>Company:DefaultProfileKey</c>, and if that is blank too the cycle
    /// FAILS rather than running unclassified: a board on which nothing classifies is
    /// indistinguishable from a healthy one, which is the one outcome that must never be possible.
    /// </summary>
    public string? CompanyProfileKey { get; set; }

    /// <summary>
    /// Whether each package also carries the individual requests/exceptions behind its board row.
    /// Default true. Costs one extra (cheap, filtered) query per package - measured at ~4s, so a
    /// cycle of 14 packages grows by roughly a minute. Turn off if a cycle starts overlapping the
    /// timer.
    /// </summary>
    public bool? IncludeRawEvidence { get; set; }

    /// <summary>
    /// Cap on raw evidence rows per package. Default 200. A busy club can produce hundreds of rows
    /// in a single 5-minute bucket; hitting the cap is reported explicitly rather than silently
    /// shipping a partial list that looks complete.
    /// </summary>
    public int? MaxRawEvidenceRows { get; set; }

    public bool ResolvedEnabled => Enabled ?? true;

    public double ResolvedWindowHours => WindowHours is > 0 ? WindowHours.Value : 4;

    public int ResolvedBucketMinutes => BucketMinutes is > 0 ? BucketMinutes.Value : 5;

    public int ResolvedTakePerClub => TakePerClub is > 0 ? TakePerClub.Value : 3;

    public string ResolvedVertical => string.IsNullOrWhiteSpace(Vertical) ? "All" : Vertical.Trim();

    public string ResolvedClubFilter => string.IsNullOrWhiteSpace(ClubFilter) ? "*" : ClubFilter.Trim();

    public int ResolvedInvestigationMinPriorityRank => InvestigationMinPriorityRank is > 0 ? InvestigationMinPriorityRank.Value : 3;

    public int ResolvedMaxNewCasesPerRun => MaxNewCasesPerRun is > 0 ? MaxNewCasesPerRun.Value : 25;

    public bool ResolvedUseLlmAssessment => UseLlmAssessment ?? true;

    public bool ResolvedAllowScoutVeto => AllowScoutVeto ?? false;

    public string ResolvedEntryDomain => string.IsNullOrWhiteSpace(EntryDomain) ? "hotels" : EntryDomain.Trim();

    public string ResolvedEntryEnvironment => string.IsNullOrWhiteSpace(EntryEnvironment) ? "production" : EntryEnvironment.Trim();

    /// <summary>Null, not "", when nothing is configured - that is what asks the provider for its own default.</summary>
    public string? ResolvedCompanyProfileKey => string.IsNullOrWhiteSpace(CompanyProfileKey) ? null : CompanyProfileKey.Trim();

    public bool ResolvedIncludeRawEvidence => IncludeRawEvidence ?? true;

    public int ResolvedMaxRawEvidenceRows => MaxRawEvidenceRows is > 0 ? MaxRawEvidenceRows.Value : 200;
}
