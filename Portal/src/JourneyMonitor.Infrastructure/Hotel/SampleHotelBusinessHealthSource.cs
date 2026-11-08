using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;

namespace JourneyMonitor.Infrastructure.Hotel;

/// <summary>
/// Canned Hotels Business Health kept for tests. The Functions host reads the Fabric view
/// instead. Current 340 bookings / $72,500 against the same hour a week earlier at 96 / $19,400.
/// </summary>
public sealed class SampleHotelBusinessHealthSource : IHotelBusinessHealthSource
{
    public const string SourceName = "sample";

    public Task<HotelBusinessHealth?> ReadAsync(string environment, long? vacationClubId, CancellationToken cancellationToken)
    {
        var utc = DateTimeOffset.UtcNow;
        var currentHour = new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, TimeSpan.Zero);

        HotelBusinessHealth health = new(
            SourceName,
            "hotel",
            environment,
            "Hour",
            vacationClubId ?? 0,
            Current: new HotelBusinessHealthReading(currentHour, 24.6m, 340, 72500m, 0),
            PriorWeek: new HotelBusinessHealthReading(currentHour.AddDays(-7), 8.2m, 96, 19400m, 0));
        return Task.FromResult<HotelBusinessHealth?>(health);
    }
}
