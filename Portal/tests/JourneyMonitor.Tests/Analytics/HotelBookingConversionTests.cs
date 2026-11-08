using JourneyMonitor.Core.Analytics;
using JourneyMonitor.Core.Domain;

namespace JourneyMonitor.Tests.Analytics;

public class HotelBookingConversionTests
{
    [Theory]
    [InlineData(0, 0, null)]
    [InlineData(0, 4, null)]
    [InlineData(4, 1, "25.0")]
    [InlineData(3, 1, "33.3")]
    [InlineData(3, 2, "66.7")]
    [InlineData(8, 1, "12.5")]
    [InlineData(2, 5, "100.0")]
    public void Percent_is_completed_over_started(int started, int completed, string? expected)
    {
        var percent = HotelBookingConversion.Percent(started, completed);

        Assert.Equal(expected is null ? null : decimal.Parse(expected), percent);
    }

    [Fact]
    public void OldestFirst_keeps_the_newest_ten_and_draws_them_left_to_right()
    {
        var newestFirst = Enumerable.Range(1, 12)
            .Select(n => new HotelBookingConversionOperation(
                new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero).AddMinutes(-n),
                n % 2 == 0,
                "15639"))
            .ToArray();

        var ordered = HotelBookingConversion.OldestFirst(newestFirst);

        Assert.Equal(10, ordered.Count);
        Assert.Equal(newestFirst[9].At, ordered[0].At);
        Assert.Equal(newestFirst[0].At, ordered[9].At);
    }

    [Fact]
    public void Pacific_hour_keeps_the_same_wall_clock_across_the_daylight_saving_change()
    {
        var current = HotelBookingHours.Containing(new DateTimeOffset(2026, 11, 3, 17, 30, 0, TimeSpan.Zero));
        var prior = HotelBookingHours.SameHourPriorWeek(current);

        Assert.Equal(new DateTimeOffset(2026, 11, 3, 17, 0, 0, TimeSpan.Zero), current.Start);
        Assert.Equal(current.Start.AddHours(1), current.End);
        Assert.Equal(new DateTimeOffset(2026, 10, 27, 16, 0, 0, TimeSpan.Zero), prior.Start);
        Assert.Equal(prior.Start.AddHours(1), prior.End);
        Assert.Equal(9, TimeZoneInfo.ConvertTime(current.Start, HotelBookingHours.Pacific).Hour);
        Assert.Equal(9, TimeZoneInfo.ConvertTime(prior.Start, HotelBookingHours.Pacific).Hour);

        var twoWeeks = HotelBookingHours.SameClockHour(current, 14);
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 16, 0, 0, TimeSpan.Zero), twoWeeks.Start);
        Assert.Equal(9, TimeZoneInfo.ConvertTime(twoWeeks.Start, HotelBookingHours.Pacific).Hour);

        var candidates = HotelBookingHours.HistoricalCandidates(current).ToArray();
        Assert.Equal(4, candidates.Length);
        Assert.Equal(prior.Start, candidates[0].Start);
        Assert.Equal(twoWeeks.Start, candidates[1].Start);
    }

    [Fact]
    public void A_conversion_reading_replaces_only_the_conversion_fields()
    {
        var window = new DateTimeOffset(2026, 11, 3, 17, 0, 0, TimeSpan.Zero);
        var bookings = new HotelBusinessHealth(
            "fabric",
            "hotel",
            "production",
            "Hour",
            15639,
            new HotelBusinessHealthReading(window, null, 7, 2672.80m, 1),
            new HotelBusinessHealthReading(window.AddDays(-7), null, 3, 357.70m, 0));
        var recent = new[]
        {
            new HotelBookingConversionOperation(window, true, "15639"),
            new HotelBookingConversionOperation(window.AddMinutes(5), false, "15639"),
        };
        var conversion = new HotelBookingConversionSnapshot(
            "sql", window, window.AddDays(-7), 25.0m, 10.0m, recent);

        var result = HotelBusinessHealthComposer.Apply(bookings, conversion, "production", 15639);

        Assert.Equal("fabric", result!.Source);
        Assert.True(result.BookingsAvailable);
        Assert.Equal(7, result.Current.Bookings);
        Assert.Equal(2672.80m, result.Current.Revenue);
        Assert.Equal(25.0m, result.Current.ConversionPercent);
        Assert.Equal(10.0m, result.PriorWeek.ConversionPercent);
        Assert.Equal(3, result.PriorWeek.Bookings);
        Assert.Equal(window.AddDays(-7), result.PriorConversionWindowStart);
        Assert.Same(recent, result.RecentOperations);
    }

    [Fact]
    public void Conversion_without_the_booking_view_does_not_invent_bookings()
    {
        var window = new DateTimeOffset(2026, 11, 3, 17, 0, 0, TimeSpan.Zero);
        var conversion = new HotelBookingConversionSnapshot(
            "sql",
            window,
            window.AddDays(-7),
            40m,
            null,
            [new HotelBookingConversionOperation(window, true, null)]);

        var result = HotelBusinessHealthComposer.Apply(null, conversion, "qa", 0);

        Assert.Equal("sql", result!.Source);
        Assert.False(result.BookingsAvailable);
        Assert.Equal(0, result.Current.Bookings);
        Assert.Equal(0m, result.Current.Revenue);
        Assert.Equal(40m, result.Current.ConversionPercent);
        Assert.Null(result.PriorWeek.ConversionPercent);
        Assert.Equal(0, result.VacationClubId);
    }

    [Fact]
    public void No_booking_view_and_no_conversion_stays_empty()
    {
        Assert.Null(HotelBusinessHealthComposer.Apply(null, null, "production", 0));
    }
}
