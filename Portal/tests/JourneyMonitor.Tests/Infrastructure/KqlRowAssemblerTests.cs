using JourneyMonitor.Core.Mapping;
using JourneyMonitor.Infrastructure.Sql;

namespace JourneyMonitor.Tests.Infrastructure;

public class KqlRowAssemblerTests
{
    [Fact]
    public void HotelRowMatchesKqlProjectNamesIncludingJourneyArray()
    {
        var stages = """[{"stageNo":1,"stage":"01 Login","status":"GREEN","calls":[]}]""";
        var row = KqlRowAssembler.Assemble("hotels", new Dictionary<string, object?>
        {
            ["UserId"] = "14001001",
            ["CorrelationId"] = "8a1c2d3e-4f56-4789-a012-111111111001",
            ["Outcome"] = "Booked",
            ["LastStep"] = 8,
            ["FailReason"] = "",
            ["SessionCount"] = 1,
            ["CallsViaCorrelation"] = 12,
            ["CallsNoCorrelation"] = 0,
            ["BookOk"] = 1,
            ["BookFail"] = 0,
            ["DistinctOkRes"] = 1,
            ["BookedReservations"] = """["HTL-10001"]""",
            ["FailedCalls"] = 0,
            ["IsBookSuccessful"] = true,
            ["PassedSearch"] = true,
            ["Exceptions"] = 0,
            ["JourneyTotalMs"] = 18420m,
            ["SlowestCallMs"] = 4100m,
            ["OverallP95"] = 4100m,
            ["SearchToConfirmSeconds"] = 312,
            ["FirstSeen"] = DateTimeOffset.Parse("2026-09-17T17:55:01Z"),
            ["LastSeen"] = DateTimeOffset.Parse("2026-09-17T18:00:13Z"),
            ["StepsJson"] = """["01 Login","02 Hotel Search"]""",
            ["JourneyJson"] = stages,
        });

        Assert.Equal("14001001", row.GetString("UserId"));
        Assert.Equal("Booked", row.GetString("Outcome"));
        Assert.Equal(8, row.GetInt("LastStep"));
        Assert.Equal(312, row.GetInt("SearchToConfirm_s"));
        Assert.True(row.GetBool("IsBookSuccessful"));
        var journey = row.GetJourneyStages();
        Assert.Single(journey);
        Assert.Equal("01 Login", journey[0].Str("stage"));
        Assert.Equal("HTL-10001", row.Get("BookedReservations")!.Value[0].GetString());
    }

    [Fact]
    public void AirRowUsesStatusNotOutcome()
    {
        var row = KqlRowAssembler.Assemble("air", new Dictionary<string, object?>
        {
            ["Status"] = "Booked",
            ["BookingId"] = "BKG-A0004",
            ["CorrelationId"] = "corr-air-1",
            ["UserId"] = "air-user",
            ["ClubId"] = "308",
            ["Route"] = "SEA-LAS",
            ["BookOk"] = 1,
            ["BookFail"] = 0,
            ["FailedCalls"] = 0,
            ["IsBookSuccessful"] = true,
            ["PassedSearch"] = true,
            ["Exceptions"] = 0,
            ["JourneyTotalMs"] = 1000m,
            ["OverallP95"] = 200m,
            ["SlowestCallMs"] = 200m,
            ["StepsCompleted"] = 6,
            ["FunnelStep"] = 6,
            ["LastStep"] = 6,
            ["FirstSeen"] = DateTimeOffset.Parse("2026-09-17T17:00:00Z"),
            ["LastSeen"] = DateTimeOffset.Parse("2026-09-17T17:10:00Z"),
            ["SourcesJson"] = """["ui","api"]""",
            ["StepsJson"] = """["Search","Book"]""",
            ["JourneyJson"] = "[]",
        });

        Assert.Equal("Booked", row.GetString("Status"));
        Assert.Equal("BKG-A0004", row.GetString("BookingId"));
        Assert.Equal("308", row.GetString("ClubId"));
        Assert.Equal("", row.GetString("Outcome"));
    }
}
