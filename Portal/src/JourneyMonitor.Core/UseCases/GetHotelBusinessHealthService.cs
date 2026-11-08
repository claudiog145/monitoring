using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Analytics;
using JourneyMonitor.Core.Domain;

namespace JourneyMonitor.Core.UseCases;

/// <summary>
/// Reads Hotels Business Health for the Reliability Watch card. Bookings and revenue
/// come from the production hotel booking view. Conversion comes from hotel journeys:
/// completed purchases divided by bookings that started. A blank club is every club;
/// a numeric club id is that club only.
/// </summary>
public sealed class GetHotelBusinessHealthService(
    IHotelBusinessHealthSource source,
    IHotelBookingConversionSource? conversions = null)
{
    public const string DefaultEnvironment = "production";

    public async Task<HotelBusinessHealth?> GetAsync(
        string? environment,
        string? domain,
        string? clubId,
        CancellationToken cancellationToken)
    {
        if (!IsHotelDomain(domain))
            return null;
        if (!TryParseClubId(clubId, out var vacationClubId))
            return null;

        var normalized = string.IsNullOrWhiteSpace(environment)
            ? DefaultEnvironment
            : environment.Trim();

        // The warehouse view holds production data only; relabeling it for another environment would mislead.
        HotelBusinessHealth? bookings = null;
        if (string.Equals(normalized, DefaultEnvironment, StringComparison.OrdinalIgnoreCase))
            bookings = await source.ReadAsync(normalized, vacationClubId, cancellationToken);

        var conversion = await (conversions ?? MissingHotelBookingConversionSource.Instance)
            .ReadAsync(normalized, vacationClubId, cancellationToken);
        return HotelBusinessHealthComposer.Apply(bookings, conversion, normalized, vacationClubId ?? 0);
    }

    public static bool IsHotelDomain(string? domain) =>
        domain is not null
        && domain.Trim().ToLowerInvariant() is "hotel" or "hotels";

    public static bool TryParseClubId(string? clubId, out long? vacationClubId)
    {
        if (string.IsNullOrWhiteSpace(clubId))
        {
            vacationClubId = null;
            return true;
        }

        if (long.TryParse(clubId.Trim(), out var id) && id > 0)
        {
            vacationClubId = id;
            return true;
        }

        vacationClubId = null;
        return false;
    }

    private sealed class MissingHotelBookingConversionSource : IHotelBookingConversionSource
    {
        public static readonly MissingHotelBookingConversionSource Instance = new();

        public Task<HotelBookingConversionSnapshot?> ReadAsync(
            string environment,
            long? vacationClubId,
            CancellationToken cancellationToken) =>
            Task.FromResult<HotelBookingConversionSnapshot?>(null);
    }
}
