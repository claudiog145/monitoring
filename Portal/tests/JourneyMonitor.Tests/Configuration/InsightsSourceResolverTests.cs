using JourneyMonitor.Core.Configuration;
using JourneyMonitor.Core.Options;

namespace JourneyMonitor.Tests.Configuration;

/// <summary>
/// Characterization tests ported from test/insights-config.test.js: same fixture GUIDs and
/// assertions, proving InsightsSourceResolver matches resolveInsightsSource()/isTenantId()/
/// normalizeEnvironment() behavior exactly, including the Cars-relabeled-error case
/// (query-appinsights.js sourceForDomain).
/// </summary>
public class InsightsSourceResolverTests
{
    private const string HotelTenant = "11111111-2222-3333-4444-555555555555";
    private const string AirTenant = "b0298d22-1db0-4550-8fa6-a116a5ab4208";
    private const string CruiseAppId = "f5f675bd-4148-4a12-bc76-1e9ed1aa1156";

    [Fact]
    public void ResolveInsightsSourceKeepsTheLegacyTopLevelHotelAppId()
    {
        var cfg = new InsightsOptions { AppInsightsAppId = "hotel-guid" };
        var src = InsightsSourceResolver.Resolve(cfg, "hotels", null);
        Assert.Equal("hotels", src.Domain);
        Assert.Equal("hotel-guid", src.AppInsightsAppId);
        Assert.Equal("", src.TenantId);
    }

    [Fact]
    public void ResolveInsightsSourceStagingUsesTheHotelUatAppInsights()
    {
        const string uatAppId = "f08f6072-4d42-4bcf-a8e9-aeb4a331757e";
        var cfg = new InsightsOptions
        {
            Hotels = new HotelInsightsOptions
            {
                TenantId = HotelTenant,
                AppInsightsAppId = "hotel-prod-guid",
                Staging = new DomainInsightsSlot { AppInsightsAppId = uatAppId },
            },
        };
        var src = InsightsSourceResolver.Resolve(cfg, "hotels", "staging");
        Assert.Equal("staging", src.Environment);
        Assert.Equal(uatAppId, src.AppInsightsAppId);
        Assert.Equal(HotelTenant, src.TenantId);
    }

    [Fact]
    public void ResolveInsightsSourceRejectsHotelDevelopmentAndMissingStagingIds()
    {
        var cfg = new InsightsOptions { Hotels = new HotelInsightsOptions { TenantId = HotelTenant, AppInsightsAppId = "hotel-prod-guid" } };
        Assert.Contains("development", Assert.Throws<InsightsConfigurationException>(() => InsightsSourceResolver.Resolve(cfg, "hotels", "development")).Message);
        Assert.Contains("staging App Insights", Assert.Throws<InsightsConfigurationException>(() => InsightsSourceResolver.Resolve(cfg, "hotels", "staging")).Message);
    }

    [Fact]
    public void ResolveInsightsSourceReadsAirTenantAndAppIdWithoutReplacingHotel()
    {
        var cfg = new InsightsOptions
        {
            AppInsightsAppId = "hotel-guid",
            Hotels = new HotelInsightsOptions { TenantId = HotelTenant },
            Air = new AirInsightsOptions { TenantId = AirTenant, AppInsightsAppId = "air-guid" },
        };
        var hotel = InsightsSourceResolver.Resolve(cfg, "hotels", null);
        var air = InsightsSourceResolver.Resolve(cfg, "air", null);
        Assert.Equal("hotel-guid", hotel.AppInsightsAppId);
        Assert.Equal(HotelTenant, hotel.TenantId);
        Assert.Equal("air-guid", air.AppInsightsAppId);
        Assert.Equal(AirTenant, air.TenantId);
        Assert.Equal("air", air.Domain);
        Assert.Equal("production", air.Environment);
        Assert.Equal(InsightsSourceResolver.AirCrossResource["production"].UiResource, air.UiResource);
        Assert.Equal(InsightsSourceResolver.AirCrossResource["production"].ApiResource, air.ApiResource);
    }

    [Fact]
    public void ResolveInsightsSourceThrowsWhenAirIsRequestedButNotConfigured()
    {
        var cfg = new InsightsOptions { AppInsightsAppId = "hotel-guid" };
        var ex = Assert.Throws<InsightsConfigurationException>(() => InsightsSourceResolver.Resolve(cfg, "air", null));
        Assert.Contains("air", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveInsightsSourceUsesTheCruiseWebResourceAndHotelTenant()
    {
        var cfg = new InsightsOptions
        {
            Hotels = new HotelInsightsOptions { TenantId = HotelTenant },
            Cruises = new CruiseInsightsOptions { AppInsightsAppId = CruiseAppId },
        };
        var src = InsightsSourceResolver.Resolve(cfg, "cruises", null);
        Assert.Equal("cruises", src.Domain);
        Assert.Equal("production", src.Environment);
        Assert.Equal(HotelTenant, src.TenantId);
        Assert.Equal(CruiseAppId, src.AppInsightsAppId);
        Assert.Equal(InsightsSourceResolver.CruiseCrossResource["production"].ClubResource, src.ClubResource);
        Assert.Equal(InsightsSourceResolver.CruiseCrossResource["production"].CoreResource, src.CoreResource);
        Assert.Equal("cruise.tmobiletravel.com", src.DefaultHost);
    }

    [Fact]
    public void NormalizeEnvironmentMapsAliasesAndRejectsJunk()
    {
        Assert.Equal("production", InsightsEnvironment.Normalize(""));
        Assert.Equal("staging", InsightsEnvironment.Normalize("UAT"));
        Assert.Throws<InsightsConfigurationException>(() => InsightsEnvironment.Normalize("prod; drop"));
    }

    [Theory]
    [InlineData("b0298d22-1db0-4550-8fa6-a116a5ab4208", true)]
    [InlineData("B0298D22-1DB0-4550-8FA6-A116A5AB4208", true)]
    [InlineData("b0298d22-1db0-4550-8fa6-a116a5ab4208\n& whoami", false)]
    [InlineData("\"b0298d22-1db0-4550-8fa6-a116a5ab4208\"", false)]
    [InlineData("contoso.onmicrosoft.com", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsTenantIdAcceptsOnlyABareGuid(string? value, bool expected)
    {
        Assert.Equal(expected, InsightsSourceResolver.IsTenantId(value));
    }

    [Fact]
    public void CarsDevelopmentAppInsightsErrorIsLabeledCarsNotHotel()
    {
        var cfg = new InsightsOptions { Hotels = new HotelInsightsOptions { TenantId = HotelTenant, AppInsightsAppId = "hotel-guid" } };
        var ex = Assert.Throws<InsightsConfigurationException>(() => InsightsSourceResolver.ResolveForDomain(cfg, "cars", "development"));
        Assert.Contains("Cars development App Insights is not configured", ex.Message);
    }
}
