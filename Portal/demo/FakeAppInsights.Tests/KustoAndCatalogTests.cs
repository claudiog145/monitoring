using FakeAppInsights.Catalog;
using FakeAppInsights.Kusto;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FakeAppInsights.Tests;

public class KustoAndCatalogTests
{
    [Fact]
    public void V1Response_DropsMetadataTables()
    {
        var v1 = FakeKusto.QueryV1(
            FakeKusto.Table("Table_0", [("a", "long", "Int64")], "[[1]]"),
            FakeKusto.Table("Table_0", [("b", "string", "String")], """[["x"]]"""));

        var tables = FakeKusto.Parse(v1);

        Assert.Equal(["a", "b"], tables.Select(t => t.Columns.Single().Name));
    }

    [Fact]
    public void V1Response_WithoutTableOfContents_KeepsEveryTable()
    {
        var tables = FakeKusto.Parse(FakeKusto.V1(FakeKusto.Table("Table_0", [("DatabaseName", "string", "String")], """[["a"],["b"]]""")));

        Assert.Equal(2, Assert.Single(tables).Rows.Count);
    }

    [Fact]
    public void V1Error_PrefersTheEngineMessage()
    {
        const string body = """
            {"error":{"code":"General_BadRequest","message":"Request is invalid and cannot be executed.",
             "@message":"Request is invalid and cannot be processed: Syntax error: SYN0002: Missing expression [line:position=1:9]",
             "innererror":{"code":"SYN0002","message":"Missing expression","@message":"Syntax error: SYN0002: Missing expression [line:position=1:9]"}}}
            """;

        var (message, code) = KustoV1Response.ReadError(body);

        Assert.Equal("Syntax error: SYN0002: Missing expression [line:position=1:9]", message);
        Assert.Equal("SYN0002", code);
        Assert.Equal(("plain text", (string?)null), KustoV1Response.ReadError(" plain text "));
    }

    [Fact]
    public void Snapshot_IgnoresRegistryAndEngineDatabases()
    {
        var snapshot = new ApplicationSnapshot(
            ["NetDefaultDB", "_demo_registry", "bank-web", "bad name"],
            [new DemoApplication("demo-x", "_demo_registry"), new DemoApplication("ghost", "missing-db")]);

        Assert.True(snapshot.DatabaseExists("bank-web"));
        Assert.False(snapshot.DatabaseExists("_demo_registry"));
        Assert.False(snapshot.DatabaseExists("NetDefaultDB"));
        Assert.False(snapshot.DatabaseExists("bad name"));
        Assert.Null(snapshot.ResolveApp("demo-x"));
        Assert.Null(snapshot.ResolveApp("ghost"));
        Assert.Null(snapshot.ResolveApp("_demo_registry"));
        Assert.Equal([new DemoApplication("bank-web", "bank-web")], snapshot.Applications);
    }

    [Fact]
    public void Snapshot_RegistryWinsOverSameNameAndConflictsPickLowestDatabase()
    {
        var snapshot = new ApplicationSnapshot(
            ["a-db", "b-db", "shared"],
            [new DemoApplication("shared", "b-db"), new DemoApplication("dup", "b-db"), new DemoApplication("dup", "a-db")]);

        Assert.Equal("b-db", snapshot.ResolveApp("shared"));
        Assert.Equal("a-db", snapshot.ResolveApp("DUP"));
        Assert.Equal("a-db", snapshot.FindDatabaseByAppId("demo-a-db"));
        Assert.Null(snapshot.FindDatabaseByAppId("a-db"));
    }

    [Fact]
    public async Task Directory_CachesAndRefreshesNewDatabases()
    {
        var kusto = new FakeKusto();
        var clock = new ManualClock();
        using var directory = new KustoApplicationDirectory(kusto, Options.Create(new FakeAppInsightsOptions()), clock,
            NullLogger<KustoApplicationDirectory>.Instance);

        Assert.Equal("bank-web", (await directory.GetAsync(default)).ResolveApp("demo-bank-web"));
        kusto.Databases.Add("retail-web");
        Assert.Null((await directory.GetAsync(default)).ResolveApp("retail-web"));
        Assert.Null((await directory.RefreshAsync(default)).ResolveApp("retail-web"));

        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal("retail-web", (await directory.RefreshAsync(default)).ResolveApp("demo-retail-web"));

        kusto.Databases.Remove("retail-web");
        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.NotNull((await directory.GetAsync(default)).ResolveApp("retail-web"));
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Null((await directory.GetAsync(default)).ResolveApp("retail-web"));
    }

    [Fact]
    public async Task Directory_WithoutRegistryDatabase_UsesNameFallbacks()
    {
        var kusto = new FakeKusto();
        kusto.Databases.Remove(ApplicationSnapshot.RegistryDatabase);
        using var directory = new KustoApplicationDirectory(kusto, Options.Create(new FakeAppInsightsOptions()), new ManualClock(),
            NullLogger<KustoApplicationDirectory>.Instance);

        var snapshot = await directory.GetAsync(default);

        Assert.Equal("bank-mobile", snapshot.ResolveApp("demo-bank-mobile"));
        Assert.Null(snapshot.ResolveApp("mobile-app-1"));
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
