using JourneyMonitor.Infrastructure.Kql;

namespace JourneyMonitor.Tests.Monitoring;

/// <summary>Confirms which domains have a latency-spikes KQL authored today (Phase 4 of the plan) and which don't yet.</summary>
public class KqlQueryTextProviderLatencySpikesTests
{
    [Theory]
    [InlineData("cars")]
    [InlineData("hotels")]
    [InlineData("cruises")]
    public void AuthoredDomainsReturnNonEmptyKql(string domain)
    {
        var provider = new KqlQueryTextProvider();
        var kql = provider.GetLatencySpikesKql(domain);
        Assert.False(string.IsNullOrWhiteSpace(kql));
        Assert.Contains("{{WINDOW_MINUTES}}", kql);
        Assert.Contains("{{FLOOR_MS}}", kql);
    }

    [Fact]
    public void IncidentExceptionsKqlDropsBrowserNoiseAndExposesTheWindow()
    {
        var kql = new KqlQueryTextProvider().GetIncidentExceptionsKql();
        Assert.Contains("let win = 15m;", kql);
        Assert.Contains("type !startswith \"ErrorEvent\"", kql);
        Assert.Contains("join kind=leftouter requestUrls on operation_Id", kql);
        Assert.Contains("RequestUrl = tostring(url)", kql);
        Assert.Contains("ItemId", kql);
        Assert.Contains("ClubId", kql);
        Assert.Contains("customDimensions.partnerId", kql);
        Assert.Contains("customDimensions.memberid", kql);
        Assert.Contains("customDimensions.session_id", kql);
        Assert.DoesNotContain("PartnerId == \"308\"", kql);
    }

    [Fact]
    public void AirThrowsBecauseItIsNotAuthoredYet()
    {
        var provider = new KqlQueryTextProvider();
        Assert.Throws<ArgumentException>(() => provider.GetLatencySpikesKql("air"));
    }
}
