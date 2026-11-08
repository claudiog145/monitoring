namespace JourneyMonitor.Core.Domain;

/// <summary>
/// Hotels Business Health for the Reliability Watch card: the current hour and the same
/// hour seven days earlier. Bookings and revenue come from the hotel booking view.
/// Conversion is completed bookings divided by bookings that reached stage 07 Booking,
/// read from <c>hotel.Journey</c>. <c>VacationClubId</c> 0 is every club.
/// <c>BookingsAvailable</c> is false when the booking view could not be read, so a
/// conversion-only result is not shown as zero bookings.
/// <c>CurrentConversionWindowStart</c> is the hour the current conversion was counted in; it can
/// differ from <c>Current.WindowStart</c> when the booking view lags or an hour boundary passes between reads.
/// <c>PriorConversionWindowStart</c> is the hour the historical conversion was counted in.
/// That is the same clock hour seven days ago when that hour has booking starts, otherwise
/// the nearest earlier same weekday and hour that does (14, 21, or 28 days).
/// </summary>
public sealed record HotelBusinessHealth(
    string Source,
    string Domain,
    string Environment,
    string Grain,
    long VacationClubId,
    HotelBusinessHealthReading Current,
    HotelBusinessHealthReading PriorWeek,
    bool Available = true,
    bool BookingsAvailable = true,
    IReadOnlyList<HotelBookingConversionOperation>? RecentOperations = null,
    DateTimeOffset? PriorConversionWindowStart = null,
    DateTimeOffset? CurrentConversionWindowStart = null);

/// <summary>One hour of Hotels business health. Bookings and revenue are net of cancellations.</summary>
public sealed record HotelBusinessHealthReading(
    DateTimeOffset WindowStart,
    decimal? ConversionPercent,
    int Bookings,
    decimal Revenue,
    int Cancellations);

/// <summary>
/// One hotel journey that reached booking. <c>Completed</c> is a successful purchase.
/// The list on <see cref="HotelBusinessHealth"/> is the newest matches, oldest first.
/// </summary>
public sealed record HotelBookingConversionOperation(
    DateTimeOffset At,
    bool Completed,
    string? ClubId);

/// <summary>
/// Start-to-complete conversion for the current Pacific hour, the same hour seven days
/// earlier, and the newest completed purchases.
/// </summary>
public sealed record HotelBookingConversionSnapshot(
    string Source,
    DateTimeOffset CurrentWindowStart,
    DateTimeOffset PriorWindowStart,
    decimal? CurrentPercent,
    decimal? PriorWeekPercent,
    IReadOnlyList<HotelBookingConversionOperation> Recent);
