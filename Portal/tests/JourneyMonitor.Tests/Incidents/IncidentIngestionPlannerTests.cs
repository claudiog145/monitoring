using JourneyMonitor.Core.Incidents;

namespace JourneyMonitor.Tests.Incidents;

/// <summary>
/// WI 238073 comment: one incident per club, ErrorEvent dropped, ItemId deduped,
/// and the example severity bands (224 Critical, 41 High, 12 Medium).
/// </summary>
public class IncidentIngestionPlannerTests
{
    private static readonly DateTimeOffset Noon = new(2026, 9, 25, 13, 4, 52, TimeSpan.Zero);

    [Fact]
    public void FingerprintKeepsDifferencesPastTheOld200CharacterCut()
    {
        var sharedTypePrefix = new string('a', 176);
        var left = IncidentIngestionPlanner.BuildFingerprint(
            "hotels", "15639", "production", sharedTypePrefix + "X", "GET /vacationclub/logincheck.aspx");
        var right = IncidentIngestionPlanner.BuildFingerprint(
            "hotels", "15639", "production", sharedTypePrefix + "Y", "GET /vacationclub/logincheck.aspx");

        Assert.NotEqual(left, right);
        Assert.True(left.Length > 200);
        Assert.EndsWith("X|GET /vacationclub/logincheck.aspx", left);
        Assert.EndsWith("Y|GET /vacationclub/logincheck.aspx", right);
    }

    [Fact]
    public void FingerprintKeepsAFullExceptionTypeAndOperation()
    {
        var exceptionType = new string('T', 256);
        var operation = new string('O', 256);
        var fingerprint = IncidentIngestionPlanner.BuildFingerprint(
            "hotels", "15639", "production", exceptionType, operation);

        Assert.Contains(exceptionType, fingerprint);
        Assert.EndsWith(operation, fingerprint);
        Assert.Equal(6 + 1 + 5 + 1 + 10 + 1 + 256 + 1 + 256, fingerprint.Length);
        Assert.True(fingerprint.Length <= 612);
    }

    [Fact]
    public void SameFingerprintWithDifferentCaseIsOneIncident()
    {
        var plan = IncidentIngestionPlanner.Plan("hotels", "production",
        [
            Signal("System.NullReferenceException", "SOR", "/vacationclub/logincheck.aspx", "item-1"),
            Signal("system.nullreferenceexception", "sor", "/vacationclub/logincheck.aspx", "item-2"),
        ]);

        var incident = Assert.Single(plan.Incidents);
        Assert.Equal(2, incident.Occurrences.Count);
        Assert.Equal("SOR", incident.ClubId);
    }

    [Theory]
    [InlineData(224, "Critical")]
    [InlineData(100, "Critical")]
    [InlineData(41, "High")]
    [InlineData(25, "High")]
    [InlineData(12, "Medium")]
    [InlineData(5, "Medium")]
    [InlineData(4, "Low")]
    public void SeverityBandsMatchTheWorkItemExample(int occurrences, string severity)
    {
        Assert.Equal(severity, IncidentIngestionPlanner.SeverityForOccurrenceCount(occurrences));
    }

    [Fact]
    public void SameErrorInTwoClubsIsTwoIncidentsAndBrowserNoiseIsDropped()
    {
        var plan = IncidentIngestionPlanner.Plan("cars", "production",
        [
            Signal("ErrorEvent:script", "T-Mobile", "/cars/search.json", "item-noise"),
            Signal("System.TimeoutException", "T-Mobile", "/cars/search.json", "item-1", "8812345", "corr-a"),
            Signal("System.TimeoutException", "T-Mobile", "/cars/search.json", "item-1", "8812345", "corr-a"),
            Signal("System.TimeoutException", "T-Mobile", "/cars/search.json", "item-2", "7743991", "corr-b", Noon.AddSeconds(9)),
            Signal("System.TimeoutException", "Synchrony", "/cars/search.json", "item-3", "100", "corr-c"),
        ]);

        Assert.Equal(1, plan.DroppedNoise);
        Assert.Equal(1, plan.DroppedDuplicates);
        Assert.Equal(2, plan.Incidents.Count);

        var tmobile = Assert.Single(plan.Incidents, incident => incident.ClubId == "T-Mobile");
        Assert.Equal("Open", tmobile.InitialStatus);
        Assert.Equal("02 Car Search", tmobile.Operation);
        Assert.Equal("System.TimeoutException", tmobile.ExceptionType);
        Assert.Equal(2, tmobile.Occurrences.Count);
        Assert.Equal(["corr-a", "corr-b"], tmobile.JourneyKeys);
        Assert.Contains("T-Mobile", tmobile.Fingerprint);
        Assert.Contains("production", tmobile.Fingerprint);

        var synchrony = Assert.Single(plan.Incidents, incident => incident.ClubId == "Synchrony");
        Assert.NotEqual(tmobile.Fingerprint, synchrony.Fingerprint);
        Assert.Equal("Open", synchrony.InitialStatus);
    }

    [Fact]
    public void HotelPurchaseUrlBecomesTheExampleOperation()
    {
        var plan = IncidentIngestionPlanner.Plan("hotels", "production",
        [
            Signal("System.NullReferenceException", "T-Mobile", "/reservations/Purchase", "item-h", "9", "corr-h"),
        ]);

        var incident = Assert.Single(plan.Incidents);
        Assert.Equal("05 Purchase", incident.Operation);
        Assert.Equal("06 Checkout", incident.Component);
        Assert.Equal("corr-h", Assert.Single(incident.JourneyKeys));
    }

    [Fact]
    public void HotelRelatedJourneyIsTheCorrelationId()
    {
        var plan = IncidentIngestionPlanner.Plan("hotels", "production",
        [
            Signal(
                "System.NullReferenceException",
                "15827",
                "/account/usercreditcards_read.json",
                "item-hotel",
                "20949333",
                "a08f8dd1-3a01-4831-a512-81e7c27fc991"),
        ]);

        var incident = Assert.Single(plan.Incidents);
        Assert.Equal("a08f8dd1-3a01-4831-a512-81e7c27fc991", Assert.Single(incident.JourneyKeys));
        Assert.Equal("20949333", Assert.Single(incident.Occurrences).MemberId);
    }

    [Fact]
    public void HotelWithoutACorrelationLinksTheMember()
    {
        var plan = IncidentIngestionPlanner.Plan("hotels", "production",
        [
            Signal("System.NullReferenceException", "15827", "/account/usercreditcards_read.json", "item-hotel-member", "20949333"),
        ]);

        Assert.Equal("20949333", Assert.Single(Assert.Single(plan.Incidents).JourneyKeys));
    }

    [Fact]
    public void HotelWithoutAMemberStillLinksTheCorrelationId()
    {
        var plan = IncidentIngestionPlanner.Plan("hotels", "production",
        [
            Signal("System.Web.HttpException", "15811", "/vacationclub/logincheck.aspx", "item-login", "0", "8486b40a-901f-4d5a-84c7-29213136ed6a"),
        ]);

        Assert.Equal(
            "8486b40a-901f-4d5a-84c7-29213136ed6a",
            Assert.Single(Assert.Single(plan.Incidents).JourneyKeys));
    }

    [Theory]
    [InlineData("/cars/search.json", "GET", true)]
    [InlineData("/api/CarBooking", "POST", true)]
    [InlineData("/api/BackgroundCarSearch", "POST", true)]
    [InlineData("/vacationclub/logincheck.aspx", "GET", false)]
    [InlineData("/reservations/Purchase", "POST", false)]
    public void SharedHotelAppRowsSplitOnCarJourneyUrls(string url, string operation, bool isCar)
    {
        Assert.Equal(isCar, IncidentIngestionPlanner.IsCarException(url, operation));
    }

    [Fact]
    public void CruiseRelatedJourneyIsTheFrontendSession()
    {
        var plan = IncidentIngestionPlanner.Plan("cruises", "production",
        [
            new ExceptionSignal(
                "323",
                "TypeError",
                "cruise-web",
                "02 Cruise Listing",
                "undefined is not an object (evaluating 'this.cruiseLines.filter')",
                null,
                "item-cruise",
                "op-1",
                Noon,
                "WES47642EFV",
                "2ff71128-d7b3-478c-9818-e4ce8f615b01",
                "de9c3391-cd1c-453f-b31d-25f331c9c74a",
                null),
        ]);

        var incident = Assert.Single(plan.Incidents);
        Assert.Equal("2ff71128-d7b3-478c-9818-e4ce8f615b01", Assert.Single(incident.JourneyKeys));
        Assert.Equal(
            "2ff71128-d7b3-478c-9818-e4ce8f615b01",
            Assert.Single(incident.Occurrences).SessionId);
    }

    [Fact]
    public void CruiseWithoutASessionStillLinksTheCorrelationId()
    {
        var plan = IncidentIngestionPlanner.Plan("cruises", "production",
        [
            Signal("TypeError", "323", "/search", "item-cruise-corr", "WES1", "de9c3391-cd1c-453f-b31d-25f331c9c74a"),
        ]);

        Assert.Equal(
            "de9c3391-cd1c-453f-b31d-25f331c9c74a",
            Assert.Single(Assert.Single(plan.Incidents).JourneyKeys));
    }

    [Fact]
    public void MissingClubIsNotAnIncident()
    {
        var plan = IncidentIngestionPlanner.Plan("cars", "production",
        [
            Signal("System.TimeoutException", "  ", "/cars/search.json", "item-x"),
        ]);

        Assert.Empty(plan.Incidents);
        Assert.Equal(1, plan.DroppedWithoutClub);
    }

    private static ExceptionSignal Signal(
        string type,
        string club,
        string url,
        string itemId,
        string? memberId = null,
        string? correlationId = null,
        DateTimeOffset? at = null) => new(
        club,
        type,
        "inventory-cars",
        "GET",
        "The operation has timed out after 30000 ms",
        null,
        itemId,
        "a1b2c3d4",
        at ?? Noon,
        memberId,
        null,
        correlationId,
        url);
}
