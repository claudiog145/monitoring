using JourneyMonitor.Core.Domain;

namespace JourneyMonitor.Core.Analytics;

public sealed record DomainTrend(string Direction, string Label);

public sealed class DomainHealth
{
    public required string Domain { get; set; }
    public required string Status { get; set; }
    public double? Availability { get; set; }
    public double? ErrorRate { get; set; }
    public double? LatencyMs { get; set; }
    public int? Incidents { get; set; }
    public required DomainTrend Trend { get; set; }
    public int SampleSize { get; set; }
}

/// <summary>
/// Aggregates mapped journeys into Domain Health cards. Direct port of domain-health.js.
/// Kept separate from mapping so every filter-driven journey reload recomputes the same way.
/// </summary>
public static class DomainHealthCalculator
{
    private static string JourneyHealth(CustomerJourney journey)
    {
        var outcome = (!string.IsNullOrEmpty(journey.Outcome) ? journey.Outcome : journey.Status).ToLowerInvariant();
        if (outcome == "success" || outcome.StartsWith("booked")) return "healthy";
        if (outcome == "failed" || outcome.Contains("failed") || outcome.Contains("purchase attempted")) return "critical";
        if (outcome == "degraded" || outcome == "in_progress" || outcome.StartsWith("dropped") || outcome.Contains("cancellation")) return "degraded";
        return "unknown";
    }

    public static int JourneyCallCount(CustomerJourney journey)
    {
        if (journey.TotalCalls != 0) return Math.Max(0, journey.TotalCalls);
        return journey.Stages.Sum(s => Math.Max(0, s.RequestCount));
    }

    public static int JourneyFailedCalls(CustomerJourney journey)
    {
        if (journey.FailedCalls != 0) return Math.Max(0, journey.FailedCalls);
        return journey.Stages.Sum(stage => stage.Calls.Count(call => call.Error || (call.FailureRatePct ?? 0) > 0));
    }

    private static double? AvailabilityFor(IReadOnlyCollection<CustomerJourney> journeys)
    {
        if (journeys.Count == 0) return null;
        var totalCalls = journeys.Sum(JourneyCallCount);
        if (totalCalls == 0) return null;
        var failedCalls = journeys.Sum(JourneyFailedCalls);
        return Math.Max(0, 100 - (double)failedCalls / totalCalls * 100);
    }

    public static string DomainStatus(double? availability, double? errorRate)
    {
        if (availability is null) return "unknown";
        if (availability < 95 || errorRate >= 5) return "critical";
        if (availability < 99.9 || errorRate > 0) return "degraded";
        return "healthy";
    }

    private static DomainTrend Trend(List<CustomerJourney> journeys)
    {
        if (journeys.Count < 4) return new("stable", "stable");
        var sorted = journeys.OrderBy(j => ParseTimestamp(j.Timestamp)).ToList();
        var midpoint = sorted.Count / 2;
        var older = AvailabilityFor(sorted.Take(midpoint).ToList());
        var recent = AvailabilityFor(sorted.Skip(midpoint).ToList());
        if (older is null || recent is null) return new("unknown", "insufficient data");
        var delta = recent.Value - older.Value;
        if (delta > 0.5) return new("improving", "improving");
        if (delta < -0.5) return new("degrading", "degrading");
        return new("stable", "stable");
    }

    private static DateTimeOffset ParseTimestamp(string? value) =>
        DateTimeOffset.TryParse(value, out var dt) ? dt : DateTimeOffset.MinValue;

    public static DomainHealth Calculate(IEnumerable<CustomerJourney> journeys, string domain)
    {
        var rows = journeys.Where(j => j.Domain == domain).ToList();
        if (rows.Count == 0)
        {
            return new DomainHealth
            {
                Domain = domain,
                Status = "unknown",
                Trend = new("unknown", "no KQL data"),
                SampleSize = 0,
            };
        }

        var availability = AvailabilityFor(rows);
        var errorRate = availability is null ? (double?)null : 100 - availability;
        var latencyMs = Math.Round(rows.Sum(j => j.TotalDurationMs) / rows.Count);
        var incidents = rows.Count(j => JourneyFailedCalls(j) > 0 || JourneyHealth(j) == "critical");

        return new DomainHealth
        {
            Domain = domain,
            Status = DomainStatus(availability, errorRate),
            Availability = availability,
            ErrorRate = errorRate,
            LatencyMs = latencyMs,
            Incidents = incidents,
            Trend = Trend(rows),
            SampleSize = rows.Count,
        };
    }
}
