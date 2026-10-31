using System.Text.Json;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Infrastructure.Demo;

/// <summary>Paths for the offline fallback fixtures. Snapshot is optional/gitignored in the
/// original app (an exported PROD dump); the anonymized demo fixture ships with the repo.</summary>
public sealed class DemoJourneyFixtureOptions
{
    public const string SectionName = "DemoJourneyFixture";

    /// <summary>Optional path to a last-exported PROD snapshot (tmo-hotel-v6-sample.json equivalent).
    /// Never committed to source control; absent by default.</summary>
    public string? SnapshotPath { get; set; }

    /// <summary>Path to the anonymized demo fixture shipped with the app (demo-journeys.json).
    /// Defaults to the copy under Infrastructure/Demo/Data next to the assembly.</summary>
    public string? DemoPath { get; set; }
}

/// <summary>
/// Direct port of loadSnapshot()/loadDemo() in server.js: the last-resort offline data used
/// when a live Application Insights query fails for Hotels/club 15639 (the only combination
/// the original app ships an offline dump for). Timestamps in the demo fixture are shifted
/// onto "now" so a fresh run never reads as months-old, exactly like the JS version.
/// </summary>
public sealed class DemoJourneyFixtureProvider(IOptions<DemoJourneyFixtureOptions> options) : IDemoJourneyFixtureProvider
{
    private string SnapshotPath => options.Value.SnapshotPath
        ?? Path.Combine(AppContext.BaseDirectory, "Demo", "Data", "tmo-hotel-v6-sample.json");

    private string DemoPath => options.Value.DemoPath
        ?? Path.Combine(AppContext.BaseDirectory, "Demo", "Data", "demo-journeys.json");

    public bool HasSnapshot => File.Exists(SnapshotPath);

    public async Task<JourneysResult> LoadSnapshotAsync(string win, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(SnapshotPath);
        var raw = await JsonSerializer.DeserializeAsync<JsonElement>(stream, cancellationToken: cancellationToken);
        var rows = raw.ValueKind == JsonValueKind.Array ? ReadRows(raw) : new List<TelemetryJourneyRow>();
        return new JourneysResult
        {
            Source = "snapshot",
            Win = win,
            FetchedAt = null,
            RowCount = rows.Count,
            Rows = rows,
            Cached = false,
            Warning = "Live App Insights query failed; serving last exported PROD snapshot.",
        };
    }

    public async Task<JourneysResult> LoadDemoAsync(string win, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(DemoPath);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        var rawRows = root.TryGetProperty("rows", out var rowsElement) && rowsElement.ValueKind == JsonValueKind.Array
            ? ReadRows(rowsElement)
            : new List<TelemetryJourneyRow>();

        var shiftMs = 0d;
        if (root.TryGetProperty("timeAnchor", out var anchorElement) && anchorElement.ValueKind == JsonValueKind.String)
        {
            if (DateTimeOffset.TryParse(anchorElement.GetString(), out var anchor))
            {
                shiftMs = (DateTimeOffset.UtcNow - anchor).TotalMilliseconds;
            }
        }

        var shifted = rawRows.Select(row => ShiftRow(row, shiftMs)).ToList();

        return new JourneysResult
        {
            Source = "demo",
            Win = win,
            FetchedAt = DateTimeOffset.UtcNow.ToString("O"),
            RowCount = shifted.Count,
            Rows = shifted,
            Cached = false,
            Warning = "Demo mode: anonymized fixture, not live PROD data. Run `az login` for live journeys.",
        };
    }

    private static List<TelemetryJourneyRow> ReadRows(JsonElement arrayElement)
    {
        var rows = new List<TelemetryJourneyRow>();
        foreach (var item in arrayElement.EnumerateArray())
        {
            var row = new TelemetryJourneyRow();
            foreach (var property in item.EnumerateObject())
            {
                row.Extra[property.Name] = property.Value.Clone();
            }
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>Shifts FirstSeen/LastSeen and every stage's `t` by shiftMs, mirroring the
    /// shift()/loadDemo() closure in server.js exactly (including leaving unparsable values as-is).</summary>
    private static TelemetryJourneyRow ShiftRow(TelemetryJourneyRow row, double shiftMs)
    {
        var shifted = new TelemetryJourneyRow();
        foreach (var (name, value) in row.Extra)
        {
            shifted.Extra[name] = name switch
            {
                "FirstSeen" or "LastSeen" => ShiftTimestamp(value, shiftMs),
                "Journey" => ShiftJourneyStages(value, shiftMs),
                _ => value,
            };
        }
        return shifted;
    }

    private static JsonElement ShiftJourneyStages(JsonElement journey, double shiftMs)
    {
        if (journey.ValueKind != JsonValueKind.Array) return journey;
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var stage in journey.EnumerateArray())
            {
                if (stage.ValueKind != JsonValueKind.Object)
                {
                    stage.WriteTo(writer);
                    continue;
                }
                writer.WriteStartObject();
                foreach (var property in stage.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    if (property.Name == "t")
                    {
                        ShiftTimestamp(property.Value, shiftMs).WriteTo(writer);
                    }
                    else
                    {
                        property.Value.WriteTo(writer);
                    }
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    private static JsonElement ShiftTimestamp(JsonElement value, double shiftMs)
    {
        if (value.ValueKind != JsonValueKind.String) return value;
        var text = value.GetString();
        if (string.IsNullOrEmpty(text) || !DateTimeOffset.TryParse(text, out var parsed)) return value;
        var shiftedIso = parsed.AddMilliseconds(shiftMs).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(shiftedIso));
        return document.RootElement.Clone();
    }
}
