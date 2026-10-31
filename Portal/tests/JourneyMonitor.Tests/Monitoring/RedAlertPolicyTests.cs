using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring.Inspector;
using JourneyMonitor.Core.Options;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Tests.Monitoring;

/// <summary>
/// The Red Alert definition (WI 239285, AC6) as executable rules. These tests exist because this
/// policy decides who gets paged: every downgrade below has a named reason, and a change that
/// makes any of them start alerting should have to delete a test rather than slip through.
/// </summary>
public class RedAlertPolicyTests
{
    [Fact]
    public void AQualifyingPilotCaseIsARedAlertOnceTheBaselineGateIsOpened()
    {
        var evaluation = Evaluate(Qualifying(), new RedAlertOptions { RequireBaselineConfirmation = false });

        Assert.Equal(AlertLevel.RedAlert, evaluation.Level);
        Assert.True(evaluation.IsRedAlert);
        // The reason has to say the alert was raised without that confirmation, so a demo can never
        // be mistaken for a validated alert.
        Assert.Contains("without baseline confirmation", evaluation.Reason);
    }

    [Fact]
    public void ByDefaultNothingReachesRedAlertBecauseNoBaselineConfirmationExistsYet()
    {
        // Not an oversight. The current detection rule compares a bucket only against the one
        // before it, so it fires when a quiet period ends: measured on stays.tmobiletravel.com the
        // volume we packaged as "Investigate now" (1683) sits below that club's own 7-day P95
        // (2243). Paging on that is the failure mode this gate exists to prevent.
        var evaluation = Evaluate(Qualifying(), new RedAlertOptions());

        Assert.Equal(AlertLevel.Amber, evaluation.Level);
        Assert.Contains("rolling baseline", evaluation.Reason);
    }

    [Fact]
    public void TheBaselineIsCheckedLastSoItsReasonMeansEverythingElseQualified()
    {
        // This ordering is what makes the Amber population countable: cases blocked only by the
        // baseline gate are exactly the candidates AC6 needs measured before its delivery
        // commitment can become binding.
        var evaluation = Evaluate(Qualifying(), new RedAlertOptions());

        Assert.Contains("every other Red Alert condition is met", evaluation.Reason);
    }

    [Fact]
    public void AVerticalOutsideThePilotIsAmberNoMatterHowGoodTheEvidenceIs()
    {
        var evaluation = Evaluate(Qualifying(vertical: "Cars"), Open());

        Assert.Equal(AlertLevel.Amber, evaluation.Level);
        Assert.Contains("outside the pilot scope", evaluation.Reason);
    }

    [Theory]
    [InlineData("Hotels")]
    [InlineData("hotels")]
    [InlineData("Hotel")]
    [InlineData("Cruise")]
    [InlineData("Cruises")]
    public void ThePilotScopeComparisonToleratesTheBoardsCasingAndPluralisation(string vertical)
    {
        Assert.Equal(AlertLevel.RedAlert, Evaluate(Qualifying(vertical: vertical), Open()).Level);
    }

    [Fact]
    public void UnclassifiedTrafficIsAmberBecauseNobodyCanBePagedForSomethingWeCannotName()
    {
        // 100% of current rank-3 detections land on "Unmapped operation" and 53% are also
        // "Shared / unclassified". This is the condition that keeps the pager usable.
        var evaluation = Evaluate(Qualifying(stage: "Unmapped operation"), Open());

        Assert.Equal(AlertLevel.Amber, evaluation.Level);
        Assert.Contains("needs a name", evaluation.Reason);
    }

    [Fact]
    public void AnUnclassifiedVerticalIsAmberForTheSameReason()
    {
        Assert.Equal(AlertLevel.Amber, Evaluate(Qualifying(vertical: "Shared / unclassified"), Open()).Level);
    }

    [Fact]
    public void AnEntityWithNoClubIdOrPartnerIdIsAmberBecauseNobodyCanAttributeIt()
    {
        // Keyed on the id the portal filters on, not on the brand: the brand is a display label
        // shared by roughly five clubs and populated on 21% of real packages, so requiring it both
        // asked the wrong question and disqualified almost every case.
        var evaluation = Evaluate(Qualifying(clubId: null), Open());

        Assert.Equal(AlertLevel.Amber, evaluation.Level);
        Assert.Contains("no club id or partner id", evaluation.Reason);
    }

    [Fact]
    public void AMissingBrandNameDoesNotBlockARedAlertWhenTheClubIdIsThere()
    {
        // The brand catalogue covers 43 of the 873 entities the Scout sees. A case with a real club
        // id is attributable and routable whether or not the catalogue happens to name its brand.
        var evaluation = Evaluate(Qualifying(brandName: null), Open());

        Assert.Equal(AlertLevel.RedAlert, evaluation.Level);
        Assert.Contains("club 113", evaluation.Reason);
    }

    [Fact]
    public void ACruiseCaseIsAttributableThroughThePartnerIdAlone()
    {
        // Cruise telemetry carries no VacationClubId - the partner id IS the club.
        var evaluation = Evaluate(Qualifying(vertical: "Cruises", clubId: null, icePartnerId: "140"), Open());

        Assert.Equal(AlertLevel.RedAlert, evaluation.Level);
    }

    [Fact]
    public void CitationsBelowTheVerificationBarAreAmberRatherThanARedAlert()
    {
        var forensicCase = Qualifying();
        forensicCase.CitationsTotal = 10;
        forensicCase.CitationsVerified = 5;

        var evaluation = Evaluate(forensicCase, Open());

        Assert.Equal(AlertLevel.Amber, evaluation.Level);
        Assert.Contains("50%", evaluation.Reason);
    }

    [Fact]
    public void NothingVerifiedIsInformationalRatherThanAmber()
    {
        // Amber still claims a real anomaly was observed. With nothing confirmed there is no claim
        // to make at all, so it must not sit in the list a human reviews for real problems.
        var forensicCase = Qualifying();
        forensicCase.CitationsTotal = 4;
        forensicCase.CitationsVerified = 0;

        var evaluation = Evaluate(forensicCase, Open());

        Assert.Equal(AlertLevel.Informational, evaluation.Level);
        Assert.Contains("could be confirmed", evaluation.Reason);
    }

    [Fact]
    public void CitingNothingAtAllSaysSoInsteadOfReportingZeroOfZero()
    {
        var forensicCase = Qualifying();
        forensicCase.CitationsTotal = 0;
        forensicCase.CitationsVerified = 0;

        Assert.Contains("cited no artifact", Evaluate(forensicCase, Open()).Reason);
    }

    [Fact]
    public void AClosedNoIssueCaseIsInformational()
    {
        var forensicCase = Qualifying();
        forensicCase.Decision = InspectorDecision.CloseNoIssue;

        var evaluation = Evaluate(forensicCase, Open());

        Assert.Equal(AlertLevel.Informational, evaluation.Level);
        Assert.Contains("no real problem", evaluation.Reason);
    }

    [Fact]
    public void AFailedInvestigationIsInformationalAndSaysWhy()
    {
        // A broken investigation is a statement about this agent, not about production - but it
        // still gets a stated reason, so a silent "informational" is never read as an all-clear.
        var forensicCase = Qualifying();
        forensicCase.Status = ForensicCaseStatus.Failed;

        var evaluation = Evaluate(forensicCase, Open());

        Assert.Equal(AlertLevel.Informational, evaluation.Level);
        Assert.Contains("investigation itself failed", evaluation.Reason);
    }

    [Fact]
    public void AnInconclusiveVerdictIsAmberRatherThanARedAlert()
    {
        var forensicCase = Qualifying();
        forensicCase.Decision = InspectorDecision.Inconclusive;

        var evaluation = Evaluate(forensicCase, Open());

        Assert.Equal(AlertLevel.Amber, evaluation.Level);
    }

    [Fact]
    public void AnEscalationCanBeARedAlert()
    {
        // Escalating is the normal outcome today. If only resolutions could alert, the policy would
        // be unreachable in practice and the severity ladder would be decorative.
        var forensicCase = Qualifying();
        forensicCase.Decision = InspectorDecision.Escalate;

        Assert.Equal(AlertLevel.RedAlert, Evaluate(forensicCase, Open()).Level);
    }

    [Fact]
    public void EveryOutcomeCarriesAReason()
    {
        foreach (var forensicCase in new[] { Qualifying(), Unclassified(), Failed() })
        {
            Assert.False(string.IsNullOrWhiteSpace(Evaluate(forensicCase, Open()).Reason));
            Assert.False(string.IsNullOrWhiteSpace(Evaluate(forensicCase, new RedAlertOptions()).Reason));
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Fixtures
    // ---------------------------------------------------------------------------------------------

    /// <summary>Options with the baseline gate opened, so the other conditions are what the test is measuring.</summary>
    private static RedAlertOptions Open() => new() { RequireBaselineConfirmation = false };

    private static RedAlertEvaluation Evaluate(ForensicCase forensicCase, RedAlertOptions options) =>
        new RedAlertPolicy(Options.Create(options)).Evaluate(forensicCase);

    /// <summary>
    /// A case that meets every condition the policy can evaluate today. The entity identity is
    /// init-only on <see cref="ForensicCase"/> by design - a case that moves to another package is
    /// a different case - so variants are built here rather than mutated by the tests.
    /// </summary>
    private static ForensicCase Qualifying(
        string vertical = "Hotels",
        string stage = "07 Booking",
        string? brandName = "Aloha Hawaii Tours and Travel",
        string? clubId = "113",
        string? icePartnerId = null) => new()
    {
        InvestigationPackageId = Guid.NewGuid(),
        CaseId = "SOR | Hotels | club:113 | 07 Booking | 2026-10-09T15:00:00Z",
        EntityKey = "club:113",
        BrandName = brandName,
        // The id every other filter in the portal keys on - this is the identity, the brand is a label.
        ClubId = clubId,
        IcePartnerId = icePartnerId,
        Vertical = vertical,
        Stage = stage,
        Status = ForensicCaseStatus.Resolved,
        Decision = InspectorDecision.Resolve,
        Confidence = 0.82,
        CitationsTotal = 4,
        CitationsVerified = 4,
        // Routability is its own condition - see ServiceOwnerResolverTests for the cases that
        // exercise a missing owner. These tests are about everything else.
        OwnerTeam = "Hotel Front End",
    };

    private static ForensicCase Unclassified() =>
        Qualifying(vertical: "Shared / unclassified", stage: "Unmapped operation");

    private static ForensicCase Failed()
    {
        var forensicCase = Qualifying();
        forensicCase.Status = ForensicCaseStatus.Failed;
        forensicCase.Decision = null;
        return forensicCase;
    }
}
