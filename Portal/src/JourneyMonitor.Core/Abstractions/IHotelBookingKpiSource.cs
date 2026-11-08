using JourneyMonitor.Core.Domain;

namespace JourneyMonitor.Core.Abstractions;

/// <summary>
/// Alert-status read of <c>dbo.vw_Alert_HotelBookingKPIs</c>. Returns an empty list when the
/// Fabric App Configuration settings are absent, instead of failing the caller.
/// </summary>
public interface IHotelBookingKpiSource
{
    Task<IReadOnlyList<HotelBookingKpiResponse>> ReadAlertStatusAsync(CancellationToken cancellationToken);
}
