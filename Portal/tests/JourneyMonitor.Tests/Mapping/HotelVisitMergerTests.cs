using System.Text.Json;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Mapping;

namespace JourneyMonitor.Tests.Mapping;

public class HotelVisitMergerTests
{
    private const string Member = "21061035";

    [Fact]
    public void LoginThenSearchWithinTwoMinutesIsOneVisitKeptUnderTheFurthestSession()
    {
        var login = Row(Member, "login-1", "2026-11-03T14:30:10Z", "2026-11-03T14:30:10Z", 1, "Dropped at step 1", Stage(1, "01 Login", "2026-11-03T14:30:10Z", 40), journeyTotalMs: 40);
        var search = Row(Member, "search-1", "2026-11-03T14:30:11Z", "2026-11-03T14:31:00Z", 3, "Dropped at step 3",
            Stage(2, "02 Hotel Search", "2026-11-03T14:30:11Z", 80) + "," + Stage(3, "03 Hotel Results", "2026-11-03T14:31:00Z", 120),
            passedSearch: true, journeyTotalMs: 200, callsNoCorrelation: 4);

        var merged = HotelVisitMerger.Merge([login, search]);

        var visit = Assert.Single(merged);
        Assert.Equal("search-1", visit.CorrelationId());
        Assert.Equal("Dropped at step 3", visit.GetString("Outcome"));
        Assert.Equal(3, visit.GetInt("LastStep"));
        Assert.True(visit.GetBool("PassedSearch"));
        Assert.False(visit.GetBool("IsBookSuccessful"));
        Assert.Equal(["01 Login", "02 Hotel Search", "03 Hotel Results"], StageNames(visit));
        Assert.Equal(["login-1"], Absorbed(visit));
        Assert.Equal(240, visit.GetDouble("JourneyTotalMs"));
        Assert.Equal(4, visit.GetInt("CallsNoCorrelation"));
        Assert.Equal(DateTimeOffset.Parse("2026-11-03T14:30:10Z"), visit.GetDateTimeOffset("FirstSeen"));
        Assert.Equal(DateTimeOffset.Parse("2026-11-03T14:31:00Z"), visit.GetDateTimeOffset("LastSeen"));
    }

    [Fact]
    public void AGapOverTwoMinutesStaysTwoJourneys()
    {
        var login = Row(Member, "login-1", "2026-11-03T14:30:10Z", "2026-11-03T14:30:10Z", 1, "Dropped at step 1", Stage(1, "01 Login", "2026-11-03T14:30:10Z", 10));
        var search = Row(Member, "search-1", "2026-11-03T14:32:11Z", "2026-11-03T14:32:20Z", 2, "Dropped at step 2", Stage(2, "02 Hotel Search", "2026-11-03T14:32:11Z", 10));

        var merged = HotelVisitMerger.Merge([login, search]);

        Assert.Equal(["login-1", "search-1"], merged.Select(row => row.CorrelationId()).Order());
        Assert.All(merged, row => Assert.False(row.Get("AbsorbedCorrelationIds").HasValue));
    }

    [Fact]
    public void ExactlyTwoMinutesStillFolds()
    {
        var login = Row(Member, "login-1", "2026-11-03T14:30:10Z", "2026-11-03T14:30:10Z", 1, "Dropped at step 1", Stage(1, "01 Login", "2026-11-03T14:30:10Z", 10));
        var search = Row(Member, "search-1", "2026-11-03T14:32:10Z", "2026-11-03T14:32:10Z", 2, "Dropped at step 2", Stage(2, "02 Hotel Search", "2026-11-03T14:32:10Z", 10));

        var merged = HotelVisitMerger.Merge([login, search]);

        Assert.Equal("search-1", Assert.Single(merged).CorrelationId());
    }

    [Fact]
    public void ABookedSessionIsNotFoldedIntoTheNextOne()
    {
        var booked = Row(Member, "booked-1", "2026-11-03T14:30:10Z", "2026-11-03T14:30:40Z", 7, "Booked, no confirmation page",
            Stage(2, "02 Hotel Search", "2026-11-03T14:30:10Z", 10) + "," + Stage(7, "07 Booking", "2026-11-03T14:30:40Z", 10),
            bookOk: 1, booked: true);
        var next = Row(Member, "confirm-1", "2026-11-03T14:31:00Z", "2026-11-03T14:31:05Z", 8, "Dropped at step 8", Stage(8, "08 Confirmation", "2026-11-03T14:31:00Z", 10));

        var merged = HotelVisitMerger.Merge([booked, next]);

        Assert.Equal(2, merged.Count);
    }

    [Fact]
    public void TheSameStageDoesNotContinueTheFunnel()
    {
        var first = Row(Member, "search-a", "2026-11-03T14:30:10Z", "2026-11-03T14:30:20Z", 2, "Dropped at step 2", Stage(2, "02 Hotel Search", "2026-11-03T14:30:10Z", 10));
        var second = Row(Member, "search-b", "2026-11-03T14:30:30Z", "2026-11-03T14:30:40Z", 3, "Dropped at step 3", Stage(2, "02 Hotel Search", "2026-11-03T14:30:30Z", 10) + "," + Stage(3, "03 Hotel Results", "2026-11-03T14:30:40Z", 10));

        var merged = HotelVisitMerger.Merge([first, second]);

        Assert.Equal(2, merged.Count);
    }

    [Fact]
    public void ALaterLoginCallOnTheSearchSessionDoesNotKeepTheLoginApart()
    {
        var login = Row(Member, "login-1", "2026-11-03T14:30:10Z", "2026-11-03T14:30:10Z", 1, "Dropped at step 1", Stage(1, "01 Login", "2026-11-03T14:30:10Z", 10));
        var search = Row(Member, "search-1", "2026-11-03T14:30:11Z", "2026-11-03T14:31:20Z", 3, "Dropped at step 3",
            Stage(2, "02 Hotel Search", "2026-11-03T14:30:11Z", 20) + ","
            + Stage(3, "03 Hotel Results", "2026-11-03T14:30:13Z", 30) + ","
            + Stage(1, "01 Login", "2026-11-03T14:31:20Z", 10));

        var visit = Assert.Single(HotelVisitMerger.Merge([login, search]));

        Assert.Equal("search-1", visit.CorrelationId());
        Assert.Equal(["login-1"], Absorbed(visit));
        Assert.Equal("Dropped at step 3", visit.GetString("Outcome"));
        Assert.Equal(["01 Login", "02 Hotel Search", "03 Hotel Results"], StageNames(visit));
    }

    [Fact]
    public void DifferentMembersStayApart()
    {
        var first = Row("111", "login-1", "2026-11-03T14:30:10Z", "2026-11-03T14:30:10Z", 1, "Dropped at step 1", Stage(1, "01 Login", "2026-11-03T14:30:10Z", 10));
        var second = Row("222", "search-1", "2026-11-03T14:30:11Z", "2026-11-03T14:30:11Z", 2, "Dropped at step 2", Stage(2, "02 Hotel Search", "2026-11-03T14:30:11Z", 10));

        var merged = HotelVisitMerger.Merge([first, second]);

        Assert.Equal(2, merged.Count);
    }

    [Fact]
    public void ThreeContinuingFragmentsBecomeTheFurthestSession()
    {
        var login = Row(Member, "login-1", "2026-11-03T14:30:10Z", "2026-11-03T14:30:10Z", 1, "Dropped at step 1", Stage(1, "01 Login", "2026-11-03T14:30:10Z", 10));
        var search = Row(Member, "search-1", "2026-11-03T14:30:40Z", "2026-11-03T14:30:40Z", 2, "Dropped at step 2", Stage(2, "02 Hotel Search", "2026-11-03T14:30:40Z", 10));
        var details = Row(Member, "details-1", "2026-11-03T14:31:20Z", "2026-11-03T14:31:50Z", 4, "Dropped at step 4", Stage(4, "04 Hotel Details", "2026-11-03T14:31:20Z", 30),
            bookFail: 0);

        var merged = HotelVisitMerger.Merge([details, login, search]);

        var visit = Assert.Single(merged);
        Assert.Equal("details-1", visit.CorrelationId());
        Assert.Equal(["login-1", "search-1"], Absorbed(visit));
        Assert.Equal(["01 Login", "02 Hotel Search", "04 Hotel Details"], StageNames(visit));
        Assert.Equal("Dropped at step 4", visit.GetString("Outcome"));
    }

    [Fact]
    public void ALaterFragmentBeyondTheGapStartsANewVisit()
    {
        var login = Row(Member, "login-1", "2026-11-03T14:30:10Z", "2026-11-03T14:30:10Z", 1, "Dropped at step 1", Stage(1, "01 Login", "2026-11-03T14:30:10Z", 10));
        var search = Row(Member, "search-1", "2026-11-03T14:30:40Z", "2026-11-03T14:30:40Z", 2, "Dropped at step 2", Stage(2, "02 Hotel Search", "2026-11-03T14:30:40Z", 10));
        var details = Row(Member, "details-1", "2026-11-03T14:33:00Z", "2026-11-03T14:33:10Z", 4, "Dropped at step 4", Stage(4, "04 Hotel Details", "2026-11-03T14:33:00Z", 10));

        var merged = HotelVisitMerger.Merge([login, search, details]);

        Assert.Equal(2, merged.Count);
        Assert.Contains(merged, row => row.CorrelationId() == "search-1" && Absorbed(row).SequenceEqual(["login-1"]));
        Assert.Contains(merged, row => row.CorrelationId() == "details-1");
    }

    [Fact]
    public void AFollowingBookingReportsBookedAndKeepsTheReservation()
    {
        var login = Row(Member, "login-1", "2026-11-03T14:30:10Z", "2026-11-03T14:30:10Z", 1, "Dropped at step 1", Stage(1, "01 Login", "2026-11-03T14:30:10Z", 10));
        var booked = Row(Member, "book-1", "2026-11-03T14:30:20Z", "2026-11-03T14:32:00Z", 8, "Booked",
            Stage(2, "02 Hotel Search", "2026-11-03T14:30:20Z", 20) + "," + Stage(7, "07 Booking", "2026-11-03T14:31:00Z", 30) + "," + Stage(8, "08 Confirmation", "2026-11-03T14:32:00Z", 15),
            bookOk: 1, bookFail: 1, booked: true, passedSearch: true, reservations: """["RES-9"]""");

        var visit = Assert.Single(HotelVisitMerger.Merge([login, booked]));

        Assert.Equal("book-1", visit.CorrelationId());
        Assert.Equal("Booked after 1 failed attempt", visit.GetString("Outcome"));
        Assert.True(visit.GetBool("IsBookSuccessful"));
        Assert.Equal(1, visit.GetInt("BookOk"));
        Assert.Equal(1, visit.GetInt("BookFail"));
        Assert.Equal("RES-9", visit.Get("BookedReservations")!.Value.EnumerateArray().Single().GetString());
        Assert.Equal(100, visit.GetInt("SearchToConfirm_s"));
    }

    [Fact]
    public void AnUnchangedRowIsLeftAsItArrived()
    {
        var only = Row(Member, "only-1", "2026-11-03T14:30:10Z", "2026-11-03T14:30:10Z", 2, "Dropped at step 2", Stage(2, "02 Hotel Search", "2026-11-03T14:30:10Z", 10));

        var merged = HotelVisitMerger.Merge([only]);

        Assert.Same(only, Assert.Single(merged));
    }

    private static List<string> StageNames(TelemetryJourneyRow row) =>
        row.GetJourneyStages().Select(stage => stage.Str("stage")).ToList();

    private static List<string> Absorbed(TelemetryJourneyRow row)
    {
        var value = row.Get(HotelVisitMerger.AbsorbedIdsProperty);
        if (value is null) return [];
        return value.Value.EnumerateArray().Select(item => item.GetString()!).ToList();
    }

    private static string Stage(int number, string name, string at, int totalMs) =>
        $$"""{"stageNo":{{number}},"stage":"{{name}}","t":"{{at}}","callCount":1,"stageTotalMs":{{totalMs}},"status":"GREEN","calls":[{"kind":"api","call":"{{name}}","t":"{{at}}","n":1}]}""";

    private static TelemetryJourneyRow Row(
        string userId,
        string correlationId,
        string firstSeen,
        string lastSeen,
        int lastStep,
        string outcome,
        string stages,
        int bookOk = 0,
        int bookFail = 0,
        bool booked = false,
        bool passedSearch = false,
        double journeyTotalMs = 0,
        int callsNoCorrelation = 0,
        string? reservations = null)
    {
        var reserved = reservations ?? "[]";
        return Parse($$"""
            {
              "UserId": "{{userId}}",
              "CorrelationId": "{{correlationId}}",
              "Outcome": "{{outcome}}",
              "LastStep": {{lastStep}},
              "FirstSeen": "{{firstSeen}}",
              "LastSeen": "{{lastSeen}}",
              "BookOk": {{bookOk}},
              "BookFail": {{bookFail}},
              "IsBookSuccessful": {{(booked ? "true" : "false")}},
              "PassedSearch": {{(passedSearch ? "true" : "false")}},
              "FailedCalls": 0,
              "Exceptions": 0,
              "JourneyTotalMs": {{journeyTotalMs}},
              "CallsNoCorrelation": {{callsNoCorrelation}},
              "CallsViaCorrelation": 0,
              "SessionCount": 1,
              "BookedReservations": {{reserved}},
              "Journey": [{{stages}}]
            }
            """);
    }

    private static TelemetryJourneyRow Parse(string json)
    {
        var row = new TelemetryJourneyRow();
        using var document = JsonDocument.Parse(json);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            row.Extra[property.Name] = property.Value.Clone();
        }

        return row;
    }
}

internal static class HotelVisitRowAssertions
{
    public static string CorrelationId(this TelemetryJourneyRow row) => row.GetString("CorrelationId");
}
