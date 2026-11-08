using System.Net.Http.Json;
using System.Text.Json;

namespace DemoGenerator.Kusto;

public sealed class KustoCommandException(string message, int statusCode) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>First result table of a Kusto v1 REST response: column names and raw JSON rows.</summary>
public sealed record KustoResult(IReadOnlyList<string> Columns, IReadOnlyList<JsonElement[]> Rows)
{
    public static readonly KustoResult Empty = new([], []);

    public int Ordinal(string column)
    {
        for (var i = 0; i < Columns.Count; i++) if (string.Equals(Columns[i], column, StringComparison.Ordinal)) return i;
        throw new KeyNotFoundException($"Column '{column}' is not in the result ({string.Join(", ", Columns)}).");
    }
}

/// <summary>The two Kusto REST verbs the generator needs. One implementation talks HTTP; tests fake it.</summary>
public interface IKustoClient
{
    Task<KustoResult> QueryAsync(string database, string csl, CancellationToken ct = default);

    Task<KustoResult> ExecuteCommandAsync(string database, string csl, CancellationToken ct = default);
}

/// <summary>
/// Kustainer over plain HTTP (<c>POST /v1/rest/query|mgmt</c> with <c>{"db","csl"}</c>, no auth). No SDK on
/// purpose: Kustainer needs none, and every command the generator sends is a single text.
/// </summary>
public sealed class KustoRestClient(HttpClient http) : IKustoClient
{
    public Task<KustoResult> QueryAsync(string database, string csl, CancellationToken ct = default) =>
        SendAsync("v1/rest/query", database, csl, ct);

    public Task<KustoResult> ExecuteCommandAsync(string database, string csl, CancellationToken ct = default) =>
        SendAsync("v1/rest/mgmt", database, csl, ct);

    private async Task<KustoResult> SendAsync(string path, string database, string csl, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(path, new { db = database, csl }, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new KustoCommandException($"Kusto {(int)response.StatusCode} on {database}: {ErrorMessage(body)}", (int)response.StatusCode);

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("Tables", out var tables) || tables.GetArrayLength() == 0) return KustoResult.Empty;
        var table = tables[0];
        var columns = table.GetProperty("Columns").EnumerateArray().Select(c => c.GetProperty("ColumnName").GetString() ?? "").ToList();
        var rows = table.GetProperty("Rows").EnumerateArray().Select(r => r.EnumerateArray().Select(v => v.Clone()).ToArray()).ToList();
        return new KustoResult(columns, rows);
    }

    private static string ErrorMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error))
            {
                if (error.TryGetProperty("@message", out var detail) && detail.GetString() is { Length: > 0 } d) return d;
                if (error.TryGetProperty("message", out var message)) return message.GetString() ?? body;
            }
        }
        catch (JsonException)
        {
        }
        return body.Length > 600 ? body[..600] : body;
    }
}
