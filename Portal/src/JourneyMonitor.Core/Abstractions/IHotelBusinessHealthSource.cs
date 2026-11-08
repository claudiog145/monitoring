using JourneyMonitor.Core.Domain;

namespace JourneyMonitor.Core.Abstractions;

/// <summary>
/// Hotels Business Health for the Reliability Watch card, read from
/// <c>dbo.vw_Alert_HotelBookingKPIs</c>. A null club id is the view's TOTAL row.
/// Returns null when the warehouse read is not configured or the hour row is absent.
/// </summary>
public interface IHotelBusinessHealthSource
{
    Task<HotelBusinessHealth?> ReadAsync(string environment, long? vacationClubId, CancellationToken cancellationToken);
}
