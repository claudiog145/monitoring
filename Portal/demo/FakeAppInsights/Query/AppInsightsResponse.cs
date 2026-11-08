using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using FakeAppInsights.Kusto;

namespace FakeAppInsights.Query;

/// <summary>
/// Turns Kusto result tables into the App Insights REST shape (contract v5 §3.1):
/// <c>{"tables":[{"name","columns":[{"name","type"}],"rows":[[…]]}]}</c>, tables named
/// <c>PrimaryResult</c>, <c>Table_1</c>, <c>Table_2</c>…; <c>datetime</c> as ISO 8601 UTC with <c>Z</c>;
/// <c>dynamic</c> as a JSON string, the way App Insights serialises it.
/// </summary>
public static class AppInsightsResponse
{
    private static readonly Dictionary<string, string> TypeAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["datetime"] = "datetime", ["date"] = "datetime", ["system.datetime"] = "datetime",
        ["string"] = "string", ["system.string"] = "string",
        ["dynamic"] = "dynamic", ["object"] = "dynamic", ["system.object"] = "dynamic",
        ["real"] = "real", ["double"] = "real", ["system.double"] = "real",
        ["long"] = "long", ["int64"] = "long", ["system.int64"] = "long",
        ["int"] = "int", ["int32"] = "int", ["system.int32"] = "int",
        ["bool"] = "bool", ["boolean"] = "bool", ["sbyte"] = "bool", ["system.boolean"] = "bool", ["system.sbyte"] = "bool",
        ["timespan"] = "timespan", ["time"] = "timespan", ["system.timespan"] = "timespan",
        ["guid"] = "guid", ["uniqueidentifier"] = "guid", ["system.guid"] = "guid",
        ["decimal"] = "decimal", ["sqldecimal"] = "decimal", ["system.decimal"] = "decimal", ["system.data.sqltypes.sqldecimal"] = "decimal",
    };

    public static JsonObject Build(IReadOnlyList<KustoTable> tables)
    {
        var result = new JsonArray();
        for (var i = 0; i < tables.Count; i++)
        {
            result.Add(BuildTable(i == 0 ? "PrimaryResult" : $"Table_{i}", tables[i]));
        }
        return new JsonObject { ["tables"] = result };
    }

    /// <summary>The App Insights type name for a Kusto column (KQL type first, CLR type as fallback).</summary>
    public static string MapType(KustoColumn column)
    {
        if (TypeAliases.TryGetValue(column.ColumnType, out var type)) return type;
        if (TypeAliases.TryGetValue(column.DataType, out type)) return type;
        return "string";
    }

    private static JsonObject BuildTable(string name, KustoTable table)
    {
        var types = table.Columns.Select(MapType).ToList();
        var columns = new JsonArray();
        for (var i = 0; i < table.Columns.Count; i++)
        {
            columns.Add(new JsonObject { ["name"] = table.Columns[i].Name, ["type"] = types[i] });
        }

        var rows = new JsonArray();
        foreach (var row in table.Rows)
        {
            var cells = new JsonArray();
            for (var i = 0; i < row.Count; i++)
            {
                cells.Add(ConvertCell(row[i], i < types.Count ? types[i] : "string"));
            }
            rows.Add(cells);
        }
        return new JsonObject { ["name"] = name, ["columns"] = columns, ["rows"] = rows };
    }

    private static JsonNode? ConvertCell(JsonElement value, string type)
    {
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        return type switch
        {
            "dynamic" => JsonValue.Create(DynamicText(value)),
            "datetime" => JsonValue.Create(UtcIso(value)),
            "bool" => value.ValueKind switch
            {
                JsonValueKind.Number when value.TryGetInt64(out var n) => JsonValue.Create(n != 0),
                JsonValueKind.String when bool.TryParse(value.GetString(), out var b) => JsonValue.Create(b),
                _ => JsonNode.Parse(value.GetRawText()),
            },
            _ => JsonNode.Parse(value.GetRawText()),
        };
    }

    /// <summary>Objects and arrays as compact JSON text; strings unchanged; numbers and booleans as their JSON text.</summary>
    private static string DynamicText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.Object or JsonValueKind.Array => JsonSerializer.Serialize(value),
        _ => value.GetRawText(),
    };

    private static string UtcIso(JsonElement value)
    {
        var text = value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.GetRawText();
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture)
            : text;
    }
}
