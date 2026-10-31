namespace JourneyMonitor.Core.Options;

/// <summary>
/// Red Alert policy configuration (WI 239285, AC6). Every condition of the definition is a named
/// setting here rather than a constant in the policy, so the bar can be retuned without a code
/// change once the candidate rate has been measured against real traffic.
///
/// All values nullable so a missing setting falls back to the documented default instead of
/// silently using zero or false, matching <see cref="InspectorOptions"/> and <see cref="ScoutOptions"/>.
/// </summary>
public sealed class RedAlertOptions
{
    public const string SectionName = "RedAlert";

    /// <summary>
    /// Verticals in pilot scope, comma-separated. Default "hotels,cruises" - the pilot is Hotel and
    /// Cruise only, so anything else may be investigated but must never page a responder.
    /// Compared case-insensitively against a normalised form of the board's vertical label.
    /// </summary>
    public string? Verticals { get; set; }

    /// <summary>
    /// Share of cited artifacts that must verify. Default 0.80. Lower than the bar to <c>resolve</c>
    /// on purpose: a Red Alert hands work to a human who will re-check it, whereas a resolution
    /// claims the matter is closed.
    /// </summary>
    public double? MinVerifiedCitationRatio { get; set; }

    /// <summary>
    /// Whether the club must be identifiable by the id the portal filters on (club id, or partner
    /// id for cruises). Default true.
    ///
    /// Deliberately NOT the brand name: that is a display label shared by about five clubs (the
    /// catalogue holds 43 clubs across 8 brands) and it is populated on 21% of real packages, so
    /// keying on it asked the wrong question and disqualified nearly every case. An alert nobody
    /// can attribute to a club is not routable and is not reachable from the portal's own filter.
    /// </summary>
    public bool? RequireIdentifiableClub { get; set; }

    /// <summary>
    /// Whether a Red Alert requires a recorded owner for the failing service. Default true, and
    /// today the ownership map is empty, so in practice this also keeps the gate shut.
    ///
    /// That is intended. The pilot exit criterion is that every Red Alert RCA carries an owner, and
    /// the named responders are a leadership decision that has not been made. An alert nobody is
    /// accountable for is a notification, not an alert.
    /// </summary>
    public bool? RequireServiceOwner { get; set; }

    /// <summary>
    /// Whether unclassified stages and verticals are barred from Red Alert. Default true, and this
    /// one is not cosmetic: 100% of current rank-3 detections land on "Unmapped operation" and 53%
    /// are also "Shared / unclassified". Allowing them would page a responder for traffic we cannot
    /// name.
    /// </summary>
    public bool? ExcludeUnmappedStages { get; set; }

    /// <summary>
    /// Whether a Red Alert requires the detection to have been confirmed against the entity's own
    /// rolling baseline. Default true, and today nothing produces that confirmation yet, so in
    /// practice nothing reaches Red Alert.
    ///
    /// That is the intended behaviour and not an oversight. The current detection rule compares a
    /// bucket only against the one before it, which fires when a quiet period ends: measured on
    /// stays.tmobiletravel.com, the volume we packaged as "Investigate now" (1683) sits below that
    /// club's own 7-day P95 (2243), because the preceding bucket was a valley. Paging on that is
    /// the single worst failure mode this policy exists to prevent.
    ///
    /// Setting this to false lets alerts fire without that check; the reason recorded on the case
    /// then says so explicitly, so a demo can never pass for a validated alert.
    /// </summary>
    public bool? RequireBaselineConfirmation { get; set; }

    public IReadOnlyList<string> ResolvedVerticals =>
        string.IsNullOrWhiteSpace(Verticals)
            ? ["hotels", "cruises"]
            : Verticals.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public double ResolvedMinVerifiedCitationRatio =>
        MinVerifiedCitationRatio is >= 0 ? MinVerifiedCitationRatio.Value : 0.80;

    public bool ResolvedRequireIdentifiableClub => RequireIdentifiableClub ?? true;

    public bool ResolvedRequireServiceOwner => RequireServiceOwner ?? true;

    public bool ResolvedExcludeUnmappedStages => ExcludeUnmappedStages ?? true;

    public bool ResolvedRequireBaselineConfirmation => RequireBaselineConfirmation ?? true;
}
