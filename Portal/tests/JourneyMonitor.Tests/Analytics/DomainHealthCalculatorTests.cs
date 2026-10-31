using JourneyMonitor.Core.Analytics;
using JourneyMonitor.Core.Domain;

namespace JourneyMonitor.Tests.Analytics;

/// <summary>
/// Characterization tests ported directly from test/domain-health.test.js (assertions and
/// fixture values kept identical) to prove DomainHealthCalculator is behaviorally equivalent
/// to the original domain-health.js.
/// </summary>
public class DomainHealthCalculatorTests
{
    private static CustomerJourney Journey(
        string domain = "hotels",
        string outcome = "Booked",
        double totalDurationMs = 1000,
        int totalCalls = 10,
        int failedCalls = 0,
        string timestamp = "2026-09-02T10:00:00Z") => new()
    {
        Id = "j1",
        CorrelationId = "c1",
        Domain = domain,
        Customer = "cust",
        Environment = "production",
        Outcome = outcome,
        TotalDurationMs = totalDurationMs,
        TotalCalls = totalCalls,
        FailedCalls = failedCalls,
        Timestamp = timestamp,
    };

    [Fact]
    public void CalculatesEveryDomainHealthMetricFromMappedKqlValues()
    {
        var health = DomainHealthCalculator.Calculate(
        [
            Journey(),
            Journey(outcome: "Purchase failed x1", totalDurationMs: 3000, failedCalls: 2, timestamp: "2026-09-02T10:05:00Z"),
            Journey(domain: "cars"),
        ], "hotels");

        Assert.Equal(90, health.Availability);
        Assert.Equal(10, health.ErrorRate);
        Assert.Equal(2000, health.LatencyMs);
        Assert.Equal(1, health.Incidents);
        Assert.Equal("critical", health.Status);
        Assert.Equal(2, health.SampleSize);
    }

    [Fact]
    public void ReturnsUnknownValuesWhenAVerticalHasNoKqlRows()
    {
        var health = DomainHealthCalculator.Calculate([], "shared");
        Assert.Equal("unknown", health.Status);
        Assert.Null(health.Availability);
        Assert.Null(health.ErrorRate);
        Assert.Null(health.LatencyMs);
        Assert.Null(health.Incidents);
        Assert.Equal("no KQL data", health.Trend.Label);
    }

    [Theory]
    [InlineData(100, 0, "healthy")]
    [InlineData(99, 0, "degraded")]
    [InlineData(100, 1, "degraded")]
    [InlineData(94.9, 0, "critical")]
    [InlineData(100, 5, "critical")]
    public void ClassifiesAggregateStatusByAvailabilityAndCallErrorRate(double availability, double errorRate, string expected)
    {
        Assert.Equal(expected, DomainHealthCalculator.DomainStatus(availability, errorRate));
    }

    [Fact]
    public void TrendComparesTheRecentHalfOfTheSampleToTheOlderHalf()
    {
        var rows = new List<CustomerJourney>
        {
            Journey(failedCalls: 2, timestamp: "2026-09-02T10:00:00Z"),
            Journey(failedCalls: 2, timestamp: "2026-09-02T10:01:00Z"),
            Journey(timestamp: "2026-09-02T10:02:00Z"),
            Journey(timestamp: "2026-09-02T10:03:00Z"),
        };
        var health = DomainHealthCalculator.Calculate(rows, "hotels");
        Assert.Equal("improving", health.Trend.Direction);
    }
}
