using System.Text.Json;
using JourneyMonitor.Core.Domain;

namespace JourneyMonitor.Infrastructure.Sql;

/// <summary>Restores SQL result columns to the names and JSON types returned by KQL.</summary>
public static class KqlRowAssembler
{
    private static readonly HashSet<string> JsonColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        "Steps", "Journey", "Sources", "BookedReservations",
    };

    private static readonly Dictionary<string, string> KqlNameByColumn = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SearchToConfirmSeconds"] = "SearchToConfirm_s",
        ["StepsJson"] = "Steps",
        ["JourneyJson"] = "Journey",
        ["SourcesJson"] = "Sources",
    };

    private static readonly HashSet<string> InternalColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        "JourneyId", "QueryRunId", "JourneyStepId",
    };

    public static TelemetryJourneyRow Assemble(string domain, IDictionary<string, object?> record)
    {
        _ = domain; // Domain is retained for future per-vertical projections.
        var row = new TelemetryJourneyRow();

        foreach (var (column, value) in record)
        {
            if (InternalColumns.Contains(column)) continue;

            var name = KqlNameByColumn.TryGetValue(column, out var kqlName) ? kqlName : column;
            row.Extra[name] = ToJsonElement(name, value);
        }

        return row;
    }

    private static JsonElement ToJsonElement(string name, object? value)
    {
        if (value is null) return JsonSerializer.SerializeToElement((string?)null);

        if (JsonColumns.Contains(name) && value is string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return JsonSerializer.SerializeToElement((string?)null);
            }

            try
            {
                using var document = JsonDocument.Parse(json);
                return document.RootElement.Clone();
            }
            catch (JsonException)
            {
                return JsonSerializer.SerializeToElement(json);
            }
        }

        return value switch
        {
            DateTimeOffset dto => JsonSerializer.SerializeToElement(dto.ToString("O")),
            DateTime dt => JsonSerializer.SerializeToElement(dt.ToString("O")),
            decimal dec => JsonSerializer.SerializeToElement(dec),
            _ => JsonSerializer.SerializeToElement(value),
        };
    }
}
