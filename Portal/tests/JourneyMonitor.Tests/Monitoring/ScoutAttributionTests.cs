using JourneyMonitor.Core.Domain;
using JourneyMonitor.Infrastructure.Monitoring;

namespace JourneyMonitor.Tests.Monitoring;

/// <summary>
/// One deployment serves several companies, and the CaseId shape - "{Source} | {Vertical} |
/// {Entity} | {Stage} | {Bucket}" - carries nothing that says whose it is. Two companies watching
/// the same telemetry therefore produce byte-identical case ids, so before ProfileKey existed the
/// dedup check made the first company to run suppress every other company's package for that
/// bucket: not a visible failure, just missing work. These tests pin that down at the store, which
/// is where the suppression happened.
/// </summary>
public class ScoutAttributionTests
{
    private const string CompanyA = "arrivia";
    private const string CompanyB = "contoso";

    // Same bucket of the same group, as both companies' boards would emit it.
    private const string SharedCaseId = "SOR | Hotels | club:140 | 07 Booking | 2026-10-17T15:00:00Z";

    [Fact]
    public async Task SameCaseIdOnTheSameBoardIsNotADuplicateAcrossCompanies()
    {
        var packages = new InMemoryInvestigationPackageRepository();

        await packages.AddAsync(Package(CompanyA, SharedCaseId, ScoutBoard.Latency));

        // The second company has not packaged anything yet, so nothing of its own can be a
        // duplicate - even though company A already holds this exact case id on this exact board.
        Assert.False(await packages.ExistsByCaseIdAsync(SharedCaseId, ScoutBoard.Latency, CompanyB));

        await packages.AddAsync(Package(CompanyB, SharedCaseId, ScoutBoard.Latency));

        var stored = await packages.GetRecentAsync(10);
        Assert.Equal(2, stored.Count);
        Assert.Equal(
            [CompanyA, CompanyB],
            stored.Select(p => p.ProfileKey).OrderBy(k => k, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task SameCaseIdSameBoardSameCompanyIsADuplicate()
    {
        var packages = new InMemoryInvestigationPackageRepository();

        await packages.AddAsync(Package(CompanyA, SharedCaseId, ScoutBoard.Latency));

        Assert.True(await packages.ExistsByCaseIdAsync(SharedCaseId, ScoutBoard.Latency, CompanyA));

        // And the store itself refuses the second write, so the C# check does not have to win a
        // race against a concurrent cycle.
        await packages.AddAsync(Package(CompanyA, SharedCaseId, ScoutBoard.Latency));
        Assert.Single(await packages.GetRecentAsync(10));
    }

    [Fact]
    public async Task TheBoardStillScopesTheDedupWithinOneCompany()
    {
        var packages = new InMemoryInvestigationPackageRepository();

        await packages.AddAsync(Package(CompanyA, SharedCaseId, ScoutBoard.Latency));

        // Adding the profile to the key must not have cost the board scoping: the same group and
        // bucket flagged by both boards is two pieces of work for the same company.
        Assert.False(await packages.ExistsByCaseIdAsync(SharedCaseId, ScoutBoard.Exception, CompanyA));

        await packages.AddAsync(Package(CompanyA, SharedCaseId, ScoutBoard.Exception));
        Assert.Equal(2, (await packages.GetRecentAsync(10)).Count);
    }

    [Fact]
    public async Task PackageProfileKeyRoundTrips()
    {
        var packages = new InMemoryInvestigationPackageRepository();
        var package = Package(CompanyB, SharedCaseId, ScoutBoard.Latency);

        await packages.AddAsync(package);

        var byId = await packages.GetByIdAsync(package.Id);
        Assert.NotNull(byId);
        Assert.Equal(CompanyB, byId.ProfileKey);
        Assert.Equal(CompanyB, Assert.Single(await packages.GetRecentAsync(10)).ProfileKey);
    }

    [Fact]
    public async Task RunProfileKeyRoundTripsAfterTheProfileResolves()
    {
        var runs = new InMemoryScoutRunRepository();
        var run = new ScoutRun
        {
            Board = ScoutBoard.Latency,
            WindowFrom = DateTimeOffset.UtcNow.AddHours(-4),
            WindowTo = DateTimeOffset.UtcNow,
        };

        // The cycle is recorded before the profile resolves, so the attribution arrives on the
        // update - a run persisted only at insert time would be permanently unattributed.
        await runs.AddAsync(run);
        Assert.Equal("", run.ProfileKey);

        run.ProfileKey = CompanyA;
        run.Status = ScoutRunStatus.Clear;
        run.CompletedAt = DateTimeOffset.UtcNow;
        await runs.UpdateAsync(run);

        Assert.Equal(CompanyA, Assert.Single(await runs.GetRecentAsync(10)).ProfileKey);
    }

    [Fact]
    public async Task OneCompanysPackagesContainNoneOfTheOthers()
    {
        var packages = new InMemoryInvestigationPackageRepository();

        await packages.AddAsync(Package(CompanyA, SharedCaseId, ScoutBoard.Latency));
        await packages.AddAsync(Package(CompanyA, "SOR | Air | club:7 | 03 Search | 2026-10-17T15:00:00Z", ScoutBoard.Latency));
        await packages.AddAsync(Package(CompanyB, SharedCaseId, ScoutBoard.Latency));

        var all = await packages.GetRecentAsync(50);
        var forB = all.Where(p => p.ProfileKey == CompanyB).ToList();

        Assert.Single(forB);
        Assert.DoesNotContain(forB, p => p.ProfileKey == CompanyA);
        Assert.Equal(2, all.Count(p => p.ProfileKey == CompanyA));
    }

    private static InvestigationPackage Package(string profileKey, string caseId, string board) => new()
    {
        CaseId = caseId,
        ProfileKey = profileKey,
        Board = board,
        Vertical = "Hotels",
        Stage = "07 Booking",
        Source = "SOR",
        EntityKey = "club:140",
        EntityKind = ClubEntityKind.Club,
        ClubResolvedBy = ClubResolution.ClubIdMatch,
        Priority = "Investigate now",
        PriorityRank = 3,
        Reason = "p95 at least 3x the preceding interval",
        WindowFrom = DateTimeOffset.Parse("2026-10-17T15:00:00Z"),
        WindowTo = DateTimeOffset.Parse("2026-10-17T15:05:00Z"),
        DeterministicJson = "{}",
        AssessmentJson = "{}",
        ScoutRunId = Guid.NewGuid(),
    };
}
