using System.Text.Json;
using System.Text.Json.Serialization;

namespace JourneyMonitor.Core.Domain;

/// <summary>
/// A single flattened row as returned by Application Insights for the domain KQL
/// (Hotel/Cars/Cruise v6 or Air), before mapping into <see cref="CustomerJourney"/>.
/// Column names intentionally mirror the KQL `project` clause exactly
/// (PascalCase, as returned by the Kusto table), because the mapper logic in the
/// original JavaScript (map-v6.js / map-air.js) reads the raw property names.
/// Unknown/extra columns are preserved in <see cref="Extra"/> so a future KQL
/// change does not require a model change everywhere.
///
/// IMPORTANT (behavioral-parity decision, see MIGRATION.md): the original app's
/// GET /api/journeys returns these RAW rows unchanged - journey mapping
/// (map-v6.js/map-air.js) and domain rollups (domain-health.js/domain-comparison.js)
/// run client-side in the browser against this exact shape. JourneyMonitor keeps that
/// contract for v1 (GetJourneysFunction also returns raw rows, and JourneyMonitor.Web
/// serves the same unmodified map-v6.js/map-air.js/domain-health.js/domain-comparison.js/
/// app.js) so the UI is pixel- and behavior-identical with zero re-implementation risk.
/// HotelJourneyMapper/AirJourneyMapper/DomainHealthCalculator/DomainComparisonCalculator
/// are still fully ported and characterization-tested in Core; they are the ready-to-wire
/// foundation for a future phase that moves this computation server-side behind a
/// CustomerJourneyDto contract, without needing another behavioral-risk migration pass.
/// </summary>
[JsonConverter(typeof(TelemetryJourneyRowJsonConverter))]
public sealed class TelemetryJourneyRow
{
    public Dictionary<string, JsonElement> Extra { get; } = new(StringComparer.OrdinalIgnoreCase);

    public JsonElement? Get(string name) => Extra.TryGetValue(name, out var value) ? value : null;

    public string GetString(string name)
    {
        var el = Get(name);
        if (el is null) return "";
        return el.Value.ValueKind switch
        {
            JsonValueKind.String => el.Value.GetString() ?? "",
            JsonValueKind.Number => el.Value.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => "",
        };
    }

    public double? GetDouble(string name)
    {
        var el = Get(name);
        if (el is null) return null;
        if (el.Value.ValueKind == JsonValueKind.Number && el.Value.TryGetDouble(out var d)) return d;
        if (el.Value.ValueKind == JsonValueKind.String && double.TryParse(el.Value.GetString(), out var ds)) return ds;
        return null;
    }

    public int GetInt(string name) => (int)Math.Round(GetDouble(name) ?? 0);

    public DateTimeOffset? GetDateTimeOffset(string name)
    {
        var el = Get(name);
        if (el is null) return null;
        if (el.Value.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(el.Value.GetString(), out var fromString))
        {
            return fromString;
        }

        return null;
    }

    public bool GetBool(string name)
    {
        var el = Get(name);
        if (el is null) return false;
        return el.Value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => string.Equals(el.Value.GetString(), "true", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    /// <summary>The `Journey` column: an array (or object-of-arrays, from Kusto's JSON encoding) of stage bags.</summary>
    public List<JsonElement> GetJourneyStages()
    {
        var el = Get("Journey");
        if (el is null) return new();
        if (el.Value.ValueKind == JsonValueKind.Array) return el.Value.EnumerateArray().ToList();
        if (el.Value.ValueKind == JsonValueKind.Object) return el.Value.EnumerateObject().Select(p => p.Value).ToList();
        return new();
    }
}

/// <summary>
/// Result envelope for GET /api/journeys - matches the server.js/Function JSON shape exactly.
/// <see cref="Rows"/> stays as RAW telemetry rows (see the note on <see cref="TelemetryJourneyRow"/>):
/// the browser's map-v6.js/map-air.js do the journey mapping, exactly as in the original app.
/// </summary>
public sealed class JourneysResult
{
    public required string Source { get; set; } // live | demo | snapshot | error
    public required string Win { get; set; }
    public string? Domain { get; set; }
    public string? Environment { get; set; }
    public string? ClubId { get; set; }
    public string? Host { get; set; }
    public int? RowCap { get; set; }
    public string? FetchedAt { get; set; }
    public int RowCount { get; set; }
    public List<TelemetryJourneyRow> Rows { get; set; } = new();
    public bool Cached { get; set; }
    public string? Warning { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// Serializes a <see cref="TelemetryJourneyRow"/> as a flat JSON object (its <c>Extra</c>
/// dictionary), matching tableToRows()'s plain-object row shape in the original JS app -
/// not <c>{"Extra": {...}}</c>. Also used for deserializing cached payloads back out of
/// <see cref="Abstractions.ITelemetryQueryCache"/>.
/// </summary>
public sealed class TelemetryJourneyRowJsonConverter : JsonConverter<TelemetryJourneyRow>
{
    public override TelemetryJourneyRow Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var row = new TelemetryJourneyRow();
        using var document = JsonDocument.ParseValue(ref reader);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            row.Extra[property.Name] = property.Value.Clone();
        }
        return row;
    }

    public override void Write(Utf8JsonWriter writer, TelemetryJourneyRow value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var (name, element) in value.Extra)
        {
            writer.WritePropertyName(name);
            element.WriteTo(writer);
        }
        writer.WriteEndObject();
    }
}

/// <summary>One entry in the Club dropdown.</summary>
public sealed class ClubOption
{
    public required string ClubId { get; set; }
    public string Host { get; set; } = "";
    /// <summary>ICE partner id for this dropdown row. Air and Cruises use this as the club value.</summary>
    public string PartnerId { get; set; } = "";
    public required string Value { get; set; }
    public required string Label { get; set; }
    public int Events { get; set; }
}

/// <summary>Result envelope for GET /api/clubs.</summary>
public sealed class ClubsResult
{
    public required string Source { get; set; } // live | demo | configured | none | error
    public required string Win { get; set; }
    public required string Domain { get; set; }
    public string? Environment { get; set; }
    public string? FetchedAt { get; set; }
    public List<ClubOption> Clubs { get; set; } = new();
    public bool Cached { get; set; }
    public bool Disabled { get; set; }
    public string? Placeholder { get; set; }
    public string? Warning { get; set; }
    public string? Error { get; set; }
}
