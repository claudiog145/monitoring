using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FakeAppInsights.Kusto;

namespace FakeAppInsights.Tests;

public class QueryEndpointTests
{
    private static Task<HttpResponseMessage> PostQuery(HttpClient client, string appId, string query, string? timespan = null) =>
        client.PostAsJsonAsync($"/v1/apps/{appId}/query", timespan is null ? new { query } : (object)new { query, timespan });

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    [Fact]
    public async Task Query_WithoutApiKey_Returns401()
    {
        using var factory = new FakeAppInsightsFactory();
        using var client = factory.CreateClient();

        var missing = await PostQuery(client, "demo-bank-web", "requests | take 1");
        client.DefaultRequestHeaders.Add("X-Api-Key", "wrong-key");
        var wrong = await PostQuery(client, "demo-bank-web", "requests | take 1");

        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        var body = await Json(missing);
        Assert.Equal("InvalidApiKeyError", body.GetProperty("error").GetProperty("code").GetString());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("error").GetProperty("message").GetString()));
        Assert.Empty(factory.Kusto.Queries);
    }

    [Fact]
    public async Task Query_BearerTokenWithoutApiKey_Returns401()
    {
        using var factory = new FakeAppInsightsFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "anything");

        var response = await client.GetAsync("/v1/apps/demo-bank-web/query?query=requests");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Query_UnknownApp_Returns404()
    {
        using var factory = new FakeAppInsightsFactory();
        using var client = factory.CreateAuthorizedClient();

        var response = await PostQuery(client, "demo-nope", "requests | take 1");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var error = (await Json(response)).GetProperty("error");
        Assert.Equal("ApplicationNotFoundError", error.GetProperty("code").GetString());
        Assert.Equal("ApplicationNotFoundError", error.GetProperty("innererror").GetProperty("code").GetString());
        Assert.Empty(factory.Kusto.Queries);
    }

    [Theory]
    [InlineData("demo-bank-web", "bank-web")]   // registry
    [InlineData("mobile-app-1", "bank-mobile")] // registry, unrelated name
    [InlineData("bank-mobile", "bank-mobile")]  // same name
    [InlineData("demo-bank-mobile", "bank-mobile")] // demo- prefix fallback
    public async Task Query_AppIdResolvesToDatabase(string appId, string database)
    {
        using var factory = new FakeAppInsightsFactory();
        using var client = factory.CreateAuthorizedClient();

        var response = await PostQuery(client, appId, "requests | count");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(database, Assert.Single(factory.Kusto.Queries).Database);
    }

    [Fact]
    public async Task Query_ReturnsAppInsightsShape()
    {
        using var factory = new FakeAppInsightsFactory();
        factory.Kusto.Respond = (_, _) => FakeKusto.QueryV1(FakeKusto.Table("Table_0",
            [("name", "string", "String"), ("count_", "long", "Int64"), ("avg", "real", "Double"), ("n", "int", "Int32"),
             ("ok", "bool", "SByte"), ("d", "timespan", "TimeSpan"), ("g", "guid", "Guid"), ("m", "decimal", "SqlDecimal")],
            """[["GET /a",3,1.5,7,true,"01:00:00","00000000-0000-0000-0000-000000000001","1.5"],["GET /b",1,null,0,0,null,null,null]]"""));
        using var client = factory.CreateAuthorizedClient();

        var response = await PostQuery(client, "demo-bank-web", "requests | summarize count() by name");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var root = await Json(response);
        Assert.Equal(["tables"], root.EnumerateObject().Select(p => p.Name));
        var table = Assert.Single(root.GetProperty("tables").EnumerateArray());
        Assert.Equal("PrimaryResult", table.GetProperty("name").GetString());
        Assert.Equal(
            ["name:string", "count_:long", "avg:real", "n:int", "ok:bool", "d:timespan", "g:guid", "m:decimal"],
            table.GetProperty("columns").EnumerateArray().Select(c => $"{c.GetProperty("name").GetString()}:{c.GetProperty("type").GetString()}"));
        var rows = table.GetProperty("rows");
        Assert.Equal(2, rows.GetArrayLength());
        Assert.Equal("""["GET /a",3,1.5,7,true,"01:00:00","00000000-0000-0000-0000-000000000001","1.5"]""", rows[0].GetRawText());
        Assert.Equal("""["GET /b",1,null,0,false,null,null,null]""", rows[1].GetRawText());
    }

    [Fact]
    public async Task Query_DynamicSerializedAsJsonString()
    {
        using var factory = new FakeAppInsightsFactory();
        factory.Kusto.Respond = (_, _) => FakeKusto.QueryV1(FakeKusto.Table("Table_0",
            [("customDimensions", "dynamic", "Object")],
            """[[{"BranchId":"B-017","n":[1,2]}],["plain"],[null],[42]]"""));
        using var client = factory.CreateAuthorizedClient();

        var response = await PostQuery(client, "demo-bank-web", "requests | project customDimensions");

        var table = (await Json(response)).GetProperty("tables")[0];
        Assert.Equal("dynamic", table.GetProperty("columns")[0].GetProperty("type").GetString());
        var cells = table.GetProperty("rows").EnumerateArray().Select(r => r[0]).ToList();
        Assert.Equal(JsonValueKind.String, cells[0].ValueKind);
        Assert.Equal("""{"BranchId":"B-017","n":[1,2]}""", cells[0].GetString());
        using (var parsed = JsonDocument.Parse(cells[0].GetString()!))
            Assert.Equal("B-017", parsed.RootElement.GetProperty("BranchId").GetString());
        Assert.Equal("plain", cells[1].GetString());
        Assert.Equal(JsonValueKind.Null, cells[2].ValueKind);
        Assert.Equal("42", cells[3].GetString());
    }

    [Fact]
    public async Task Query_DatetimeIsUtcIso()
    {
        using var factory = new FakeAppInsightsFactory();
        factory.Kusto.Respond = (_, _) => FakeKusto.QueryV1(FakeKusto.Table("Table_0",
            [("timestamp", "datetime", "DateTime")],
            """[["2026-11-07T17:42:15.0248534Z"],["2026-11-07T14:00:00-03:00"],["2026-11-07T10:00:00"],[null]]"""));
        using var client = factory.CreateAuthorizedClient();

        var response = await PostQuery(client, "demo-bank-web", "requests | project timestamp");

        var table = (await Json(response)).GetProperty("tables")[0];
        Assert.Equal("datetime", table.GetProperty("columns")[0].GetProperty("type").GetString());
        var values = table.GetProperty("rows").EnumerateArray().Select(r => r[0].ValueKind == JsonValueKind.Null ? null : r[0].GetString()).ToList();
        Assert.Equal(["2026-11-07T17:42:15.0248534Z", "2026-11-07T17:00:00Z", "2026-11-07T10:00:00Z", null], values);
    }

    [Fact]
    public async Task Query_KqlError_Returns400BadArgument()
    {
        using var factory = new FakeAppInsightsFactory();
        factory.Kusto.Throw = new KustoQueryException(
            "Semantic error: SEM0100: 'where' operator: Failed to resolve column or scalar expression named 'nope'", "SEM0100");
        using var client = factory.CreateAuthorizedClient();

        var response = await PostQuery(client, "demo-bank-web", "requests | where nope > 1");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = (await Json(response)).GetProperty("error");
        Assert.Equal("BadArgumentError", error.GetProperty("code").GetString());
        Assert.Contains("Failed to resolve column or scalar expression named 'nope'",
            error.GetProperty("innererror").GetProperty("message").GetString());
    }

    [Fact]
    public async Task Query_UnknownAppReference_Returns400WithContractMessage()
    {
        using var factory = new FakeAppInsightsFactory();
        using var client = factory.CreateAuthorizedClient();

        var response = await PostQuery(client, "demo-bank-web", "app('ghost').requests | count");
        var workspace = await PostQuery(client, "demo-bank-web", "workspace('w').requests | count");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = (await Json(response)).GetProperty("error");
        Assert.Equal("BadArgumentError", error.GetProperty("code").GetString());
        Assert.Equal("Unknown application 'ghost'.", error.GetProperty("message").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, workspace.StatusCode);
        Assert.Empty(factory.Kusto.Queries);
    }

    [Fact]
    public async Task Query_AppReferenceRewrittenToDatabase()
    {
        using var factory = new FakeAppInsightsFactory();
        using var client = factory.CreateAuthorizedClient();
        const string arm = "/subscriptions/00000000-0000-0000-0000-00000000demo/resourceGroups/rg-demo/providers/microsoft.insights/components/bank-mobile";

        var response = await PostQuery(client, "demo-bank-web", $"union requests, app('{arm}').requests, app('demo-bank-web').requests | count");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var (database, csl) = Assert.Single(factory.Kusto.Queries);
        Assert.Equal("bank-web", database);
        Assert.Equal("union requests, database('bank-mobile').requests, database('bank-web').requests | count", csl);
    }

    [Fact]
    public async Task Query_MultipleTables_NamedPrimaryResultThenTable1()
    {
        using var factory = new FakeAppInsightsFactory();
        factory.Kusto.Respond = (_, _) => FakeKusto.QueryV1(
            FakeKusto.Table("Table_0", [("a", "long", "Int64")], "[[1]]"),
            FakeKusto.Table("Table_0", [("b", "string", "String")], """[["x"]]"""),
            FakeKusto.Table("Table_0", [("c", "real", "Double")], "[[2.5]]"));
        using var client = factory.CreateAuthorizedClient();

        var response = await PostQuery(client, "demo-bank-web", "print a=1; print b='x'; print c=2.5");

        var tables = (await Json(response)).GetProperty("tables").EnumerateArray().ToList();
        Assert.Equal(["PrimaryResult", "Table_1", "Table_2"], tables.Select(t => t.GetProperty("name").GetString()));
        Assert.Equal("a", tables[0].GetProperty("columns")[0].GetProperty("name").GetString());
        Assert.Equal("b", tables[1].GetProperty("columns")[0].GetProperty("name").GetString());
        Assert.Equal(2.5, tables[2].GetProperty("rows")[0][0].GetDouble());
    }

    [Fact]
    public async Task Query_TimespanApplied()
    {
        using var factory = new FakeAppInsightsFactory();
        using var client = factory.CreateAuthorizedClient();

        var response = await PostQuery(client, "demo-bank-web",
            "requests | join kind=inner (dependencies) on operation_Id | count",
            "2026-11-01T00:00:00Z/2026-11-02T00:00:00Z");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var csl = Assert.Single(factory.Kusto.Queries).Csl;
        const string window = "timestamp between (datetime(2026-11-01T00:00:00.0000000Z) .. datetime(2026-11-02T00:00:00.0000000Z))";
        Assert.Contains($"(requests | where {window})", csl);
        Assert.Contains($"(dependencies | where {window})", csl);
    }

    [Fact]
    public async Task Query_WithoutTimespan_IsNotFiltered()
    {
        using var factory = new FakeAppInsightsFactory();
        using var client = factory.CreateAuthorizedClient();

        await PostQuery(client, "demo-bank-web", "requests | count");

        Assert.Equal("requests | count", Assert.Single(factory.Kusto.Queries).Csl);
    }

    [Fact]
    public async Task Query_GetWithQueryString_AppliesTimespan()
    {
        using var factory = new FakeAppInsightsFactory();
        using var client = factory.CreateAuthorizedClient();

        var response = await client.GetAsync("/v1/apps/demo-bank-web/query?query=" + Uri.EscapeDataString("exceptions | count") + "&timespan=PT6H");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("(exceptions | where timestamp between (datetime(", Assert.Single(factory.Kusto.Queries).Csl);
    }

    [Theory]
    [InlineData("union * | count")]
    [InlineData("union withsource=T req* | count")]
    [InlineData("find 'timeout'")]
    [InlineData("search 'timeout'")]
    [InlineData("search in (requests, exceptions) 'timeout'")]
    [InlineData("find in (requests) where name has 'x'")]
    public async Task Query_UnfilterableWithTimespan_Returns400(string query)
    {
        using var factory = new FakeAppInsightsFactory();
        using var client = factory.CreateAuthorizedClient();

        var response = await PostQuery(client, "demo-bank-web", query, "P1D");
        var withoutTimespan = await PostQuery(client, "demo-bank-web", query);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("BadArgumentError", (await Json(response)).GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.OK, withoutTimespan.StatusCode);
        Assert.Equal(query, Assert.Single(factory.Kusto.Queries).Csl);
    }

    [Theory]
    [InlineData("requests | search 'timeout'")]
    [InlineData("(requests | union exceptions) | search 'timeout'")]
    [InlineData("requests | project-away client_* | union exceptions")]
    public async Task Query_FilterableWithTimespan_Succeeds(string query)
    {
        using var factory = new FakeAppInsightsFactory();
        using var client = factory.CreateAuthorizedClient();

        var response = await PostQuery(client, "demo-bank-web", query, "P1D");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Query_InvalidTimespan_Returns400()
    {
        using var factory = new FakeAppInsightsFactory();
        using var client = factory.CreateAuthorizedClient();

        var response = await PostQuery(client, "demo-bank-web", "requests | count", "yesterday");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("BadArgumentError", (await Json(response)).GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(factory.Kusto.Queries);
    }

    [Fact]
    public async Task Query_EngineUnreachable_Returns503()
    {
        using var factory = new FakeAppInsightsFactory();
        factory.Kusto.Throw = new KustoUnavailableException("connection refused");
        using var client = factory.CreateAuthorizedClient();

        var response = await PostQuery(client, "demo-bank-web", "requests | count");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Apps_ListsRegisteredAndUnregisteredDatabases()
    {
        using var factory = new FakeAppInsightsFactory();
        factory.Kusto.Databases.Add("retail-search");
        using var client = factory.CreateAuthorizedClient();
        using var anonymous = factory.CreateClient();

        var response = await client.GetAsync("/v1/apps");
        var denied = await anonymous.GetAsync("/v1/apps");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        var apps = (await Json(response)).EnumerateArray()
            .Select(a => $"{a.GetProperty("appId").GetString()}={a.GetProperty("database").GetString()}").ToList();
        Assert.Equal(["mobile-app-1=bank-mobile", "demo-bank-web=bank-web", "retail-search=retail-search"], apps);
    }
}
