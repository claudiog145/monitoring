using FakeAppInsights.Rewrite;

namespace FakeAppInsights.Tests;

public class AppReferenceRewriterTests
{
    private static readonly StaticApplicationCatalog Catalog = new(
    [
        ("demo-bank-web-banking", "bank-web-banking"),
        ("demo-bank-core-api", "bank-core-api"),
        ("6a1f0c2e-0000-4000-8000-00000000demo", "tourism-cruise-core"),
    ]);

    private static readonly DateTimeOffset From = new(2026, 11, 7, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = new(2026, 11, 7, 16, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AppRewriter_ArmIdBecomesDatabase()
    {
        const string arm = "/subscriptions/00000000-0000-0000-0000-00000000demo/resourceGroups/rg-demo/providers/microsoft.insights/components/bank-web-banking";
        var kql = $"app('{arm}').requests | where success == false | count";

        var rewritten = AppReferenceRewriter.Rewrite(kql, Catalog);

        Assert.Equal("database('bank-web-banking').requests | where success == false | count", rewritten);
    }

    [Fact]
    public void AppRewriter_ArmIdIsCaseInsensitiveAndAcceptsDoubleQuotes()
    {
        const string arm = "/subscriptions/x/resourcegroups/y/providers/Microsoft.Insights/components/bank-core-api";
        var kql = $"union isfuzzy=true app(\"{arm}\").exceptions, app('bank-web-banking').exceptions | count";

        var rewritten = AppReferenceRewriter.Rewrite(kql, Catalog);

        Assert.Equal("union isfuzzy=true database('bank-core-api').exceptions, database('bank-web-banking').exceptions | count", rewritten);
    }

    [Fact]
    public void AppRewriter_NameAndAppIdResolve()
    {
        var byName = AppReferenceRewriter.Rewrite("app('bank-web-banking').requests | take 1", Catalog);
        var byAppId = AppReferenceRewriter.Rewrite("app('demo-bank-core-api').requests | take 1", Catalog);
        var byGuid = AppReferenceRewriter.Rewrite("app('6a1f0c2e-0000-4000-8000-00000000demo').traces | take 1", Catalog);

        Assert.Equal("database('bank-web-banking').requests | take 1", byName);
        Assert.Equal("database('bank-core-api').requests | take 1", byAppId);
        Assert.Equal("database('tourism-cruise-core').traces | take 1", byGuid);
    }

    [Fact]
    public void AppRewriter_TablesWithoutPrefixUntouched()
    {
        const string kql = """
            let window = 1h;
            // app('not-a-call') inside a comment stays as it is
            requests
            | where timestamp > ago(window) and name != "app('bank-web-banking')"
            | join kind=leftouter (exceptions | project operation_Id, type) on operation_Id
            | summarize count() by name
            """;

        Assert.Equal(kql, AppReferenceRewriter.Rewrite(kql, Catalog));
    }

    [Fact]
    public void AppRewriter_WorkspaceRejected()
    {
        Assert.Throws<UnsupportedKqlException>(() =>
            AppReferenceRewriter.Rewrite("workspace('law-demo').AppRequests | take 1", Catalog));
        Assert.Throws<UnsupportedKqlException>(() =>
            AppReferenceRewriter.Rewrite("union app('bank-web-banking').requests, workspace('x').AppRequests", Catalog));
    }

    [Fact]
    public void AppRewriter_UnknownAppRejected()
    {
        var ex = Assert.Throws<UnknownApplicationException>(() =>
            AppReferenceRewriter.Rewrite("app('cruise-core-ovs-ai-prod').requests | take 1", Catalog));

        Assert.Equal("Unknown application 'cruise-core-ovs-ai-prod'.", ex.Message);
        Assert.Throws<UnknownApplicationException>(() => AppReferenceRewriter.Rewrite(
            "app('/subscriptions/s/resourceGroups/r/providers/microsoft.insights/components/nope').requests", Catalog));
    }

    [Fact]
    public void Timespan_FiltersAllSixTables()
    {
        const string kql = """
            let requests_count = 5;
            union requests, exceptions, dependencies, traces, pageViews, customEvents
            | union (database('bank-web-banking').requests | extend requests = 1)
            | summarize c = count() by requests_count
            """;

        var filtered = TimespanFilter.Apply(kql, From, To);

        var predicate = "| where timestamp between (datetime(2026-11-07T10:00:00.0000000Z) .. datetime(2026-11-07T16:00:00.0000000Z)))";
        foreach (var table in AppInsightsSchema.TableNames)
        {
            Assert.Contains($"({table} {predicate}", filtered);
        }
        Assert.Contains($"(database('bank-web-banking').requests {predicate}", filtered);
        Assert.Contains("extend requests = 1", filtered);
        Assert.Contains("let requests_count = 5;", filtered);
        Assert.Contains("by requests_count", filtered);
        Assert.Equal(7, CountOccurrences(filtered, "timestamp between"));
    }

    [Fact]
    public void Timespan_LetVariableShadowingATableIsNotFiltered()
    {
        const string kql = "let traces = datatable(timestamp:datetime)[]; traces | count";

        Assert.Equal(kql, TimespanFilter.Apply(kql, From, To));
    }

    [Fact]
    public void Timespan_ParsesIsoFormats()
    {
        var now = new DateTimeOffset(2026, 11, 7, 16, 0, 0, TimeSpan.Zero);

        Assert.Equal(new TimeRange(now.AddHours(-6), now), TimespanParser.Parse("PT6H", now));
        Assert.Equal(new TimeRange(now.AddDays(-1), now), TimespanParser.Parse("P1D", now));
        Assert.Equal(new TimeRange(From, To), TimespanParser.Parse("2026-11-07T10:00:00Z/2026-11-07T16:00:00Z", now));
        Assert.Equal(new TimeRange(From, From.AddMinutes(90)), TimespanParser.Parse("2026-11-07T10:00:00Z/PT1H30M", now));
        Assert.Equal(new TimeRange(To.AddHours(-2), To), TimespanParser.Parse("PT2H/2026-11-07T16:00:00Z", now));
        Assert.Throws<FormatException>(() => TimespanParser.Parse("yesterday", now));
        Assert.Throws<FormatException>(() => TimespanParser.Parse("2026-11-07T16:00:00Z/2026-11-07T10:00:00Z", now));
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal)) count++;
        return count;
    }
}
