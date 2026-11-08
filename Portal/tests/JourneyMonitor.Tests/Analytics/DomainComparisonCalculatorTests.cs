using JourneyMonitor.Core.Analytics;
using JourneyMonitor.Core.Domain;

namespace JourneyMonitor.Tests.Analytics;

public class DomainComparisonCalculatorTests
{
    [Theory]
    [InlineData("hotels")]
    [InlineData("cruises")]
    [InlineData("cars")]
    public void A_booking_session_counts_when_its_search_was_an_earlier_session(string domain)
    {
        var rows = new[]
        {
            Journey(domain, "search-1", passedSearch: true, booked: false),
            Journey(domain, "search-2", passedSearch: true, booked: false),
            Journey(domain, "booked-later", passedSearch: false, booked: true),
        };
        var comparison = domain switch
        {
            "hotels" => DomainComparisonCalculator.CalculateHotel(rows),
            "cruises" => DomainComparisonCalculator.CalculateCruise(rows),
            "cars" => DomainComparisonCalculator.CalculateCars(rows),
            _ => throw new ArgumentOutOfRangeException(nameof(domain)),
        };

        Assert.Equal(3, comparison.Journeys);
        Assert.Equal(1d / 2d, comparison.ConversionRate);
    }

    private static CustomerJourney Journey(string domain, string id, bool passedSearch, bool booked) => new()
    {
        Id = id,
        CorrelationId = id,
        Domain = domain,
        Customer = "cust",
        Environment = "production",
        PassedSearch = passedSearch,
        IsBookSuccessful = booked,
        BookOk = booked ? 1 : 0,
    };
}
