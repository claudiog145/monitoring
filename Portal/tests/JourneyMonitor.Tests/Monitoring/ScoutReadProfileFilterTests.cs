using JourneyMonitor.Core.Domain;
using JourneyMonitor.Infrastructure.Monitoring;

namespace JourneyMonitor.Tests.Monitoring;

/// <summary>
/// The write side was partitioned by company first (see ScoutAttributionTests); the reads were
/// not, so with two companies active the portal showed both customers' work in one list and
/// summed both into one KPI. These tests pin the read filter down: a named company sees only its
/// own rows, and - the part that is easy to get wrong - an ABSENT or BLANK key still means "every
/// company" rather than the empty-string ProfileKey carried by the rows written before the column
/// existed.
///
/// Exercised against the in-memory stores: the repository pair they mirror is the SQL one, whose
/// procs cannot run here without a database. The filter predicate in
/// usp_GetRecentInvestigationPackages / usp_GetRecentScoutRuns / usp_GetScoutStats is therefore
/// only covered by review, not by these tests.
/// </summary>
public class ScoutReadProfileFilterTests
{
    private const string CompanyA = "arrivia";
    private const string CompanyB = "contoso";

    // What every row written before ProfileKey existed carries. A real value, never a wildcard.
    private const string Unattributed = "";

    [Fact]
    public async Task PackagesForOneCompanyExcludeTheOthers()
    {
        var packages = await SeededPackagesAsync();

        var forA = await packages.GetRecentAsync(50, profileKey: CompanyA);

        Assert.Equal(2, forA.Count);
        Assert.All(forA, p => Assert.Equal(CompanyA, p.ProfileKey));
    }

    [Fact]
    public async Task PackagesWithoutAProfileKeyReturnEveryCompany()
    {
        var packages = await SeededPackagesAsync();

        var all = await packages.GetRecentAsync(50);

        Assert.Equal(4, all.Count);
        Assert.Equal(
            [Unattributed, CompanyA, CompanyA, CompanyB],
            all.Select(p => p.ProfileKey).OrderBy(k => k, StringComparer.Ordinal).ToArray());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BlankPackageProfileKeyReadsAsEveryCompanyNotAsTheUnattributedOnes(string blank)
    {
        var packages = await SeededPackagesAsync();

        var result = await packages.GetRecentAsync(50, profileKey: blank);

        // The trap: "" is a stored value, so a blank filter passed through verbatim would return
        // the single unattributed row and call it "everything".
        Assert.Equal(4, result.Count);
        Assert.Contains(result, p => p.ProfileKey == CompanyA);
        Assert.Contains(result, p => p.ProfileKey == CompanyB);
    }

    [Fact]
    public async Task UnattributedPackagesAreNotAddressableByAnyFilterValue()
    {
        var packages = await SeededPackagesAsync();

        // Documents the accepted cost of the above: there is no filter value that selects only the
        // pre-ProfileKey rows, because blank means "all". They are visible in the unfiltered list
        // and nowhere else - a backfill, not a query, is what would make them company-addressable.
        var all = await packages.GetRecentAsync(50);

        Assert.Single(all, p => p.ProfileKey == Unattributed);
    }

    [Fact]
    public async Task PackageProfileAndBoardFiltersCombine()
    {
        var packages = await SeededPackagesAsync();

        var result = await packages.GetRecentAsync(50, ScoutBoard.Exception, CompanyA);

        var only = Assert.Single(result);
        Assert.Equal(CompanyA, only.ProfileKey);
        Assert.Equal(ScoutBoard.Exception, only.Board);
    }

    [Fact]
    public async Task RunsForOneCompanyExcludeTheOthers()
    {
        var runs = await SeededRunsAsync();

        var forA = await runs.GetRecentAsync(50, profileKey: CompanyA);

        Assert.Equal(2, forA.Count);
        Assert.All(forA, r => Assert.Equal(CompanyA, r.ProfileKey));
    }

    [Fact]
    public async Task RunsWithoutAProfileKeyReturnEveryCompany()
    {
        var runs = await SeededRunsAsync();

        Assert.Equal(4, (await runs.GetRecentAsync(50)).Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BlankRunProfileKeyReadsAsEveryCompany(string blank)
    {
        var runs = await SeededRunsAsync();

        Assert.Equal(4, (await runs.GetRecentAsync(50, profileKey: blank)).Count);
    }

    [Fact]
    public async Task StatsAreCountedPerCompany()
    {
        var runs = await SeededRunsAsync();

        var forA = await runs.GetStatsAsync(profileKey: CompanyA);
        var forB = await runs.GetStatsAsync(profileKey: CompanyB);
        var everyone = await runs.GetStatsAsync();

        // The reason the filter exists: without it one customer's cycles inflate the other's card.
        Assert.Equal(2, forA.Cycles);
        Assert.Equal(3, forA.PackagesCreated);
        Assert.Equal(1, forB.Cycles);
        Assert.Equal(5, forB.PackagesCreated);
        Assert.Equal(4, everyone.Cycles);
        Assert.Equal(9, everyone.PackagesCreated);
    }

    [Fact]
    public async Task StatsProfileAndBoardFiltersCombine()
    {
        var runs = await SeededRunsAsync();

        var stats = await runs.GetStatsAsync(ScoutBoard.Exception, CompanyA);

        Assert.Equal(1, stats.Cycles);
        Assert.Equal(1, stats.PackagesCreated);
    }

    /// <summary>Two companies plus one pre-ProfileKey row, across both boards.</summary>
    private static async Task<InMemoryInvestigationPackageRepository> SeededPackagesAsync()
    {
        var packages = new InMemoryInvestigationPackageRepository();
        await packages.AddAsync(Package(CompanyA, "SOR | Hotels | club:140 | 07 Booking | 2026-10-17T15:00:00Z", ScoutBoard.Latency));
        await packages.AddAsync(Package(CompanyA, "SOR | Air | club:7 | 03 Search | 2026-10-17T15:00:00Z", ScoutBoard.Exception));
        await packages.AddAsync(Package(CompanyB, "SOR | Hotels | club:140 | 07 Booking | 2026-10-17T15:00:00Z", ScoutBoard.Latency));
        await packages.AddAsync(Package(Unattributed, "SOR | Cruise | club:9 | 05 Quote | 2026-10-17T15:00:00Z", ScoutBoard.Latency));
        return packages;
    }

    private static async Task<InMemoryScoutRunRepository> SeededRunsAsync()
    {
        var runs = new InMemoryScoutRunRepository();
        await runs.AddAsync(Run(CompanyA, ScoutBoard.Latency, packagesCreated: 2));
        await runs.AddAsync(Run(CompanyA, ScoutBoard.Exception, packagesCreated: 1));
        await runs.AddAsync(Run(CompanyB, ScoutBoard.Latency, packagesCreated: 5));
        await runs.AddAsync(Run(Unattributed, ScoutBoard.Latency, packagesCreated: 1));
        return runs;
    }

    private static ScoutRun Run(string profileKey, string board, int packagesCreated) => new()
    {
        ProfileKey = profileKey,
        Board = board,
        Status = ScoutRunStatus.Detected,
        StartedAt = DateTimeOffset.Parse("2026-10-17T15:00:00Z"),
        CompletedAt = DateTimeOffset.Parse("2026-10-17T15:00:30Z"),
        WindowFrom = DateTimeOffset.Parse("2026-10-17T11:00:00Z"),
        WindowTo = DateTimeOffset.Parse("2026-10-17T15:00:00Z"),
        PackagesCreated = packagesCreated,
    };

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
