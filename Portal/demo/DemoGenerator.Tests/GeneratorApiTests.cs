using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DemoGenerator.Kusto;
using DemoGenerator.Synthesis;

namespace DemoGenerator.Tests;

public class GeneratorApiTests : IClassFixture<DemoAppFactory>
{
    private readonly DemoAppFactory _factory;

    public GeneratorApiTests(DemoAppFactory factory) => _factory = factory;

    [Fact]
    public async Task Companies_ListsLoadedPacksWithScenarios()
    {
        using var client = _factory.CreateClient();

        var companies = await client.GetFromJsonAsync<JsonElement>("/api/companies");

        var company = Assert.Single(companies.EnumerateArray());
        Assert.Equal("fixture", company.GetProperty("companyKey").GetString());
        Assert.Equal("fixture-demo", company.GetProperty("profileKey").GetString());
        Assert.Equal("Fixture Shop (demo)", company.GetProperty("displayName").GetString());
        Assert.Equal(2, company.GetProperty("apps").GetArrayLength());
        Assert.Contains(company.GetProperty("scenarios").EnumerateArray(), s => s.GetProperty("key").GetString() == "pay-gw-down" && s.GetProperty("title").GetString() == "Payment gateway down");
    }

    [Fact]
    public async Task Scenarios_StartListStopAndUnknownIs404()
    {
        using var client = _factory.CreateClient();

        var started = await client.PostAsJsonAsync("/api/scenarios/fixture/cart-errors/start", new { durationMinutes = 15 });
        var unknownScenario = await client.PostAsJsonAsync("/api/scenarios/fixture/nope/start", new { });
        var unknownCompany = await client.PostAsJsonAsync("/api/scenarios/nobody/cart-errors/start", new { });
        var active = await client.GetFromJsonAsync<JsonElement>("/api/scenarios/active");
        var stopped = await client.PostAsJsonAsync("/api/scenarios/stop", new { companyKey = "fixture" });
        var afterStop = await client.GetFromJsonAsync<JsonElement>("/api/scenarios/active");

        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        var body = await started.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(15, (body.GetProperty("endsAt").GetDateTimeOffset() - body.GetProperty("startedAt").GetDateTimeOffset()).TotalMinutes);
        Assert.Equal(HttpStatusCode.NotFound, unknownScenario.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknownCompany.StatusCode);
        Assert.Contains(active.EnumerateArray(), s => s.GetProperty("scenarioKey").GetString() == "cart-errors" && s.GetProperty("companyKey").GetString() == "fixture");
        Assert.Equal(HttpStatusCode.OK, stopped.StatusCode);
        Assert.DoesNotContain(afterStop.EnumerateArray(), s => s.GetProperty("scenarioKey").GetString() == "cart-errors");
    }

    [Fact]
    public async Task Reset_RecreatesDatabasesRegistersAppIdsAndReportsRows()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/reset", new { companies = new[] { "fixture" }, days = 1, scale = "tiny" });
        var badScale = await client.PostAsJsonAsync("/api/reset", new { scale = "huge" });
        var badCompany = await client.PostAsJsonAsync("/api/reset", new { companies = new[] { "nobody" } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var company = Assert.Single(body.GetProperty("companies").EnumerateArray());
        Assert.Equal("fixture", company.GetProperty("companyKey").GetString());
        Assert.Equal(20, company.GetProperty("rows").GetProperty("requests").GetInt64());
        Assert.Equal(2, company.GetProperty("rows").GetProperty("customEvents").GetInt64());
        Assert.True(body.TryGetProperty("elapsedSeconds", out _));
        Assert.Equal(HttpStatusCode.BadRequest, badScale.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badCompany.StatusCode);

        var commands = _factory.Kusto.Snapshot();
        Assert.Contains(commands, c => c.Csl.StartsWith(".create database ['fixture-web'] persist", StringComparison.Ordinal));
        Assert.Contains(commands, c => c.Database == "fixture-api" && c.Csl.StartsWith(".drop tables (requests, dependencies, exceptions, traces, pageViews, customEvents) ifexists", StringComparison.Ordinal));
        Assert.Contains(commands, c => c.Database == "fixture-web" && c.Csl.StartsWith(".create-merge table customEvents", StringComparison.Ordinal));
        Assert.Contains(commands, c => c.Database == DemoKustoAdmin.RegistryDatabase
            && c.Csl == ".set-or-replace _app <| _app | where appId !in (\"demo-fixture-web\", \"demo-fixture-api\") | union (datatable(appId:string, database:string) [\"demo-fixture-web\", \"fixture-web\", \"demo-fixture-api\", \"fixture-api\"])");
        Assert.Contains(commands, c => c.Database == DemoKustoAdmin.RegistryDatabase && c.Csl.StartsWith(".set-or-replace _company", StringComparison.Ordinal));
        foreach (var table in KqlSynthesisBuilder.Tables)
            Assert.Contains(commands, c => c.Database == "fixture-web" && c.Csl.StartsWith($".set-or-append {table} <|", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Feeder_StartsAndStopsAndHealthReportsIt()
    {
        using var client = _factory.CreateClient();

        var start = await client.PostAsync("/api/feeder/start", null);
        var running = await client.GetFromJsonAsync<JsonElement>("/health");
        var stop = await client.PostAsync("/api/feeder/stop", null);
        var stopped = await client.GetFromJsonAsync<JsonElement>("/health");
        var unknown = await client.PostAsync("/api/feeder/pause", null);

        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        Assert.Equal("running", running.GetProperty("feeder").GetString());
        Assert.Equal(HttpStatusCode.OK, stop.StatusCode);
        Assert.Equal("stopped", stopped.GetProperty("feeder").GetString());
        Assert.Equal("ok", stopped.GetProperty("status").GetString());
        Assert.Equal("reachable", stopped.GetProperty("kusto").GetString());
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task Bodies_AreOptionalAndMalformedJsonIs400()
    {
        using var client = _factory.CreateClient();

        var noBody = await client.PostAsync("/api/scenarios/fixture/flash-sale/start", null);
        var stopAll = await client.PostAsync("/api/scenarios/stop", null);
        var malformed = await client.PostAsync("/api/reset", new StringContent("{ not json", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, noBody.StatusCode);
        var body = await noBody.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(60, (body.GetProperty("endsAt").GetDateTimeOffset() - body.GetProperty("startedAt").GetDateTimeOffset()).TotalMinutes);
        Assert.Equal(HttpStatusCode.OK, stopAll.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
    }

    [Fact]
    public void Schema_IsEmbeddedWithTheSixTables()
    {
        var schema = DemoKustoAdmin.Schema;

        Assert.Equal(6, schema.Count);
        foreach (var table in KqlSynthesisBuilder.Tables)
            Assert.Contains(schema, c => c.StartsWith($".create-merge table {table} (timestamp:datetime", StringComparison.Ordinal));
    }
}
