using JourneyMonitor.Core.Domain;

namespace JourneyMonitor.Core.Analytics;

/// <summary>
/// Hotel conversion is bookings that completed divided by bookings that started.
/// A booking starts at stage 07 Booking (or a CompletePurchase result) and completes
/// when the purchase succeeds.
/// </summary>
public static class HotelBookingConversion
{
    /// <summary>KQL stage "07 Booking": CompletePurchase and the supplier book call.</summary>
    public const int BookingStageNo = 7;

    public const int RecentOperationCount = 10;

    /// <summary>Null when nothing started. One decimal, matching the Reliability Watch card.</summary>
    public static decimal? Percent(int started, int completed)
    {
        if (started <= 0) return null;
        if (completed < 0) completed = 0;
        if (completed > started) completed = started;
        return Math.Round(completed * 100m / started, 1, MidpointRounding.AwayFromZero);
    }

    /// <summary>Keeps the newest <paramref name="take"/> rows, then returns them oldest first.</summary>
    public static IReadOnlyList<HotelBookingConversionOperation> OldestFirst(
        IEnumerable<HotelBookingConversionOperation> newestFirst,
        int take = RecentOperationCount)
    {
        var newest = newestFirst.Take(Math.Max(0, take)).ToArray();
        Array.Reverse(newest);
        return newest;
    }
}

/// <summary>One clock hour. <see cref="End"/> is exclusive.</summary>
public readonly record struct HourWindow(DateTimeOffset Start, DateTimeOffset End);

/// <summary>
/// The Reliability Watch card compares the current Pacific hour with the same clock hour
/// seven days earlier. The earlier hour keeps its own Pacific offset, so a daylight-saving
/// change does not shift the wall clock.
/// </summary>
public static class HotelBookingHours
{
    public static TimeZoneInfo Pacific { get; } = ResolvePacific();

    public static HourWindow Containing(DateTimeOffset instant)
    {
        var local = TimeZoneInfo.ConvertTime(instant, Pacific);
        var start = new DateTimeOffset(local.Year, local.Month, local.Day, local.Hour, 0, 0, local.Offset);
        return new HourWindow(start, start.AddHours(1));
    }

    /// <summary>How many same-weekday hours to try when the hour seven days ago has no booking starts.</summary>
    public const int HistoricalLookbackWeeks = 4;

    public static HourWindow SameHourPriorWeek(HourWindow current) => SameClockHour(current, 7);

    /// <summary>
    /// The same Pacific clock hour <paramref name="daysBack"/> days before <paramref name="current"/>.
    /// The earlier day keeps its own Pacific offset.
    /// </summary>
    public static HourWindow SameClockHour(HourWindow current, int daysBack)
    {
        var local = TimeZoneInfo.ConvertTime(current.Start, Pacific);
        var prior = DateTime.SpecifyKind(local.DateTime.AddDays(-daysBack), DateTimeKind.Unspecified);
        var wall = new DateTime(prior.Year, prior.Month, prior.Day, prior.Hour, 0, 0, DateTimeKind.Unspecified);
        var start = new DateTimeOffset(wall, Pacific.GetUtcOffset(wall));
        return new HourWindow(start, start.AddHours(1));
    }

    /// <summary>
    /// Same weekday and clock hour, nearest first: 7 days ago, then 14, 21, and 28.
    /// A day with no ingested journeys is skipped so Historical still has a rate.
    /// </summary>
    public static IEnumerable<HourWindow> HistoricalCandidates(HourWindow current)
    {
        for (var weeks = 1; weeks <= HistoricalLookbackWeeks; weeks++)
            yield return SameClockHour(current, weeks * 7);
    }

    private static TimeZoneInfo ResolvePacific()
    {
        foreach (var id in new[] { "America/Los_Angeles", "Pacific Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        throw new InvalidOperationException("Pacific time zone is not available on this machine.");
    }
}

/// <summary>Folds a journey-store conversion onto the booking-view health row.</summary>
public static class HotelBusinessHealthComposer
{
    public static HotelBusinessHealth? Apply(
        HotelBusinessHealth? bookings,
        HotelBookingConversionSnapshot? conversion,
        string environment,
        long vacationClubId)
    {
        if (bookings is null && conversion is null) return null;
        if (conversion is null) return bookings;
        if (bookings is null)
        {
            return new HotelBusinessHealth(
                conversion.Source,
                "hotel",
                environment,
                "Hour",
                vacationClubId,
                new HotelBusinessHealthReading(
                    conversion.CurrentWindowStart,
                    conversion.CurrentPercent,
                    0,
                    0,
                    0),
                new HotelBusinessHealthReading(
                    conversion.PriorWindowStart,
                    conversion.PriorWeekPercent,
                    0,
                    0,
                    0),
                BookingsAvailable: false,
                RecentOperations: conversion.Recent,
                PriorConversionWindowStart: conversion.PriorWindowStart,
                CurrentConversionWindowStart: conversion.CurrentWindowStart);
        }

        return bookings with
        {
            Current = bookings.Current with { ConversionPercent = conversion.CurrentPercent },
            PriorWeek = bookings.PriorWeek with { ConversionPercent = conversion.PriorWeekPercent },
            RecentOperations = conversion.Recent,
            PriorConversionWindowStart = conversion.PriorWindowStart,
            CurrentConversionWindowStart = conversion.CurrentWindowStart,
        };
    }
}
