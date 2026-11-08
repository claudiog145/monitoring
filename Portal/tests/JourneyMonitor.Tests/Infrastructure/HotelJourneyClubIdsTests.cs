using System.Text.Json;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Infrastructure.Sql;

namespace JourneyMonitor.Tests.Infrastructure;

public class HotelJourneyClubIdsTests
{
    [Fact]
    public void StampsTheStoredClubOntoAHotelRowThatOmittedIt()
    {
        var tmobile = Row("22331600");
        var wesalute = Row("21720432");
        var labeled = Row("7258446", "15668");

        HotelJourneyClubIds.Apply(
            [tmobile, wesalute, labeled],
            [
                new HotelJourneyClub("22331600", "15639"),
                new HotelJourneyClub("21720432", "15827"),
                new HotelJourneyClub("7258446", "99999"),
            ]);

        Assert.Equal("15639", tmobile.GetString("ClubId"));
        Assert.Equal("15827", wesalute.GetString("ClubId"));
        Assert.Equal("15668", labeled.GetString("ClubId"));
    }

    [Fact]
    public void IgnoresABlankStoredClub()
    {
        var row = Row("22331600");

        HotelJourneyClubIds.Apply([row], [new HotelJourneyClub("22331600", "  ")]);

        Assert.Equal("", row.GetString("ClubId"));
    }

    private static TelemetryJourneyRow Row(string userId, string? clubId = null)
    {
        var row = new TelemetryJourneyRow();
        row.Extra["UserId"] = JsonSerializer.SerializeToElement(userId);
        if (clubId is not null) row.Extra["ClubId"] = JsonSerializer.SerializeToElement(clubId);
        return row;
    }
}
