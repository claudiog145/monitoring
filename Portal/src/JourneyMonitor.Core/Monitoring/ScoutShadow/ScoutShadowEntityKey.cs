using System.Text.Json;
using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Domain;

namespace JourneyMonitor.Core.Monitoring.ScoutShadow;

/// <summary>
/// The one entity vocabulary both sides of the shadow comparison are reduced to (contract v4 §4, §6 A1).
///
/// The rules side is taken from the board's OWN identity column - GroupPayload <c>Entity</c>, else the raw
/// <c>Entity</c> / <c>ClubOrHost</c> column - because that is the KQL's identity (profile identity sources in
/// order, then the request host), the same coalesce the entity KPI template renders. <see cref="ScoutBoardRow.EntityKey"/>
/// is NOT used first: it is the C# reclassification, which deliberately does not feed a "club:140" back as a
/// dimension and therefore reads such rows as "[host/club unavailable]".
///
/// The two sides still differ in format, so both are normalised:
/// - company board: the bare value (<c>"140"</c>, <c>"shop.example.com"</c>) or <c>"(entity unavailable)"</c>;
/// - legacy incumbent board: <c>"club:140"</c> / <c>"host:Shop.Example.com"</c> (host not lower-cased) or
///   <c>"[host/club unavailable]"</c>;
/// - entity KPI: the bare value, host lower-cased, or <c>"(entity unavailable)"</c>.
/// The legacy <c>club:</c>/<c>host:</c> prefix is dropped and values are trimmed and lower-cased. A board row
/// without an identity falls back to its host, like the KQL; with no host either it has NO key and the caller
/// skips it, so distinct unidentified entities are never merged into one sentinel row.
/// </summary>
public static class ScoutShadowEntityKey
{
    /// <summary>The entity KQL's own "could not identify" value.</summary>
    public const string KpiUnknownEntity = "(entity unavailable)";

    private static readonly string[] BoardIdentityColumns = ["Entity", "ClubOrHost"];
    private static readonly string[] HostColumns = ["Host"];
    private static readonly string[] LegacyPrefixes = [ClubEntityKind.Club + ":", ClubEntityKind.Host + ":"];

    /// <summary>Normalised entity of a board row; null when the row identifies no entity (the row is then skipped).</summary>
    public static string? FromBoardRow(ScoutBoardRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        var boardIdentity = ReadColumn(row.GroupPayloadJson, BoardIdentityColumns) ?? ReadColumn(row.RawRowJson, BoardIdentityColumns);
        if (boardIdentity is not null)
        {
            if (!IsSentinel(boardIdentity)) return Normalize(StripLegacyPrefix(boardIdentity));
            return HostOf(row);
        }

        // A board that projects no identity column: the profile classification is the same identity order.
        var classified = (row.EntityKey ?? "").Trim();
        if (classified.Length > 0 && !IsSentinel(classified))
        {
            var separator = classified.IndexOf(':');
            return Normalize(separator > 0 ? classified[(separator + 1)..] : classified);
        }

        return HostOf(row);
    }

    /// <summary>Normalised entity of an entity-grain KPI series; null for the domain aggregate or the unknown entity.</summary>
    public static string? FromKpi(string? entityKey)
    {
        var key = (entityKey ?? "").Trim();
        if (key.Length == 0 || IsSentinel(key)) return null;
        return Normalize(key);
    }

    private static bool IsSentinel(string key) => key.StartsWith('[') || key.StartsWith('(');

    private static string StripLegacyPrefix(string value)
    {
        var trimmed = value.Trim();
        foreach (var prefix in LegacyPrefixes)
        {
            if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return trimmed[prefix.Length..];
        }
        return trimmed;
    }

    private static string? Normalize(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length == 0 || IsSentinel(trimmed) ? null : trimmed.ToLowerInvariant();
    }

    private static string? HostOf(ScoutBoardRow row)
    {
        var host = ReadColumn(row.RawRowJson, HostColumns) ?? ReadColumn(row.GroupPayloadJson, HostColumns);
        if (host is not null) return Normalize(host);

        var url = ScoutRowMapper.ToRowContext(row).Url;
        if (string.IsNullOrWhiteSpace(url)) return null;
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Host.Length > 0 ? Normalize(uri.Host) : null;
    }

    /// <summary>First non-blank string/number among <paramref name="names"/> (case-insensitive); the payload may be a JSON string.</summary>
    internal static string? ReadColumn(string? json, string[] names)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.String) return ReadColumn(root.GetString(), names);
            if (root.ValueKind != JsonValueKind.Object) return null;

            foreach (var name in names)
            {
                foreach (var property in root.EnumerateObject())
                {
                    if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                    var text = property.Value.ValueKind switch
                    {
                        JsonValueKind.String => property.Value.GetString(),
                        JsonValueKind.Number => property.Value.ToString(),
                        _ => null,
                    };
                    if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
                }
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }
}
