using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;

namespace JourneyMonitor.Core.Filtering;

/// <summary>
/// Shapes <c>brand.Information</c> rows into the Club dropdown options for a domain.
/// Hotels/Cars are keyed by club id (the KQL <c>tmoClub</c>) with the brand Url as host;
/// Air/Cruises are keyed by ICE partner id (their telemetry carries partner 308, not club 15639),
/// so rows sharing a partner id collapse into one option.
/// </summary>
public static class BrandClubOptions
{
    public static bool UsesPartnerId(string domain) =>
        domain.ToLowerInvariant() is "air" or "cruises";

    public static string Label(string brandName, string id) =>
        string.IsNullOrWhiteSpace(brandName) ? id : $"{brandName.Trim()} ({id})";

    public static List<ClubOption> ForDomain(IEnumerable<BrandClub> rows, string domain)
    {
        var d = domain.ToLowerInvariant();
        var options = new List<ClubOption>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            var id = UsesPartnerId(d) ? row.IcePartnerId?.Trim() : row.ClubId?.Trim();
            if (string.IsNullOrEmpty(id) || !ClubFilter.IsValidClubId(id)) continue;
            if (!seen.Add(id)) continue;

            var host = ClubFilter.IsValidHost(row.Url) ? row.Url!.Trim() : "";
            var partner = UsesPartnerId(d)
                ? id
                : ClubFilter.IsValidClubId(row.IcePartnerId?.Trim()) ? row.IcePartnerId!.Trim() : "";
            options.Add(new ClubOption
            {
                ClubId = id,
                Host = host,
                PartnerId = partner,
                Value = id,
                Label = Label(row.BrandName, id),
                Events = 0,
            });
        }

        return options;
    }

    /// <summary>
    /// Staging hotel dropdown. Keeps brands whose Url is a UAT host (<c>saveonuat.com</c>)
    /// and one option per host, so the Synchrony Travel rows that share a host collapse.
    /// </summary>
    public static List<ClubOption> ForStagingHotels(IEnumerable<BrandClub> rows)
    {
        var options = new List<ClubOption>();
        var seenHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            var id = row.ClubId?.Trim();
            if (string.IsNullOrEmpty(id) || !ClubFilter.IsValidClubId(id)) continue;

            var host = ClubFilter.IsValidHost(row.Url) ? row.Url!.Trim() : "";
            if (!IsUatHotelHost(host) || !seenHosts.Add(host)) continue;

            var partner = ClubFilter.IsValidClubId(row.IcePartnerId?.Trim()) ? row.IcePartnerId!.Trim() : "";
            options.Add(new ClubOption
            {
                ClubId = id,
                Host = host,
                PartnerId = partner,
                Value = id,
                Label = Label(row.BrandName, id),
                Events = 0,
            });
        }

        return options;
    }

    public static bool IsUatHotelHost(string? host) =>
        !string.IsNullOrWhiteSpace(host)
        && host.Contains("saveonuat.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>Host for a hotel/car club id from the brand table, or null when unknown/invalid.</summary>
    public static string? HostFor(IEnumerable<BrandClub> rows, string clubId)
    {
        var match = rows.FirstOrDefault(r => string.Equals(r.ClubId?.Trim(), clubId, StringComparison.Ordinal));
        return match is not null && ClubFilter.IsValidHost(match.Url) ? match.Url!.Trim() : null;
    }

    /// <summary>
    /// Host for the id the Club dropdown sends. Hotels/Cars match club id; Air/Cruises match ICE partner id
    /// and use the first valid brand Url for that partner.
    /// </summary>
    public static string? HostForDomain(IEnumerable<BrandClub> rows, string domain, string selectedId)
    {
        if (!UsesPartnerId(domain)) return HostFor(rows, selectedId);

        foreach (var row in rows)
        {
            if (!string.Equals(row.IcePartnerId?.Trim(), selectedId, StringComparison.Ordinal)) continue;
            if (ClubFilter.IsValidHost(row.Url)) return row.Url!.Trim();
        }
        return null;
    }

    /// <summary>
    /// Brand name for an incident club token. A vacation club id wins; an ICE partner id is used
    /// when the token is not a club id. Unknown tokens (such as a non-numeric code) return null.
    /// </summary>
    public static string? DisplayName(IEnumerable<BrandClub> rows, string? storedId)
    {
        if (string.IsNullOrWhiteSpace(storedId)) return null;
        var id = storedId.Trim();

        foreach (var row in rows)
        {
            if (!string.Equals(row.ClubId?.Trim(), id, StringComparison.Ordinal)) continue;
            var name = row.BrandName?.Trim();
            if (!string.IsNullOrEmpty(name)) return name;
        }

        foreach (var row in rows)
        {
            if (!string.Equals(row.IcePartnerId?.Trim(), id, StringComparison.Ordinal)) continue;
            var name = row.BrandName?.Trim();
            if (!string.IsNullOrEmpty(name)) return name;
        }

        return null;
    }

    /// <summary>ICE partner id for a hotel/car club, or null when the brand row has none.</summary>
    public static string? PartnerFor(IEnumerable<BrandClub> rows, string clubId)
    {
        var match = rows.FirstOrDefault(r => string.Equals(r.ClubId?.Trim(), clubId, StringComparison.Ordinal));
        var partner = match?.IcePartnerId?.Trim();
        return partner is not null && ClubFilter.IsValidClubId(partner) ? partner : null;
    }
}
