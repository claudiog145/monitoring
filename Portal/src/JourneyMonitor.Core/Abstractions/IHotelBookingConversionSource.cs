using JourneyMonitor.Core.Domain;

namespace JourneyMonitor.Core.Abstractions;

/// <summary>
/// Start-to-complete hotel conversion from <c>hotel.Journey</c>. A null club id is every club.
/// Returns null when the journey store is not configured or the read fails.
/// </summary>
public interface IHotelBookingConversionSource
{
    Task<HotelBookingConversionSnapshot?> ReadAsync(
        string environment,
        long? vacationClubId,
        CancellationToken cancellationToken);
}
