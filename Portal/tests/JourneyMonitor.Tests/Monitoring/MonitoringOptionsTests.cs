using JourneyMonitor.Core.Options;

namespace JourneyMonitor.Tests.Monitoring;

public class MonitoringOptionsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-5)]
    public void ResolvedSpikeThresholdMsFallsBackToDefaultForInvalidValues(int? configured)
    {
        var options = new MonitoringOptions { SpikeThresholdMs = configured };
        Assert.Equal(MonitoringOptions.DefaultSpikeThresholdMs, options.ResolvedSpikeThresholdMs);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    public void ResolvedSpikeOccurrenceThresholdFallsBackToDefaultForInvalidValues(int? configured)
    {
        // A 0 or negative occurrence threshold would otherwise divide-by-zero when
        // LatencySpikeDetectorService computes the severity ratio.
        var options = new MonitoringOptions { SpikeOccurrenceThreshold = configured };
        Assert.Equal(MonitoringOptions.DefaultSpikeOccurrenceThreshold, options.ResolvedSpikeOccurrenceThreshold);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-60)]
    public void ResolvedSpikeWindowMinutesFallsBackToDefaultForInvalidValues(int? configured)
    {
        var options = new MonitoringOptions { SpikeWindowMinutes = configured };
        Assert.Equal(MonitoringOptions.DefaultSpikeWindowMinutes, options.ResolvedSpikeWindowMinutes);
    }

    [Fact]
    public void ResolvedValuesUseTheConfiguredValueWhenValid()
    {
        var options = new MonitoringOptions { SpikeThresholdMs = 250, SpikeOccurrenceThreshold = 3, SpikeWindowMinutes = 30 };
        Assert.Equal(250, options.ResolvedSpikeThresholdMs);
        Assert.Equal(3, options.ResolvedSpikeOccurrenceThreshold);
        Assert.Equal(30, options.ResolvedSpikeWindowMinutes);
    }

    [Theory]
    [InlineData(null, "cars", false)]
    [InlineData("", "cars", false)]
    [InlineData("cars", "cars", true)]
    [InlineData("cars", "CARS", true)]
    [InlineData("hotels,cars,cruises", "cars", true)]
    [InlineData("hotels,cruises", "cars", false)]
    public void UsesSqlDetectorMatchesDomainsCaseInsensitively(string? configured, string domain, bool expected)
    {
        var options = new MonitoringOptions { SqlDetectorDomains = configured };
        Assert.Equal(expected, options.UsesSqlDetector(domain));
    }

    [Theory]
    [InlineData(null, "cars", false)]
    [InlineData("", "cars", false)]
    [InlineData("cars", "cars", true)]
    [InlineData("cars", "CARS", true)]
    [InlineData("hotels,cruises", "cars", false)]
    public void IsShadowDomainMatchesDomainsCaseInsensitively(string? configured, string domain, bool expected)
    {
        var options = new MonitoringOptions { ShadowDomains = configured };
        Assert.Equal(expected, options.IsShadowDomain(domain));
    }
}
