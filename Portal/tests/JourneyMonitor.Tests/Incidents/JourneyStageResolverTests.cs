using JourneyMonitor.Core.Incidents;

namespace JourneyMonitor.Tests.Incidents;

public class JourneyStageResolverTests
{
    [Theory]
    [InlineData("hotels", "https://stays.tmobiletravel.com/vacationclub/logincheck.aspx", "GET", "01 Login")]
    [InlineData("hotels", "/api/search.json", "POST", "02 Hotel Search")]
    [InlineData("hotels", "/api/BackgroundHotelSearch", "POST", "03 Hotel Results")]
    [InlineData("hotels", "/api/HotelDetails", "GET", "04 Hotel Details")]
    [InlineData("hotels", "/api/book.json", "POST", "05 PreBook")]
    [InlineData("hotels", "/reservations/Purchase", "POST", "06 Checkout")]
    [InlineData("hotels", "/reservations/CompletePurchase", "POST", "07 Booking")]
    [InlineData("hotels", "/reservations/PurchaseComplete", "GET", "08 Confirmation")]
    [InlineData("hotels", "/reservations/Cancel", "POST", "09 Cancellation")]
    [InlineData("hotels", "https://payments.arrivia.com/pay", "POST", "07 Booking")]
    [InlineData("cars", "/api/CarSearch", "POST", "02 Car Search")]
    [InlineData("cars", "/api/BackgroundCarSearch", "POST", "03 Car Results")]
    [InlineData("cars", "/cars/search.json", "GET", "03 Car Results")]
    [InlineData("cars", "/api/CarBooking", "POST", "05 PreBook")]
    [InlineData("air", "/svc/air/v1/flightsearch", "GET", "01 Flight Search")]
    [InlineData("air", "/payment", "POST", "05 Review & Pay")]
    [InlineData("air", "/getbooking", "GET", "06 Confirmation")]
    [InlineData("cruises", "/cruise-listing", "GET", "02 Cruise Listing")]
    [InlineData("cruises", "/cruisepayment", "POST", "09 Payment")]
    [InlineData("cruises", "/cruiseconfirm", "GET", "11 Confirmation")]
    public void UrlMapsToTheJourneyStep(string domain, string url, string operation, string stage)
    {
        Assert.Equal(stage, JourneyStageResolver.ForException(domain, url, operation));
    }

    [Fact]
    public void AppInsightsRoleNameIsNotAStage()
    {
        Assert.Equal(
            JourneyStageResolver.Unclassified,
            JourneyStageResolver.ForException("hotels", "https://stays.example/unknown-page", "Angular"));
    }

    [Fact]
    public void AlreadyResolvedStageNameIsKeptWhenTheUrlIsMissing()
    {
        Assert.Equal("02 Car Search", JourneyStageResolver.ForException("cars", null, "02 Car Search"));
    }
}
