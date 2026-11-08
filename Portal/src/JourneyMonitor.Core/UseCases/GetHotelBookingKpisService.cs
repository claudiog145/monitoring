using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;

namespace JourneyMonitor.Core.UseCases;

/// <summary>
/// Reads the hotel booking KPI view. The timer calls this; the SQL and the App Configuration
/// read live in <see cref="IHotelBookingKpiSource"/>.
/// </summary>
public sealed class GetHotelBookingKpisService(IHotelBookingKpiSource source)
{
    public Task<IReadOnlyList<HotelBookingKpiResponse>> ReadAlertStatusAsync(CancellationToken cancellationToken) =>
        source.ReadAlertStatusAsync(cancellationToken);
}
