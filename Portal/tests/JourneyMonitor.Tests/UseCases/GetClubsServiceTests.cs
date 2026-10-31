using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Filtering;
using JourneyMonitor.Core.UseCases;

namespace JourneyMonitor.Tests.UseCases;

/// <summary>
/// GET /api/clubs now lists brand.Information (platformDB) instead of App Insights discovery.
/// Fixture rows mirror Sql/CreateBrandTables.sql.
/// </summary>
public class GetClubsServiceTests
{
    private static readonly BrandClub[] BrandRows =
    [
        new("AARP Go", "15893", "333", "members.aarparrivia.com"),
        new("AARP Go", "16108", "333", "members.aarparrivia.com"),
        new("Smarter Getaways", "15662", "239", "members.smartergetaways.com"),
        new("Synchrony Travel", "15039", "306", "travel.mysynchronytravel.com"),
        new("T-Mobile", "15639", "308", "stays.tmobiletravel.com"),
        new("Broken Row", "abc", null, "not a host"),
    ];

    private sealed class FakeBrandCatalog(IReadOnlyList<BrandClub> rows, bool configured = true, bool throws = false) : IBrandCatalog
    {
        public bool IsConfigured => configured;

        public Task<IReadOnlyList<BrandClub>> GetAllAsync(CancellationToken cancellationToken) =>
            throws ? throw new InvalidOperationException("SQL down") : Task.FromResult(rows);
    }

    [Fact]
    public async Task HotelsListsEveryBrandClubIdWithBrandLabelAndUrlAsHost()
    {
        var service = new GetClubsService(new FakeBrandCatalog(BrandRows));

        var result = await service.GetAsync("hotels", null, null, true, null, CancellationToken.None);

        Assert.Equal(GetClubsService.DatabaseSource, result.Source);
        Assert.Equal("15m", result.Win);
        Assert.Equal("All hotel clubs", result.Placeholder);
        Assert.Null(result.Warning);
        Assert.Equal(["15893", "16108", "15662", "15039", "15639"], result.Clubs.Select(c => c.Value).ToArray());

        var tmobile = result.Clubs.Single(c => c.ClubId == "15639");
        Assert.Equal("T-Mobile (15639)", tmobile.Label);
        Assert.Equal("stays.tmobiletravel.com", tmobile.Host);
        Assert.Equal("308", tmobile.PartnerId);
        Assert.Equal("333", result.Clubs.Single(c => c.ClubId == "15893").PartnerId);
        Assert.Equal("AARP Go (16108)", result.Clubs.Single(c => c.ClubId == "16108").Label);
    }

    [Fact]
    public async Task StagingHotelsListsOneOptionPerUatHost()
    {
        BrandClub[] rows =
        [
            new("Smarter Getaways", "15662", "239", "members.smartergetaways.com"),
            new("Synchrony Travel", "15039", "306", "travel.mysynchronytravel.saveonuat.com"),
            new("Synchrony Travel", "15491", "306", "travel.mysynchronytravel.saveonuat.com"),
            new("Synchrony Travel", "15695", "306", "travel.mysynchronytravel.saveonuat.com"),
            new("T-Mobile", "15639", "308", "tmobile.saveonuat.com"),
            new("WeSalute", "15811", "323", "book.wesalute.com"),
        ];
        var service = new GetClubsService(new FakeBrandCatalog(rows));

        var staging = await service.GetAsync("hotels", null, "staging", true, null, CancellationToken.None);

        Assert.Equal(["15039", "15639"], staging.Clubs.Select(c => c.Value).ToArray());
        Assert.Equal("Synchrony Travel (15039)", staging.Clubs[0].Label);
        Assert.Equal("travel.mysynchronytravel.saveonuat.com", staging.Clubs[0].Host);
        Assert.Equal("tmobile.saveonuat.com", staging.Clubs[1].Host);

        var production = await service.GetAsync("hotels", null, "production", true, null, CancellationToken.None);
        Assert.Equal(["15662", "15039", "15491", "15695", "15639", "15811"], production.Clubs.Select(c => c.Value).ToArray());

        var cruises = await service.GetAsync("cruises", null, "staging", true, null, CancellationToken.None);
        Assert.Contains(cruises.Clubs, c => c.Value == "239");
    }

    [Fact]
    public async Task AirCollapsesRowsToDistinctPartnerIds()
    {
        var service = new GetClubsService(new FakeBrandCatalog(BrandRows));

        var result = await service.GetAsync("air", null, null, true, null, CancellationToken.None);

        Assert.Equal(GetClubsService.DatabaseSource, result.Source);
        Assert.Equal(["333", "239", "306", "308"], result.Clubs.Select(c => c.Value).ToArray());
        Assert.Equal("T-Mobile (308)", result.Clubs.Single(c => c.ClubId == "308").Label);
        Assert.Equal("308", result.Clubs.Single(c => c.ClubId == "308").PartnerId);
    }

    [Fact]
    public async Task UnconfiguredDatabaseFallsBackToDemoClubs()
    {
        var service = new GetClubsService(new FakeBrandCatalog(BrandRows, configured: false));

        var result = await service.GetAsync("hotels", "2h", null, true, null, CancellationToken.None);

        Assert.Equal("demo", result.Source);
        Assert.Single(result.Clubs);
        Assert.Equal("15639", result.Clubs[0].ClubId);
        Assert.Equal("T-Mobile / T Life", result.Clubs[0].Label);
        Assert.Contains("not configured", result.Warning);
    }

    [Fact]
    public async Task DatabaseFailureFallsBackToDemoClubs()
    {
        var service = new GetClubsService(new FakeBrandCatalog(BrandRows, throws: true));

        var result = await service.GetAsync("cruises", null, null, true, null, CancellationToken.None);

        Assert.Equal("configured", result.Source);
        Assert.Equal("308", result.Clubs.Single().ClubId);
        Assert.Contains("unavailable", result.Warning);
    }

    [Fact]
    public async Task ForcedDemoModeSkipsTheDatabase()
    {
        var service = new GetClubsService(new FakeBrandCatalog(BrandRows, throws: true));

        var result = await service.GetAsync("hotels", null, null, true, "demo", CancellationToken.None);

        Assert.Equal("demo", result.Source);
        Assert.Equal("Demo mode: known Hotel club only, not a live club list.", result.Warning);
    }

    [Fact]
    public async Task NoDomainStaysDisabled()
    {
        var service = new GetClubsService(new FakeBrandCatalog(BrandRows));

        var result = await service.GetAsync("all", null, null, true, null, CancellationToken.None);

        Assert.Equal("none", result.Source);
        Assert.True(result.Disabled);
        Assert.Empty(result.Clubs);
    }

    [Fact]
    public void HostForResolvesBrandUrlAndIgnoresInvalidHosts()
    {
        Assert.Equal("members.smartergetaways.com", BrandClubOptions.HostFor(BrandRows, "15662"));
        Assert.Null(BrandClubOptions.HostFor(BrandRows, "abc"));
        Assert.Null(BrandClubOptions.HostFor(BrandRows, "99999"));
        Assert.Equal("239", BrandClubOptions.PartnerFor(BrandRows, "15662"));
        Assert.Equal("members.aarparrivia.com", BrandClubOptions.HostForDomain(BrandRows, "cruises", "333"));
        Assert.Null(BrandClubOptions.PartnerFor(BrandRows, "99999"));
        Assert.Equal("T-Mobile", BrandClubOptions.DisplayName(BrandRows, "15639"));
        Assert.Equal("T-Mobile", BrandClubOptions.DisplayName(BrandRows, "308"));
        Assert.Equal("AARP Go", BrandClubOptions.DisplayName(BrandRows, "333"));
        Assert.Null(BrandClubOptions.DisplayName(BrandRows, "SOR"));
    }
}
