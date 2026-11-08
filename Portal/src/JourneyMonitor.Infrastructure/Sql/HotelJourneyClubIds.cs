using System.Text.Json;
using JourneyMonitor.Core.Domain;

namespace JourneyMonitor.Infrastructure.Sql;

/// <summary>
/// Copies <c>hotel.Journey.ClubId</c> onto rows whose read procedure did not project it.
/// Production and staging both store the club on the journey row; an older procedure
/// returns the mixed list without that column, and the portal then labels every hotel
/// journey with the default brand.
/// </summary>
public static class HotelJourneyClubIds
{
    public static void Apply(IEnumerable<TelemetryJourneyRow> rows, IEnumerable<HotelJourneyClub> stored)
    {
        var byUser = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in stored)
        {
            if (string.IsNullOrWhiteSpace(entry.UserId) || string.IsNullOrWhiteSpace(entry.ClubId)) continue;
            byUser[entry.UserId] = entry.ClubId.Trim();
        }

        foreach (var row in rows)
        {
            if (!string.IsNullOrEmpty(row.GetString("ClubId"))) continue;
            var userId = row.GetString("UserId");
            if (userId.Length == 0 || !byUser.TryGetValue(userId, out var clubId)) continue;
            row.Extra["ClubId"] = JsonSerializer.SerializeToElement(clubId);
        }
    }
}

public readonly record struct HotelJourneyClub(string UserId, string? ClubId);
