using System.Text.Json;

namespace FakeAppInsights.Kusto;

/// <summary>
/// Reads a Kusto REST v1 response (<c>{"Tables":[{"TableName","Columns","Rows"}]}</c>). A query
/// response ends with a table of contents (<c>Ordinal, Kind, Name, Id, PrettyName</c>); only the
/// tables it lists as <c>QueryResult</c> are data. <c>@ExtendedProperties</c>, <c>QueryStatus</c> and
/// the table of contents itself are dropped. Management responses have no table of contents.
/// </summary>
public static class KustoV1Response
{
    private static readonly string[] TocColumns = ["Ordinal", "Kind", "Name", "Id", "PrettyName"];

    public static IReadOnlyList<KustoTable> Parse(JsonElement root)
    {
        if (!root.TryGetProperty("Tables", out var tablesElement) || tablesElement.ValueKind != JsonValueKind.Array)
            throw new KustoUnavailableException("The engine response has no 'Tables' array.");

        var tables = tablesElement.EnumerateArray().Select(ReadTable).ToList();
        if (tables.Count < 2 || !IsTableOfContents(tables[^1])) return tables;

        var results = new List<KustoTable>();
        foreach (var entry in tables[^1].Rows)
        {
            if (entry.Count < 2 || entry[1].ValueKind != JsonValueKind.String || entry[1].GetString() != "QueryResult") continue;
            if (entry[0].ValueKind == JsonValueKind.Number && entry[0].TryGetInt32(out var ordinal) && ordinal >= 0 && ordinal < tables.Count - 1)
                results.Add(tables[ordinal]);
        }
        return results;
    }

    /// <summary>The most specific message of a Kusto error body; the raw text when it is not JSON.</summary>
    public static (string Message, string? Code) ReadError(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                var code = Text(error, "code");
                if (error.TryGetProperty("innererror", out var inner) && inner.ValueKind == JsonValueKind.Object)
                {
                    code = Text(inner, "code") ?? code;
                    var innerMessage = Text(inner, "@message") ?? Text(inner, "message");
                    if (innerMessage is not null) return (innerMessage, code);
                }
                return (Text(error, "@message") ?? Text(error, "message") ?? body, code);
            }
        }
        catch (JsonException)
        {
        }
        return (body.Trim(), null);
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool IsTableOfContents(KustoTable table) =>
        table.Columns.Select(c => c.Name).SequenceEqual(TocColumns, StringComparer.Ordinal);

    private static KustoTable ReadTable(JsonElement table)
    {
        var name = table.TryGetProperty("TableName", out var n) ? n.GetString() ?? "" : "";
        var columns = new List<KustoColumn>();
        if (table.TryGetProperty("Columns", out var cols) && cols.ValueKind == JsonValueKind.Array)
        {
            foreach (var column in cols.EnumerateArray())
            {
                columns.Add(new KustoColumn(
                    Text(column, "ColumnName") ?? "",
                    Text(column, "ColumnType") ?? "",
                    Text(column, "DataType") ?? ""));
            }
        }

        var rows = new List<IReadOnlyList<JsonElement>>();
        if (table.TryGetProperty("Rows", out var rowsElement) && rowsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in rowsElement.EnumerateArray())
            {
                if (row.ValueKind == JsonValueKind.Array) rows.Add(row.EnumerateArray().Select(v => v.Clone()).ToList());
            }
        }
        return new KustoTable(name, columns, rows);
    }
}
