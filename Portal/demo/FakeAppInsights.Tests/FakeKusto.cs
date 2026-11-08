using System.Collections.Concurrent;
using System.Text.Json;
using FakeAppInsights.Catalog;
using FakeAppInsights.Kusto;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace FakeAppInsights.Tests;

/// <summary>
/// An in-memory Kustainer: answers <c>.show databases</c>, the appId registry and, for app queries,
/// whatever <see cref="Respond"/> returns as a raw REST v1 body (metadata tables included).
/// </summary>
public sealed class FakeKusto : IKustoExecutor
{
    public List<string> Databases { get; } = ["NetDefaultDB", "bank-web", "bank-mobile", ApplicationSnapshot.RegistryDatabase];

    public List<(string AppId, string Database)> Registry { get; } = [("demo-bank-web", "bank-web"), ("mobile-app-1", "bank-mobile")];

    public ConcurrentQueue<(string Database, string Csl)> Queries { get; } = new();

    public Func<string, string, string> Respond { get; set; } = (_, _) => QueryV1(Table("Table_0", [("x", "long", "Int64")], "[[1]]"));

    public Exception? Throw { get; set; }

    public Task<IReadOnlyList<KustoTable>> ManagementAsync(string database, string csl, CancellationToken cancellationToken)
    {
        var rows = string.Join(",", Databases.Select(d => $"[{JsonSerializer.Serialize(d)}]"));
        return Task.FromResult(Parse(V1(Table("Table_0", [("DatabaseName", "string", "String")], $"[{rows}]"))));
    }

    public Task<IReadOnlyList<KustoTable>> QueryAsync(string database, string csl, CancellationToken cancellationToken)
    {
        if (database == ApplicationSnapshot.RegistryDatabase)
        {
            var rows = string.Join(",", Registry.Select(r => $"[{JsonSerializer.Serialize(r.AppId)},{JsonSerializer.Serialize(r.Database)}]"));
            return Task.FromResult(Parse(QueryV1(Table("Table_0", [("appId", "string", "String"), ("database", "string", "String")], $"[{rows}]"))));
        }

        Queries.Enqueue((database, csl));
        if (Throw is not null) throw Throw;
        return Task.FromResult(Parse(Respond(database, csl)));
    }

    public static IReadOnlyList<KustoTable> Parse(string v1)
    {
        using var document = JsonDocument.Parse(v1);
        return KustoV1Response.Parse(document.RootElement);
    }

    public static string Table(string name, (string Name, string ColumnType, string DataType)[] columns, string rowsJson) =>
        $$"""{"TableName":"{{name}}","Columns":[{{string.Join(",", columns.Select(c => $$"""{"ColumnName":"{{c.Name}}","DataType":"{{c.DataType}}","ColumnType":"{{c.ColumnType}}"}"""))}}],"Rows":{{rowsJson}}}""";

    /// <summary>A management-style body: the tables as given, no table of contents.</summary>
    public static string V1(params string[] tables) => $$"""{"Tables":[{{string.Join(",", tables)}}]}""";

    /// <summary>A query body the way Kustainer returns it: results, @ExtendedProperties, QueryStatus and the table of contents.</summary>
    public static string QueryV1(params string[] results)
    {
        var all = results.Select((t, i) => t.Replace("\"TableName\":\"Table_0\"", $"\"TableName\":\"Table_{i}\"")).ToList();
        var n = all.Count;
        all.Add(Table($"Table_{n}", [("Value", "string", "String")], """[["{\"Visualization\":null}"]]"""));
        all.Add(Table($"Table_{n + 1}", [("Timestamp", "datetime", "DateTime"), ("Severity", "int", "Int32"), ("StatusDescription", "string", "String")],
            """[["2026-11-07T17:42:15.0130334Z",4,"Query completed successfully"]]"""));
        var toc = new List<string>();
        for (var i = 0; i < n; i++) toc.Add($$"""[{{i}},"QueryResult","PrimaryResult","00000000-0000-0000-0000-00000000000{{i}}",""]""");
        toc.Add($$"""[{{n}},"QueryProperties","@ExtendedProperties","00000000-0000-0000-0000-0000000000aa",""]""");
        toc.Add($$"""[{{n + 1}},"QueryStatus","QueryStatus","00000000-0000-0000-0000-000000000000",""]""");
        all.Add(Table($"Table_{n + 2}",
            [("Ordinal", "long", "Int64"), ("Kind", "string", "String"), ("Name", "string", "String"), ("Id", "string", "String"), ("PrettyName", "string", "String")],
            "[" + string.Join(",", toc) + "]"));
        return V1([.. all]);
    }
}

/// <summary>The real app with <see cref="FakeKusto"/> in place of Kustainer.</summary>
public sealed class FakeAppInsightsFactory : WebApplicationFactory<Program>
{
    public const string ApiKey = "test-demo-key";

    public FakeKusto Kusto { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("FakeAppInsights:ApiKey", ApiKey);
        builder.UseSetting("FakeAppInsights:KustoUrl", "http://127.0.0.1:1");
        builder.ConfigureTestServices(services => services.AddSingleton<IKustoExecutor>(Kusto));
    }

    public HttpClient CreateAuthorizedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ApiKey);
        return client;
    }
}
