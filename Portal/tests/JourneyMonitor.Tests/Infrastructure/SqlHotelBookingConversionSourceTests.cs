using JourneyMonitor.Core.Analytics;
using JourneyMonitor.Infrastructure.Sql;

namespace JourneyMonitor.Tests.Infrastructure;

public class SqlHotelBookingConversionSourceTests
{
    [Fact]
    public void Count_and_recent_queries_use_the_booking_stage_and_the_club_filter()
    {
        Assert.Equal(7, HotelBookingConversion.BookingStageNo);
        Assert.Contains("s.StageNo = 7", SqlHotelBookingConversionSource.StartedPredicate);
        Assert.Contains("j.IsBookSuccessful = 1", SqlHotelBookingConversionSource.StartedPredicate);
        Assert.Contains("j.BookFail > 0", SqlHotelBookingConversionSource.StartedPredicate);

        var count = Collapse(SqlHotelBookingConversionSource.CountSql);
        Assert.Contains("COUNT(*) AS Started", count);
        Assert.Contains("j.IsBookSuccessful = 1 THEN 1 ELSE 0 END), 0) AS Completed", count);
        Assert.Contains("q.Environment = @Environment", count);
        Assert.Contains("(@ClubId IS NULL OR j.ClubId = @ClubId)", count);
        Assert.Contains("j.LastSeen >= @Since", count);
        Assert.Contains("j.LastSeen < @Until", count);
        Assert.Contains("s.StageNo = 7", count);

        var recent = Collapse(SqlHotelBookingConversionSource.RecentSql);
        Assert.Contains("SELECT TOP (10)", recent);
        Assert.Contains("j.IsBookSuccessful AS Completed", recent);
        Assert.Contains("AND j.IsBookSuccessful = 1", recent);
        Assert.Contains("(@ClubId IS NULL OR j.ClubId = @ClubId)", recent);
        Assert.Contains("ORDER BY j.LastSeen DESC, j.JourneyId DESC", recent);
        Assert.DoesNotContain("s.StageNo = 7", recent);
    }

    private static string Collapse(string sql) =>
        string.Join(' ', sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
