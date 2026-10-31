using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Domain;

namespace JourneyMonitor.Tests.EndToEnd;

/// <summary>
/// The detection pipeline run end to end, in process, for more than one company over the SAME
/// telemetry.
///
/// Every other test in this suite proves one component honours the profile. None of them proves
/// the chain does, and the chain is where the expensive failure lives: a vocabulary that leaks
/// between customers, an identity that was guessed rather than read, a cycle that fails quietly,
/// or one customer's dedup suppressing another's work. Those four are the four groups below.
///
/// Fast and free by construction - see <see cref="CompanyPipelineHarness"/>: real services, fake
/// edges, no Docker, no network, no tokens.
/// </summary>
public class MultiCompanyPipelineTests
{
    // =========================================================================================
    // Group A - separation. The same bytes in, two companies out, and nothing crossing over.
    // =========================================================================================

    [Fact]
    public async Task TheSameBoardRowsUnderTwoCompaniesShareNoServiceOrStepVocabulary()
    {
        var harness = BothCompanies();

        var freight = await harness.RunScoutAsync(CompanyProfiles.FreightKey);
        var streaming = await harness.RunScoutAsync(CompanyProfiles.StreamingKey);

        // Identical rows were read twice: any difference below is one the profile caused, because
        // nothing else about the two cycles differs.
        Assert.Equal(freight.Run.BoardRowsEvaluated, streaming.Run.BoardRowsEvaluated);
        Assert.Equal(
            freight.Packages.Select(p => p.CaseId),
            streaming.Packages.Select(p => p.CaseId));

        var freightServices = ResolvedLabels(freight, c => c.ServiceLabel);
        var streamingServices = ResolvedLabels(streaming, c => c.ServiceLabel);
        var freightSteps = ResolvedLabels(freight, c => c.StepLabel);
        var streamingSteps = ResolvedLabels(streaming, c => c.StepLabel);

        // Disjointness over empty sets is true and worthless, so prove there is something to compare.
        Assert.NotEmpty(freightServices);
        Assert.NotEmpty(streamingServices);
        Assert.NotEmpty(freightSteps);
        Assert.NotEmpty(streamingSteps);

        Assert.Empty(freightServices.Intersect(streamingServices, StringComparer.OrdinalIgnoreCase));
        Assert.Empty(freightSteps.Intersect(streamingSteps, StringComparer.OrdinalIgnoreCase));

        // The entity vocabulary is separate too - each company read its OWN stamped dimension off
        // the same request, so neither one ever learns the other's identifier.
        var freightEntities = ResolvedEntityKeys(freight);
        var streamingEntities = ResolvedEntityKeys(streaming);
        Assert.NotEmpty(freightEntities);
        Assert.NotEmpty(streamingEntities);
        Assert.Empty(freightEntities.Intersect(streamingEntities, StringComparer.OrdinalIgnoreCase));

        // Only the platform's own sentinel is shared, which is why it is excluded above: it is not
        // a word either company chose, it is the pipeline saying it has no answer.
        Assert.Contains(ScoutRowClassification.UnresolvedStepLabel, AllLabels(freight, c => c.StepLabel));
        Assert.Contains(ScoutRowClassification.UnresolvedStepLabel, AllLabels(streaming, c => c.StepLabel));
    }

    [Fact]
    public async Task EveryPackageCarriesTheProfileThatClassifiedItAndNoneCarriesTheOthers()
    {
        var harness = BothCompanies();

        var freight = await harness.RunScoutAsync(CompanyProfiles.FreightKey);
        var streaming = await harness.RunScoutAsync(CompanyProfiles.StreamingKey);

        Assert.NotEmpty(freight.Packages);
        Assert.NotEmpty(streaming.Packages);

        Assert.All(freight.Packages, p => Assert.Equal(CompanyProfiles.FreightKey, p.ProfileKey));
        Assert.All(streaming.Packages, p => Assert.Equal(CompanyProfiles.StreamingKey, p.ProfileKey));
        Assert.DoesNotContain(CompanyProfiles.StreamingKey, freight.Packages.Select(p => p.ProfileKey));
        Assert.DoesNotContain(CompanyProfiles.FreightKey, streaming.Packages.Select(p => p.ProfileKey));

        // The run says so as well, so a cycle that classified nothing is still attributable.
        Assert.Equal(CompanyProfiles.FreightKey, freight.Run.ProfileKey);
        Assert.Equal(CompanyProfiles.StreamingKey, streaming.Run.ProfileKey);

        // And the classification recorded inside the package agrees with the column it was written
        // to - two places that could drift, which is exactly why both are asserted.
        Assert.All(freight.Packages, p => Assert.Equal(p.ProfileKey, CompanyPipelineHarness.ClassificationOf(p).ProfileKey));
        Assert.All(streaming.Packages, p => Assert.Equal(p.ProfileKey, CompanyPipelineHarness.ClassificationOf(p).ProfileKey));

        // The board query itself was composed under each company's own profile, not under one of
        // them twice - that seam is where a company-specific board would re-enter the pipeline.
        Assert.Equal([CompanyProfiles.FreightKey, CompanyProfiles.StreamingKey], harness.BoardQueryProfileKeys);
    }

    [Fact]
    public async Task AskingTheStoreForOneCompanysPackagesReturnsNoneOfTheOthersInEitherDirection()
    {
        var harness = BothCompanies();

        await harness.RunScoutAsync(CompanyProfiles.FreightKey);
        await harness.RunScoutAsync(CompanyProfiles.StreamingKey);

        var freight = await harness.PackagesUnderAsync(CompanyProfiles.FreightKey);
        var streaming = await harness.PackagesUnderAsync(CompanyProfiles.StreamingKey);

        Assert.NotEmpty(freight);
        Assert.NotEmpty(streaming);
        Assert.Empty(freight.Where(p => p.ProfileKey == CompanyProfiles.StreamingKey));
        Assert.Empty(streaming.Where(p => p.ProfileKey == CompanyProfiles.FreightKey));
        Assert.Empty(freight.Select(p => p.Id).Intersect(streaming.Select(p => p.Id)));

        // Partitioning the store by profile loses nothing and duplicates nothing: a package that
        // belonged to neither, or to both, would be invisible to one customer or leak to the other.
        var all = await harness.AllPackagesAsync();
        Assert.Equal(all.Count, freight.Count + streaming.Count);
    }

    // =========================================================================================
    // Group B - attribution integrity. WHO was affected, and how confidently we know it.
    // =========================================================================================

    [Fact]
    public async Task TheIdentitySourceThatAnsweredAndHowItWasReachedSurviveIntoThePackage()
    {
        var harness = BothCompanies();

        var freight = await harness.RunScoutAsync(CompanyProfiles.FreightKey);
        var streaming = await harness.RunScoutAsync(CompanyProfiles.StreamingKey);

        // The same request carried both companies' identifiers; each profile read only its own.
        var carrier = Package(freight, CompanyBoards.SharedCaseId);
        var carrierIdentity = CompanyPipelineHarness.ClassificationOf(carrier);
        Assert.True(carrierIdentity.EntityIsResolved);
        Assert.Equal(CompanyProfiles.ShipperDimension, carrierIdentity.EntitySourceKey);
        Assert.Equal(CompanyResolution.ExactMatch, carrierIdentity.EntityResolvedBy);
        Assert.Equal(CompanyProfiles.ShipperValue, carrierIdentity.EntityValue);
        Assert.Equal(CompanyProfiles.ShipperEntityKey, carrierIdentity.EntityKey);

        var video = Package(streaming, CompanyBoards.SharedCaseId);
        var videoIdentity = CompanyPipelineHarness.ClassificationOf(video);
        Assert.True(videoIdentity.EntityIsResolved);
        Assert.Equal(CompanyProfiles.SubscriberDimension, videoIdentity.EntitySourceKey);
        Assert.Equal(CompanyResolution.ExactMatch, videoIdentity.EntityResolvedBy);
        Assert.Equal(CompanyProfiles.SubscriberValue, videoIdentity.EntityValue);
        Assert.Equal(CompanyProfiles.SubscriberEntityKey, videoIdentity.EntityKey);

        // The key reaches the package's own column, not only the classification blob - that column
        // is what every reader downstream actually filters and groups on.
        Assert.Equal(CompanyProfiles.ShipperEntityKey, carrier.EntityKey);
        Assert.Equal(CompanyProfiles.SubscriberEntityKey, video.EntityKey);

        // And the step says how it was reached too: a pattern that matched is worth more than a
        // service-wide catch-all, and a reader cannot re-derive that afterwards.
        Assert.Equal(CompanyResolution.PatternMatch, carrierIdentity.StepResolvedBy);
        Assert.Equal(CompanyResolution.PatternMatch, videoIdentity.StepResolvedBy);
    }

    [Fact]
    public async Task ARowCarryingNoConfiguredIdentityIsPackagedAsExplicitlyUnresolved()
    {
        var harness = BothCompanies();

        var freight = await harness.RunScoutAsync(CompanyProfiles.FreightKey);

        // The row is still here. Dropping it would be the worst outcome of the three: an incident
        // nobody can attribute is still an incident.
        var anonymous = Package(freight, CompanyBoards.AnonymousCaseId);
        var classification = CompanyPipelineHarness.ClassificationOf(anonymous);

        Assert.False(classification.EntityIsResolved);
        Assert.Equal(CompanyResolution.Unresolved, classification.EntityResolvedBy);
        Assert.Equal(ScoutRowClassification.UnresolvedEntityKey, classification.EntityKey);
        Assert.Equal(ScoutRowClassification.UnresolvedEntityKey, anonymous.EntityKey);

        // Not blank - a blank entity reads as "nobody was affected" rather than "we do not know".
        Assert.False(string.IsNullOrWhiteSpace(anonymous.EntityKey));

        // Not guessed either. The board DID offer a host for this row, and the profile does not
        // list the host as an identity source, so the honest answer is to decline it: a guessed
        // identity attributes an incident to a customer who is not having one.
        Assert.Null(classification.EntityValue);
        Assert.Null(classification.EntitySourceKey);
        Assert.DoesNotContain(CompanyBoards.BoardHost, anonymous.EntityKey, StringComparison.OrdinalIgnoreCase);

        // Nothing else about the row degraded: the step still classified, so "unknown who" has not
        // been allowed to become "unknown what".
        Assert.True(classification.StepIsResolved);
    }

    // =========================================================================================
    // Group C - honest failure. A failure must never be readable as health.
    // =========================================================================================

    [Fact]
    public async Task AnUnknownProfileFailsTheCycleLoudlyRatherThanPublishingAnEmptyOne()
    {
        var harness = BothCompanies();

        var outcome = await harness.RunScoutAsync("no-such-company");

        Assert.Equal(ScoutRunStatus.Failed, outcome.Run.Status);
        Assert.NotEqual(ScoutRunStatus.Clear, outcome.Run.Status);
        Assert.False(string.IsNullOrWhiteSpace(outcome.Run.ErrorMessage));
        Assert.Contains("no-such-company", outcome.Run.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(outcome.Packages);

        // It fails BEFORE the board is queried: a board nothing can classify is indistinguishable
        // from a quiet one, so there is no point paying for it.
        Assert.Equal(0, harness.Telemetry.Queries);

        // And the failure is persisted, not only returned - the card reads the store, not the call.
        var recorded = await harness.Runs.GetRecentAsync(10);
        Assert.Equal(ScoutRunStatus.Failed, recorded[0].Status);
    }

    [Fact]
    public async Task ADisabledProfileFailsTheCycleRatherThanRunningUnclassified()
    {
        // Switching a customer off must stop its cycle visibly. A disabled profile that silently
        // classified nothing would publish a permanently calm board for a company nobody is watching.
        var harness = new CompanyPipelineHarness([CompanyProfiles.Freight(enabled: false)]);

        var outcome = await harness.RunScoutAsync(CompanyProfiles.FreightKey);

        Assert.Equal(ScoutRunStatus.Failed, outcome.Run.Status);
        Assert.Contains(CompanyProfiles.FreightKey, outcome.Run.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(outcome.Packages);
        Assert.Equal(0, harness.Telemetry.Queries);
    }

    [Fact]
    public async Task ATelemetryBackendThatThrowsProducesAFailedRunAndNotAHealthyBoard()
    {
        var harness = BothCompanies();
        harness.Telemetry.Failure = new InvalidOperationException("the telemetry backend refused the query");

        var outcome = await harness.RunScoutAsync(CompanyProfiles.FreightKey);

        Assert.Equal(ScoutRunStatus.Failed, outcome.Run.Status);
        Assert.Contains("refused the query", outcome.Run.ErrorMessage!);
        Assert.Empty(outcome.Packages);

        // The cycle did reach the backend - this is a failure to READ the board, which is a
        // different thing from a profile that could not be resolved, and the two must stay telling
        // apart.
        Assert.Equal(1, harness.Telemetry.Queries);
        Assert.Equal(0, outcome.Run.BoardRowsEvaluated);
    }

    [Fact]
    public async Task AProfileWhoseStepsMatchNothingStillPackagesEveryRowAsUnresolved()
    {
        // The dangerous version of this bug is silence: rows that match no step quietly disappear,
        // the cycle reports "clear", and an unmapped company looks like a company with no traffic.
        var harness = new CompanyPipelineHarness([CompanyProfiles.MatchesNothing()]);

        var outcome = await harness.RunScoutAsync("unmapped");

        Assert.Equal(ScoutRunStatus.Detected, outcome.Run.Status);
        Assert.Equal(outcome.Run.BoardRowsEvaluated, outcome.Packages.Count);
        Assert.Equal(CompanyBoards.AllCaseIds, outcome.Packages.Select(p => p.CaseId).OrderBy(c => c, StringComparer.Ordinal));

        Assert.All(outcome.Packages, p =>
        {
            var classification = CompanyPipelineHarness.ClassificationOf(p);
            Assert.False(classification.StepIsResolved);
            Assert.Equal(CompanyResolution.Unresolved, classification.StepResolvedBy);

            // Unresolved, NOT unavailable: the distinction is the difference between "this profile
            // does not describe this traffic" (their gap) and "the classifier broke" (ours).
            Assert.Equal(ScoutRowClassification.UnresolvedStepLabel, p.Stage);
            Assert.Equal(ScoutRowClassification.UnresolvedServiceLabel, p.Vertical);
            Assert.NotEqual(ScoutRowClassification.UnavailableStepLabel, p.Stage);
            Assert.NotEqual(ScoutRowClassification.UnavailableServiceLabel, p.Vertical);
        });
    }

    // =========================================================================================
    // Group D - dedup. Once per company, and never across companies.
    // =========================================================================================

    [Fact]
    public async Task TheSameBoardReadTwiceForOneCompanyPackagesNothingTheSecondTime()
    {
        var harness = BothCompanies();

        var first = await harness.RunScoutAsync(CompanyProfiles.FreightKey);
        var second = await harness.RunScoutAsync(CompanyProfiles.FreightKey);

        Assert.NotEmpty(first.Packages);
        Assert.Empty(second.Packages);
        Assert.Equal(0, second.Run.PackagesCreated);
        Assert.Equal(first.Run.PackagesCreated, second.Run.PackagesSkippedDuplicate);

        // Nothing was lost and nothing was added: the store still holds exactly the first cycle.
        var all = await harness.AllPackagesAsync();
        Assert.Equal(first.Packages.Count, all.Count);
        Assert.Equal(first.Packages.Select(p => p.Id), all.Select(p => p.Id));
    }

    [Fact]
    public async Task TwoCompaniesWhoseRowsProduceTheSameCaseIdAreBothPackaged()
    {
        // THE regression this whole change exists for.
        //
        // The dedup key is the board's own CaseId, and the board is the same board. Two customers
        // monitored by one deployment therefore produce byte-identical CaseIds for completely
        // unrelated incidents. Before the profile joined the key, whichever company's cycle ran
        // first silently suppressed every other company's work for that bucket - and suppressed it
        // as a "duplicate", which reads in the UI as the system working correctly.
        //
        // If this test ever fails, one customer is invisible to this platform. It is not a
        // cosmetic assertion.
        var harness = BothCompanies();

        var freight = await harness.RunScoutAsync(CompanyProfiles.FreightKey);

        // Same key, other company: not known yet, however loudly the first cycle just recorded it.
        Assert.True(await harness.Packages.ExistsByCaseIdAsync(
            CompanyBoards.SharedCaseId, ScoutBoard.Latency, CompanyProfiles.FreightKey));
        Assert.False(await harness.Packages.ExistsByCaseIdAsync(
            CompanyBoards.SharedCaseId, ScoutBoard.Latency, CompanyProfiles.StreamingKey));

        var streaming = await harness.RunScoutAsync(CompanyProfiles.StreamingKey);

        // The case ids really are identical - without this the test would be proving nothing.
        Assert.Equal(freight.Packages.Select(p => p.CaseId), streaming.Packages.Select(p => p.CaseId));

        Assert.Equal(freight.Run.PackagesCreated, streaming.Run.PackagesCreated);
        Assert.Equal(0, streaming.Run.PackagesSkippedDuplicate);
        Assert.NotEmpty(streaming.Packages);

        // Every shared case id is in the store exactly twice, once per company.
        var all = await harness.AllPackagesAsync();
        Assert.Equal(freight.Packages.Count * 2, all.Count);
        foreach (var group in all.GroupBy(p => p.CaseId, StringComparer.Ordinal))
        {
            Assert.Equal(2, group.Count());
            Assert.Equal(
                [CompanyProfiles.FreightKey, CompanyProfiles.StreamingKey],
                group.Select(p => p.ProfileKey).OrderBy(k => k, StringComparer.Ordinal));
        }
    }

    // =========================================================================================

    private static CompanyPipelineHarness BothCompanies() =>
        new([CompanyProfiles.Freight(), CompanyProfiles.Streaming()]);

    private static InvestigationPackage Package(ScoutOutcome outcome, string caseId) =>
        Assert.Single(outcome.Packages.Where(p => p.CaseId == caseId));

    /// <summary>
    /// Only the labels a profile actually produced. The unresolved sentinels are the platform's own
    /// words and both companies emit them, so including them would make every separation assertion
    /// fail for a reason that has nothing to do with separation.
    /// </summary>
    private static IReadOnlyCollection<string> ResolvedLabels(ScoutOutcome outcome, Func<ScoutRowClassification, string> label) =>
        outcome.Packages
            .Select(CompanyPipelineHarness.ClassificationOf)
            .Where(c => c.StepIsResolved)
            .Select(label)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyCollection<string> AllLabels(ScoutOutcome outcome, Func<ScoutRowClassification, string> label) =>
        outcome.Packages.Select(CompanyPipelineHarness.ClassificationOf).Select(label).ToList();

    private static IReadOnlyCollection<string> ResolvedEntityKeys(ScoutOutcome outcome) =>
        outcome.Packages
            .Select(CompanyPipelineHarness.ClassificationOf)
            .Where(c => c.EntityIsResolved)
            .Select(c => c.EntityKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
