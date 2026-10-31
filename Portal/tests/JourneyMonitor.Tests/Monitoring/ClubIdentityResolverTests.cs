using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring;
using Microsoft.Extensions.Logging.Abstractions;

namespace JourneyMonitor.Tests.Monitoring;

public class ClubIdentityResolverTests
{
    private static ClubIdentityResolver Build(params ClubInventoryRow[] clubs) =>
        new(new FakeClubInventoryStore(clubs), NullLogger<ClubIdentityResolver>.Instance);

    private static ClubInventoryRow Row(string brandName, string clubId, string? icePartnerId, string? site, string? clubName = null) =>
        new(Array.Empty<ClubProductStatus>(), brandName, int.Parse(clubId), clubName, null, icePartnerId, site, "Fusion", 0, null, "", "Production");

    [Fact]
    public async Task ResolvesANumericClubKeyAgainstClubId()
    {
        var resolver = Build(Row("T-Mobile", "15639", "308", "stays.tmobiletravel.com", "T-Mobile Travel"));

        var identity = await resolver.ResolveAsync("club:15639");

        Assert.Equal("T-Mobile", identity.BrandName);
        Assert.Equal("T-Mobile Travel", identity.ClubName);
        Assert.Equal(ClubResolution.ClubIdMatch, identity.ResolvedBy);
        Assert.Equal(ClubEntityKind.Club, identity.Kind);
        Assert.True(identity.IsResolved);
    }

    [Fact]
    public async Task ResolvesACruiseClubKeyAgainstIcePartnerIdBecauseCruiseHasNoVacationClubId()
    {
        // The board emits "club:308" for Cruise traffic, where 308 is the ICE partner id, not a
        // club id - without this fallback no Cruise entity would ever resolve.
        var resolver = Build(Row("T-Mobile", "15639", "308", "stays.tmobiletravel.com"));

        var identity = await resolver.ResolveAsync("club:308");

        Assert.Equal("T-Mobile", identity.BrandName);
        Assert.Equal(ClubResolution.IcePartnerIdMatch, identity.ResolvedBy);
    }

    [Fact]
    public async Task PrefersAClubIdMatchOverAPartnerIdMatch()
    {
        var resolver = Build(
            Row("Partner Brand", "999", "140", "partner.example.com"),
            Row("Club Brand", "140", "777", "club.example.com"));

        var identity = await resolver.ResolveAsync("club:140");

        Assert.Equal("Club Brand", identity.BrandName);
        Assert.Equal(ClubResolution.ClubIdMatch, identity.ResolvedBy);
    }

    [Fact]
    public async Task ResolvesAHostKeyAgainstTheExactUrl()
    {
        var resolver = Build(Row("Smarter Getaways", "15662", "239", "members.smartergetaways.com"));

        var identity = await resolver.ResolveAsync("host:MEMBERS.SMARTERGETAWAYS.COM");

        Assert.Equal("Smarter Getaways", identity.BrandName);
        Assert.Equal(ClubResolution.HostExactMatch, identity.ResolvedBy);
    }

    [Fact]
    public async Task FallsBackToTheBaseDomainWhenTheCatalogueRecordsASiblingHostOfTheSameBrand()
    {
        var resolver = Build(Row("Smarter Getaways", "15662", "239", "members.smartergetaways.com"));

        var identity = await resolver.ResolveAsync("host:www.smartergetaways.com");

        Assert.Equal("Smarter Getaways", identity.BrandName);
        Assert.Equal(ClubResolution.HostBaseDomainMatch, identity.ResolvedBy);
    }

    [Fact]
    public async Task DoesNotMatchHostsThatMerelyShareASuffixWithoutADomainBoundary()
    {
        // travel.mysynchronytravel.com and travel.synchronytravel.com look related and a naive
        // "ends with" check pairs them, but mysynchronytravel.com and synchronytravel.com are
        // different registrable domains. Guessing here would attach a real brand to traffic that
        // may not belong to it, which is worse than reporting the club as unknown.
        var resolver = Build(Row("Synchrony Travel", "15525", "306", "travel.mysynchronytravel.com"));

        var identity = await resolver.ResolveAsync("host:travel.synchronytravel.com");

        Assert.Null(identity.BrandName);
        Assert.Equal(ClubResolution.Unresolved, identity.ResolvedBy);
    }

    [Fact]
    public async Task ReportsAnUnknownHostAsUnresolvedInsteadOfGuessingABrand()
    {
        var resolver = Build(Row("T-Mobile", "15639", "308", "stays.tmobiletravel.com"));

        var identity = await resolver.ResolveAsync("host:members.jifutravel.net");

        Assert.Null(identity.BrandName);
        Assert.False(identity.IsResolved);
        Assert.Equal(ClubResolution.Unresolved, identity.ResolvedBy);
        Assert.Equal(ClubEntityKind.Host, identity.Kind);
    }

    [Fact]
    public async Task KeepsTheRawClubIdEvenWhenTheBrandIsUnknown()
    {
        var resolver = Build(Row("T-Mobile", "15639", "308", "stays.tmobiletravel.com"));

        var identity = await resolver.ResolveAsync("club:2020606");

        Assert.Equal("2020606", identity.ClubId);
        Assert.Null(identity.BrandName);
        Assert.Null(identity.ClubName);
        Assert.Equal(ClubResolution.Unresolved, identity.ResolvedBy);
    }

    [Theory]
    [InlineData("[host/club unavailable]")]
    [InlineData("")]
    [InlineData("nonsense")]
    [InlineData("club:")]
    public async Task TreatsAnUnparseableEntityKeyAsUnknownRatherThanThrowing(string entityKey)
    {
        var resolver = Build(Row("T-Mobile", "15639", "308", "stays.tmobiletravel.com"));

        var identity = await resolver.ResolveAsync(entityKey);

        Assert.Equal(ClubEntityKind.Unknown, identity.Kind);
        Assert.Equal(ClubResolution.Unresolved, identity.ResolvedBy);
        Assert.Null(identity.BrandName);
    }

    [Fact]
    public async Task DegradesToUnresolvedWhenTheCatalogueIsNotConfigured()
    {
        var resolver = new ClubIdentityResolver(new FakeClubInventoryStore([], isConfigured: false), NullLogger<ClubIdentityResolver>.Instance);

        var identity = await resolver.ResolveAsync("club:15639");

        Assert.Equal(ClubResolution.Unresolved, identity.ResolvedBy);
        Assert.Equal("15639", identity.ClubId);
    }

    [Fact]
    public async Task DegradesToUnresolvedWhenTheCatalogueReadThrows()
    {
        var resolver = new ClubIdentityResolver(new ThrowingClubInventoryStore(), NullLogger<ClubIdentityResolver>.Instance);

        var identity = await resolver.ResolveAsync("host:stays.tmobiletravel.com");

        Assert.Equal(ClubResolution.Unresolved, identity.ResolvedBy);
        Assert.Null(identity.BrandName);
    }

    [Fact]
    public async Task ReadsTheCatalogueOnceAndServesLaterLookupsFromCache()
    {
        var catalog = new FakeClubInventoryStore([Row("T-Mobile", "15639", "308", "stays.tmobiletravel.com")]);
        var resolver = new ClubIdentityResolver(catalog, NullLogger<ClubIdentityResolver>.Instance);

        await resolver.ResolveAsync("club:15639");
        await resolver.ResolveAsync("club:15639");
        await resolver.ResolveAsync("host:stays.tmobiletravel.com");

        Assert.Equal(1, catalog.ReadCount);
    }

    private sealed class FakeClubInventoryStore(IReadOnlyList<ClubInventoryRow> clubs, bool isConfigured = true) : IClubInventoryStore
    {
        public int ReadCount { get; private set; }

        public bool IsConfigured => isConfigured;

        public Task<IReadOnlyList<ClubInventoryRow>> GetAllAsync(CancellationToken cancellationToken)
        {
            ReadCount++;
            return Task.FromResult(clubs);
        }
    }

    private sealed class ThrowingClubInventoryStore : IClubInventoryStore
    {
        public bool IsConfigured => true;

        public Task<IReadOnlyList<ClubInventoryRow>> GetAllAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("brand.ClubInventory is unreachable");
    }
}
