using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Options;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Core.Monitoring.Inspector;

/// <summary>
/// Decides whether a finished investigation is a Red Alert, Amber or Informational (WI 239285, AC6).
///
/// Arithmetic and string comparison only. Who gets paged is a policy, so it has to be auditable
/// ("why did this one page me at 3am?"), testable without a provider, and retunable by editing a
/// setting rather than a paragraph of English in a prompt.
///
/// The checks run in a fixed order and the first failure decides, because the order is what makes
/// the recorded reason useful: scope and classification are cheap statements about the case,
/// evidence quality is about the investigation, and the baseline confirmation is checked last on
/// purpose - a case that fails only on that one is a case where everything else qualified, which
/// is exactly the population AC6 needs counted before its delivery commitment becomes binding.
///
/// What this class does NOT do, and must not be read as doing: the detection-side conditions of
/// the definition (above the entity's own rolling baseline, sustained across buckets, affected
/// sessions above a floor) are evaluated where the detection happens, not here. Until that exists,
/// <see cref="RedAlertOptions.ResolvedRequireBaselineConfirmation"/> keeps the gate shut.
/// </summary>
public sealed class RedAlertPolicy(IOptions<RedAlertOptions> options) : IRedAlertPolicy
{
    /// <summary>The board's label for traffic its classifier has no name for. Not a vertical anyone owns.</summary>
    private const string UnclassifiedVertical = "shared / unclassified";

    /// <summary>The board's label for a URL the stage classifier does not cover.</summary>
    private const string UnmappedStage = "unmapped operation";

    public RedAlertEvaluation Evaluate(ForensicCase forensicCase)
    {
        var limits = options.Value;

        // An investigation that broke tells us about this agent, not about production.
        if (forensicCase.Status == ForensicCaseStatus.Failed)
        {
            return Informational("the investigation itself failed, so nothing was established about production.");
        }

        if (forensicCase.Decision == InspectorDecision.CloseNoIssue)
        {
            return Informational("the investigation found no real problem.");
        }

        // Mirrors the verdict service: nothing cited, or nothing that verified, means there is no
        // grounded evidence - and an alert is a claim on somebody's attention.
        if (forensicCase.CitationsVerified <= 0)
        {
            return Informational(forensicCase.CitationsTotal == 0
                ? "the investigation cited no artifact, so none of it could be checked against the real system."
                : $"none of the {forensicCase.CitationsTotal} cited artifacts could be confirmed in the real system.");
        }

        if (forensicCase.Decision != InspectorDecision.Escalate && forensicCase.Decision != InspectorDecision.Resolve)
        {
            return Amber($"the verdict was \"{forensicCase.Decision ?? "none"}\" rather than an escalation or a resolution.");
        }

        if (!IsInPilotScope(forensicCase.Vertical, limits.ResolvedVerticals))
        {
            return Amber($"vertical \"{forensicCase.Vertical}\" is outside the pilot scope ({string.Join(", ", limits.ResolvedVerticals)}).");
        }

        if (limits.ResolvedExcludeUnmappedStages && IsUnclassified(forensicCase))
        {
            return Amber(
                $"the traffic is unclassified (vertical \"{forensicCase.Vertical}\", stage \"{forensicCase.Stage}\") - "
                + "it needs a name before anyone can be paged for it.");
        }

        // The id the rest of the portal filters on, not the brand name. The brand is a display
        // label shared by roughly five clubs (43 clubs over 8 brands) and is populated on 21% of
        // real packages, so requiring it both asked the wrong question and disqualified almost
        // everything. What routing needs is an entity somebody can point at.
        if (limits.ResolvedRequireIdentifiableClub && !HasClubIdentity(forensicCase))
        {
            return Amber(
                $"entity \"{forensicCase.EntityKey}\" has no club id or partner id, so this case cannot be attributed "
                + "to a club or reached by the portal's club filter.");
        }

        // The club says who is affected; this says who fixes it. The pilot exit criterion requires
        // every Red Alert to carry an owner, so an alert nobody is accountable for cannot be one.
        if (limits.ResolvedRequireServiceOwner && string.IsNullOrWhiteSpace(forensicCase.OwnerTeam))
        {
            return Amber(
                $"no team is recorded as owning \"{forensicCase.Vertical} / {forensicCase.Stage}\", so there is nobody accountable to page. "
                + "The service ownership map is empty until the named responders are agreed.");
        }

        var ratio = (double)forensicCase.CitationsVerified / forensicCase.CitationsTotal;
        if (ratio < limits.ResolvedMinVerifiedCitationRatio)
        {
            return Amber(
                $"only {ratio:P0} of the cited artifacts verified, below the {limits.ResolvedMinVerifiedCitationRatio:P0} a Red Alert requires.");
        }

        // Checked last so this reason means "everything else qualified".
        if (limits.ResolvedRequireBaselineConfirmation)
        {
            return Amber(
                "every other Red Alert condition is met, but the detection was not confirmed against this entity's own "
                + "rolling baseline, and the current detection rule compares a bucket only against the one before it - "
                + "so this could be a quiet period ending rather than a degradation.");
        }

        return new RedAlertEvaluation(
            AlertLevel.RedAlert,
            $"{forensicCase.CitationsVerified} of {forensicCase.CitationsTotal} cited artifacts verified on a pilot "
            + $"{forensicCase.Vertical} journey at \"{forensicCase.Stage}\" for {ClubLabel(forensicCase)}, owned by {forensicCase.OwnerTeam}. "
            + "Raised without baseline confirmation, which is disabled by configuration.");
    }

    /// <summary>The club's own name when the catalogue knows it, the brand next, the id otherwise - never a blank where a club should be named.</summary>
    private static string ClubLabel(ForensicCase forensicCase)
    {
        var id = forensicCase.ClubId ?? forensicCase.IcePartnerId;
        var name = forensicCase.ClubName is { Length: > 0 } clubName ? clubName : forensicCase.BrandName;
        return name is { Length: > 0 } ? $"{name} ({id})" : $"club {id}";
    }

    private static bool IsInPilotScope(string vertical, IReadOnlyList<string> pilotVerticals) =>
        pilotVerticals.Any(v => Normalize(v) == Normalize(vertical));

    /// <summary>Either id identifies the club: hotels and cars carry a club id, cruises and air carry the partner id.</summary>
    private static bool HasClubIdentity(ForensicCase forensicCase) =>
        !string.IsNullOrWhiteSpace(forensicCase.ClubId) || !string.IsNullOrWhiteSpace(forensicCase.IcePartnerId);

    /// <summary>
    /// The board writes "Hotels" where configuration says "hotels" and sometimes the singular, so
    /// the comparison is on a trimmed, lower-cased, de-pluralised form rather than on equality.
    /// </summary>
    private static string Normalize(string value) =>
        value.Trim().ToLowerInvariant().TrimEnd('s');

    private static bool IsUnclassified(ForensicCase forensicCase) =>
        forensicCase.Vertical.Trim().Equals(UnclassifiedVertical, StringComparison.OrdinalIgnoreCase)
        || forensicCase.Stage.Trim().Equals(UnmappedStage, StringComparison.OrdinalIgnoreCase);

    private static RedAlertEvaluation Amber(string reason) => new(AlertLevel.Amber, reason);

    private static RedAlertEvaluation Informational(string reason) => new(AlertLevel.Informational, reason);
}
