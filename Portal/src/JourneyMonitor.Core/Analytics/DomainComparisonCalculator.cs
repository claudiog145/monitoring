using JourneyMonitor.Core.Domain;

namespace JourneyMonitor.Core.Analytics;

public sealed class DomainComparison
{
    public required string Domain { get; set; }
    public int Journeys { get; set; }
    public double? ConversionRate { get; set; }
    public double? P95LatencyMs { get; set; }
    public int Exceptions { get; set; }
}

/// <summary>
/// Builds Domain Comparison rows (Analytics tab) from mapped KQL journeys.
/// Direct port of domain-comparison.js. Conversion is search-to-booking for every
/// vertical, but each vertical has its own "reached search" predicate and Cruise uses a
/// different P95 formula (average, not sample-percentile) - preserve exactly, do not unify.
/// </summary>
public static class DomainComparisonCalculator
{
    public static readonly string[] ComparisonDomains = ["hotels", "cruises"];

    /// <summary>All is Hotels and Cruises. A named domain compares only itself.</summary>
    public static IReadOnlyList<string> ComparisonDomainsFor(string selected) =>
        selected == "all" ? ComparisonDomains : ComparisonDomains.Where(d => d == selected).ToArray();

    private static bool HotelReachedSearch(CustomerJourney j) => j.PassedSearch || j.Stages.Any(s => s.Name == "02 Hotel Search");
    private static bool HotelBooked(CustomerJourney j) => j.IsBookSuccessful || j.BookOk > 0;
    private static bool AirReachedSearch(CustomerJourney j) => j.PassedSearch || j.Stages.Any(s => s.Name == "01 Flight Search");
    private static bool AirBooked(CustomerJourney j) => j.IsBookSuccessful || j.BookOk > 0;
    private static bool CarsReachedSearch(CustomerJourney j) => j.PassedSearch || j.Stages.Any(s => s.Name == "02 Car Search");
    private static bool CarsBooked(CustomerJourney j) => j.IsBookSuccessful || j.BookOk > 0;

    private static double? SamplePercentile(IReadOnlyList<double> values, double p)
    {
        var nums = values.OrderBy(v => v).ToList();
        if (nums.Count == 0) return null;
        var idx = Math.Min(nums.Count - 1, Math.Max(0, (int)Math.Ceiling(p / 100 * nums.Count) - 1));
        return Math.Round(nums[idx]);
    }

    public static bool ShouldRefresh(bool analyticsVisible, bool stale) => analyticsVisible && stale;

    private static DomainComparison Build(List<CustomerJourney> rows, string domain, Func<CustomerJourney, bool> reachedSearch,
        Func<CustomerJourney, bool> booked, bool sampleP95)
    {
        var searched = rows.Where(reachedSearch).ToList();
        var successful = searched.Where(booked).ToList();
        var latencies = rows.Where(j => j.OverallP95Ms is not null).Select(j => j.OverallP95Ms!.Value).ToList();
        double? p95 = sampleP95
            ? SamplePercentile(latencies, 95)
            : (latencies.Count > 0 ? Math.Round(latencies.Average()) : null);

        return new DomainComparison
        {
            Domain = domain,
            Journeys = rows.Count,
            ConversionRate = searched.Count > 0 ? (double)successful.Count / searched.Count : null,
            P95LatencyMs = p95,
            Exceptions = rows.Sum(j => Math.Max(0, j.Exceptions)),
        };
    }

    public static DomainComparison CalculateHotel(IEnumerable<CustomerJourney> journeys) =>
        Build(journeys.Where(j => j.Domain == "hotels").ToList(), "hotels", HotelReachedSearch, HotelBooked, sampleP95: true);

    public static DomainComparison CalculateAir(IEnumerable<CustomerJourney> journeys) =>
        Build(journeys.Where(j => j.Domain == "air").ToList(), "air", AirReachedSearch, AirBooked, sampleP95: true);

    public static DomainComparison CalculateCars(IEnumerable<CustomerJourney> journeys) =>
        Build(journeys.Where(j => j.Domain == "cars").ToList(), "cars", CarsReachedSearch, CarsBooked, sampleP95: true);

    public static DomainComparison CalculateCruise(IEnumerable<CustomerJourney> journeys) =>
        Build(journeys.Where(j => j.Domain == "cruises").ToList(), "cruises", j => j.PassedSearch, j => j.IsBookSuccessful, sampleP95: false);
}
