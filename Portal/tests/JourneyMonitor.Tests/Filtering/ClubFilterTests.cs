using JourneyMonitor.Core.Filtering;
using JourneyMonitor.Infrastructure.Kql;

namespace JourneyMonitor.Tests.Filtering;

/// <summary>
/// Characterization tests ported from test/club-filter.test.js: same fixture values and
/// assertions, run against the same embedded KQL text used by JourneyMonitor.Functions.
/// </summary>
public class ClubFilterTests
{
    private static readonly KqlQueryTextProvider Kql = new();

    [Fact]
    public void ClubLabelPrefersTheMarketingNameThenTheHostThenTheRawId()
    {
        Assert.Equal("T-Mobile / T Life", ClubFilter.ClubLabel("15639", "stays.tmobiletravel.com"));
        Assert.Equal("members.ulifestylecollection.com (14945)", ClubFilter.ClubLabel("14945", "members.ulifestylecollection.com"));
        Assert.Equal("99999", ClubFilter.ClubLabel("99999", ""));
    }

    [Fact]
    public void ClubControlStateDisablesClubUntilATravelDomainIsChosen()
    {
        foreach (var domain in new[] { "all", "shared" })
        {
            var state = ClubFilter.ControlState(domain);
            Assert.True(state.Disabled);
            Assert.False(state.CanList);
            Assert.Equal("Select a domain first", state.Placeholder);
        }
    }

    [Theory]
    [InlineData("hotels", "All hotel clubs")]
    [InlineData("cars", "All car clubs")]
    [InlineData("air", "All air clubs")]
    [InlineData("cruises", "All cruise clubs")]
    public void ClubControlStateListsClubsForEveryWiredTravelDomain(string domain, string placeholder)
    {
        var state = ClubFilter.ControlState(domain);
        Assert.False(state.Disabled);
        Assert.True(state.CanList);
        Assert.Equal(placeholder, state.Placeholder);
    }

    [Theory]
    [InlineData(50, 50)]
    [InlineData(200, 200)]
    [InlineData(15, 50)] // anything not in the allow-list normalizes to INIT_ROW_CAP
    [InlineData(100, 50)]
    [InlineData(0, 50)]
    public void NormalizeRowCapOnlyAcceptsTheAllowListedCaps(int input, int expected)
    {
        Assert.Equal(expected, ClubFilter.NormalizeRowCap(input));
    }

    [Fact]
    public void InjectJourneyKqlRewritesWinHostClubAndRowCapLiterally()
    {
        var raw = Kql.GetJourneyKql("hotels");
        var injected = ClubFilter.InjectJourneyKql(raw, "2h", "15639", "stays.tmobiletravel.com", rowCap: 200);

        Assert.Contains("let win = 2h;", injected);
        Assert.Contains("let tmoHost = \"stays.tmobiletravel.com\";", injected);
        Assert.Contains("let tmoClub = \"15639\";", injected);
        Assert.Contains("let tmoPartner = \"\";", injected);
        Assert.Contains("false or Club == tmoClub", injected);
        Assert.DoesNotContain("PartnerId == tmoPartner", injected);
        Assert.Contains("top 200 by LastSeen desc", injected);
    }

    [Fact]
    public void InjectJourneyKqlRewritesHostPartnerAndClubFromTheSelectedClub()
    {
        var hotels = ClubFilter.InjectJourneyKql(
            Kql.GetJourneyKql("hotels"), "2h", "15893", "members.aarparrivia.com", partnerId: "333");
        Assert.Contains("let tmoHost = \"members.aarparrivia.com\";", hotels);
        Assert.Contains("let tmoPartner = \"333\";", hotels);
        Assert.Contains("let tmoClub = \"15893\";", hotels);

        var cruises = Kql.GetJourneyKql("cruises");
        Assert.DoesNotContain("PartnerId == \"308\"", cruises);
        var injectedCruises = ClubFilter.InjectJourneyKql(cruises, "15m", "333", "members.aarparrivia.com", partnerId: "333");
        Assert.Contains("let tmoPartner = \"333\";", injectedCruises);
        Assert.Contains("PartnerId == tmoPartner", injectedCruises);
        Assert.DoesNotContain("PartnerId == tmoPartner or url has tmoHost", injectedCruises);

        var air = ClubFilter.InjectAirJourneyKql(Kql.GetAirJourneyKql(), "15m", "239", partnerId: "239");
        Assert.Contains("let tmoClub = \"239\";", air);
        Assert.Contains("let tmoPartner = \"239\";", air);
        Assert.DoesNotContain("let tmoPartner = \"308\";", air);
    }

    [Fact]
    public void InjectJourneyKqlRejectsAnInvalidHost()
    {
        var raw = Kql.GetJourneyKql("hotels");
        Assert.Throws<ClubFilterException>(() => ClubFilter.InjectJourneyKql(raw, "2h", "15639", "not a host"));
    }

    [Fact]
    public void InjectAirJourneyKqlRewritesWinAndClubOnly()
    {
        var raw = Kql.GetAirJourneyKql();
        var injected = ClubFilter.InjectAirJourneyKql(raw, "15m", "308", rowCap: 50);

        Assert.Contains("let win = 15m;", injected);
        Assert.Contains("let tmoClub = \"308\";", injected);
    }

    [Theory]
    [InlineData("15m", 15)]
    [InlineData("1h", 60)]
    [InlineData("6h", 360)]
    [InlineData("24h", 1440)]
    [InlineData("7d", 10080)]
    public void TryParseWindowAcceptsPortalTimeRangeTokens(string win, int minutes)
    {
        Assert.True(ClubFilter.TryParseWindow(win, out var duration));
        Assert.Equal(TimeSpan.FromMinutes(minutes), duration);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("yesterday")]
    [InlineData("15")]
    public void TryParseWindowRejectsUnknownTokens(string? win)
    {
        Assert.False(ClubFilter.TryParseWindow(win, out _));
    }

    [Fact]
    public void InjectDashboardKqlRewritesWinAndHostAndDropsRender()
    {
        var raw = Kql.GetDashboardChartKql("hotels", "step_latency_p95");
        var injected = ClubFilter.InjectDashboardKql(raw, "4h", "15639", "stays.tmobiletravel.com");

        Assert.Contains("let win = 4h;", injected);
        Assert.Contains("let tmoHost = \"stays.tmobiletravel.com\";", injected);
        Assert.DoesNotContain("render", injected, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DashboardChartsAreMappedForHotelAndCruiseOnly()
    {
        Assert.Equal(2, JourneyMonitor.Core.Abstractions.DashboardCharts.ForDomain("hotels").Count);
        Assert.Equal(2, JourneyMonitor.Core.Abstractions.DashboardCharts.ForDomain("cruises").Count);
        Assert.Empty(JourneyMonitor.Core.Abstractions.DashboardCharts.ForDomain("cars"));
        Assert.Empty(JourneyMonitor.Core.Abstractions.DashboardCharts.ForDomain("air"));
    }

    [Fact]
    public void InjectVerticalJourneyKqlCapsEachHotelClubAndEachCruisePartner()
    {
        var hotels = ClubFilter.InjectVerticalJourneyKql(
            "hotels",
            Kql.GetJourneyKql("hotels"),
            "15m",
            ["15639", "15827"],
            ["stays.tmobiletravel.com", "book.wesalute.com"],
            ["308", "323"],
            rowCap: 200);
        Assert.Contains("let win = 15m;", hotels);
        Assert.Contains("url has_any (tmoHosts)", hotels);
        Assert.Contains("Club in (tmoClubs) or PartnerId in (tmoPartners)", hotels);
        Assert.Contains("| where Club in (tmoClubs)", hotels);
        Assert.Contains("let clubByUser =", hotels);
        Assert.Contains("| join kind=inner userByCorr on Corr", hotels);
        Assert.DoesNotContain("let hostUser =", hotels);
        Assert.Contains("_Rank <= 200", hotels);
        Assert.DoesNotContain("| where url has tmoHost", hotels);
        Assert.DoesNotContain("top 200 by LastSeen desc", hotels);

        var cruises = ClubFilter.InjectVerticalJourneyKql(
            "cruises",
            Kql.GetJourneyKql("cruises"),
            "15m",
            ["308", "333"],
            ["cruise.tmobiletravel.com", "members.aarparrivia.com"],
            ["308", "333"],
            rowCap: 200);
        Assert.Contains("PartnerId in (tmoPartners)", cruises);
        Assert.Contains("let partnerBySession =", cruises);
        Assert.Contains("extend ClubId = PartnerId", cruises);
        Assert.Contains("_Rank <= 200", cruises);
        Assert.DoesNotContain("PartnerId == tmoPartner", cruises);
    }

    [Fact]
    public void StagingHotelVerticalQueryFiltersByHostDomainInsteadOfClubId()
    {
        var hotels = ClubFilter.InjectVerticalJourneyKql(
            "hotels",
            Kql.GetJourneyKql("hotels"),
            "15m",
            ["15639", "15039"],
            ["tmobile.saveonuat.com", "travel.mysynchronytravel.saveonuat.com"],
            ["308", "306"],
            rowCap: 200,
            environment: "staging");

        Assert.Contains("url has_any (tmoHosts)", hotels);
        Assert.Contains("tostring(customDimensions.site) has_any (tmoHosts)", hotels);
        Assert.Contains("tmobile.saveonuat.com", hotels, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("travel.mysynchronytravel.saveonuat.com", hotels, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("| where UserId in (hostUser)", hotels);
        Assert.Contains("| join kind=inner userByHostCorr on Corr", hotels);
        Assert.Contains("| join kind=leftouter userByCorr on Corr", hotels);
        Assert.DoesNotContain("Club in (tmoClubs)", hotels);
        Assert.DoesNotContain("| join kind=inner userByCorr on Corr", hotels);
    }

    [Fact]
    public void InjectVerticalDashboardKqlGroupsHotelsByHostAndCruisesByPartner()
    {
        var hotelLatency = ClubFilter.InjectVerticalDashboardKql(
            "hotels",
            Kql.GetDashboardChartKql("hotels", "step_latency_p95"),
            "15m",
            ["stays.tmobiletravel.com", "book.wesalute.com"],
            []);
        Assert.Contains("Host in (tmoHosts)", hotelLatency);
        Assert.Contains("by bin(timestamp, 5m), Series, Host", hotelLatency);

        var hotelExceptions = ClubFilter.InjectVerticalDashboardKql(
            "hotels",
            Kql.GetDashboardChartKql("hotels", "exceptions_by_step"),
            "15m",
            ["stays.tmobiletravel.com"],
            []);
        Assert.Contains("distinct operation_Id, Stage, Host", hotelExceptions);
        Assert.Contains("by bin(timestamp, 5m), Stage, Host", hotelExceptions);

        var cruiseLatency = ClubFilter.InjectVerticalDashboardKql(
            "cruises",
            Kql.GetDashboardChartKql("cruises", "step_latency_p95"),
            "15m",
            ["cruise.tmobiletravel.com"],
            ["308", "333"]);
        Assert.Contains("PartnerId in (tmoPartners)", cruiseLatency);
        Assert.Contains("by UserId, SessionId, PartnerId;", cruiseLatency);
        Assert.Contains("Operation, PartnerId;", cruiseLatency);
        Assert.DoesNotContain("PartnerId == tmoPartner", cruiseLatency);

        var cruiseExceptions = ClubFilter.InjectVerticalDashboardKql(
            "cruises",
            Kql.GetDashboardChartKql("cruises", "exceptions_by_step"),
            "15m",
            ["cruise.tmobiletravel.com"],
            ["308", "333"]);
        Assert.Contains("| project operation_Id, Operation, PartnerId;", cruiseExceptions);
        Assert.Contains("by bin(timestamp, 1h), Operation, PartnerId", cruiseExceptions);
    }
}
