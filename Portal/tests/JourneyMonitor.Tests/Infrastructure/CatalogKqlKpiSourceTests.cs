using System.Net;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring.Kpi;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Functions.Functions;
using JourneyMonitor.Infrastructure.AppInsights;
using JourneyMonitor.Infrastructure.DependencyInjection;
using JourneyMonitor.Infrastructure.Monitoring;
using JourneyMonitor.Tests.Functions;
using JourneyMonitor.Tests.Spec1000.A2;
using JourneyMonitor.Tests.Spec1000.A4;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Tests.Infrastructure;

/// <summary>Contract v4 §5 (AG3): KPIs backed by an audited catalog query, <c>kql:{queryKey}</c>.</summary>
public sealed class CatalogKqlKpiSourceTests
{
    private const string QueryKey = "kpi:hotels-purchases";
    private const string SourceKey = "kql:" + QueryKey;
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 12, 34, 56, TimeSpan.Zero);

    private const string ValidQuery = """
        // Purchases per bucket.
        let fromTs = {{from}};
        let toTs = {{to}};
        let bucketSize = {{bucket}};
        let samples = {{perBucket}};
        let stageFilter = {{stage}};
        app('{{SOURCE:web-front}}').requests
        | where timestamp >= fromTs and timestamp < toTs and name has "PurchaseComplete"
        | summarize TotalCount = sum(itemCount) by BucketStart = bin_at(timestamp, bucketSize, fromTs)
        | project ['kind'] = "bucket", BucketStart, TotalCount, Value = todouble(TotalCount), Grain = "{{grain}}"
        """;

    private static readonly TelemetryJourneyRow[] BucketRows =
    [
        KpiSourceDoubles.Row("""{"kind":"bucket","BucketStart":"2026-11-04T14:00:00Z","TotalCount":42,"Value":42.0}"""),
        KpiSourceDoubles.Row("""{"kind":"bucket","BucketStart":"2026-11-04T15:00:00Z","TotalCount":7,"Value":null}"""),
    ];

    private static KpiDefinition Definition(string sourceKey = SourceKey, KpiGrain grain = KpiGrain.Domain, string stage = "") =>
        KpiSourceDoubles.Definition(sourceKey: sourceKey, grain: grain, stage: stage);

    private static async Task<InMemoryKqlQueryStore> CatalogAsync(string? text = ValidQuery, bool enabled = true)
    {
        var store = new InMemoryKqlQueryStore();
        if (text is not null)
        {
            await store.UpsertAsync(new KqlQueryEntry(QueryKey, text, Description: "Hotels purchases", Enabled: enabled), "test");
        }
        return store;
    }

    private static CatalogKqlKpiSource Source(ITelemetrySource telemetry, IKqlQueryStore queries, KpiSourceDoubles.AliasResolver? resolver = null) =>
        new(
            telemetry,
            queries,
            new InMemoryCompanyProfileStore([KpiSourceDoubles.Incumbent(), KpiSourceDoubles.HostScoped()]),
            resolver ?? new KpiSourceDoubles.AliasResolver(),
            Options.Create(new JourneySyncOptions()),
            KpiSourceDoubles.Source(telemetry, KpiSourceDoubles.Incumbent(), KpiSourceDoubles.HostScoped()));

    [Fact]
    public async Task KqlKpi_UsesCatalogQuery()
    {
        var telemetry = new KpiSourceDoubles.CapturingTelemetrySource(BucketRows);
        var resolver = new KpiSourceDoubles.AliasResolver();
        var source = Source(telemetry, await CatalogAsync(), resolver);

        Assert.True(source.CanHandle(SourceKey));
        Assert.True(source.CanHandle("KQL:kpi:other"));
        // Only custom kpi: catalog keys: a KPI never runs a built-in query.
        Assert.False(source.CanHandle("kql:scout:drilldown"));
        Assert.False(source.CanHandle("kql:kpi:Upper"));
        Assert.False(source.CanHandle("kql:"));
        Assert.False(source.CanHandle(AppInsightsKpiSource.LatencySourceKey));

        var result = await source.FetchAsync(Definition(stage: "checkout"), KpiSourceDoubles.From, KpiSourceDoubles.To, 5);

        var request = Assert.Single(telemetry.Requests);
        // The catalog text, with every token filled and the alias resolved; the target is the hotels service's.
        Assert.Contains("name has \"PurchaseComplete\"", request.Kql);
        Assert.Contains("let fromTs = datetime(2026-11-04T14:00:00Z);", request.Kql);
        Assert.Contains("let toTs = datetime(2026-11-05T14:00:00Z);", request.Kql);
        Assert.Contains("let bucketSize = 1h;", request.Kql);
        Assert.Contains("let samples = 5;", request.Kql);
        Assert.Contains("let stageFilter = \"checkout\";", request.Kql);
        Assert.Contains("Grain = \"domain\"", request.Kql);
        Assert.Contains("app('/res/web-front')", request.Kql);
        Assert.DoesNotContain("{{", request.Kql);
        Assert.Equal("hotel-app", request.AppInsightsAppId);
        Assert.Single(resolver.Resolved);

        Assert.Equal(2, result.Buckets.Count);
        Assert.Equal(42, result.Buckets[0].TotalCount);
        Assert.Equal(42.0, result.Buckets[0].Value);
        Assert.Null(result.Buckets[1].Value);
        Assert.Empty(result.Samples);

        // A missing or disabled catalog query never reaches App Insights.
        var missing = Source(telemetry, await CatalogAsync(text: null));
        var notFound = await Assert.ThrowsAsync<KpiSourceUnavailableException>(() =>
            missing.FetchAsync(Definition(), KpiSourceDoubles.From, KpiSourceDoubles.To, 0));
        Assert.Contains($"query '{QueryKey}' does not exist in the KQL query catalog", notFound.Message);

        var disabled = Source(telemetry, await CatalogAsync(enabled: false));
        var off = await Assert.ThrowsAsync<KpiSourceUnavailableException>(() =>
            disabled.FetchAsync(Definition(), KpiSourceDoubles.From, KpiSourceDoubles.To, 0));
        Assert.Contains("is disabled", off.Message);
        Assert.Single(telemetry.Requests);

        // A domain that is not a service of the profile is unavailable, as for the App Insights KPIs.
        var foreign = await Assert.ThrowsAsync<KpiSourceUnavailableException>(() =>
            source.FetchAsync(KpiSourceDoubles.Definition(sourceKey: SourceKey, domain: "payments"), KpiSourceDoubles.From, KpiSourceDoubles.To, 0));
        Assert.Contains("domain is not a service of the profile", foreign.Message);
    }

    [Theory]
    [InlineData("{{domain}}")]
    [InlineData("{{maxEntities}}")]
    [InlineData("{{SOURCE_UNION}}")]
    [InlineData("{{HOST_FILTER}}")]
    [InlineData("{{From}}")]
    [InlineData("{{TIME_START}}")]
    [InlineData("{{ evil }}")]
    [InlineData("{{unterminated")]
    public async Task KqlKpi_RejectsUnknownTokens(string token)
    {
        var telemetry = new KpiSourceDoubles.CapturingTelemetrySource(BucketRows);
        var source = Source(telemetry, await CatalogAsync(ValidQuery + "\n| extend X = " + token));

        var ex = await Assert.ThrowsAsync<KpiSourceUnavailableException>(() =>
            source.FetchAsync(Definition(), KpiSourceDoubles.From, KpiSourceDoubles.To, 0));

        Assert.Contains("uses tokens that a KPI query may not use", ex.Message);
        Assert.Contains(token.StartsWith("{{unterminated", StringComparison.Ordinal) ? "{{" : token, ex.Message);
        Assert.Contains("{{from}}, {{to}}, {{bucket}}, {{perBucket}}, {{stage}}, {{grain}} and {{SOURCE:alias}}", ex.Message);
        Assert.Empty(telemetry.Requests);

        // The allowed set (and SOURCE aliases) alone is accepted.
        Assert.Empty(KqlKpiContract.FindDisallowedTokens(ValidQuery));
        Assert.Equal(["{{domain}}", "{{x}}"], KqlKpiContract.FindDisallowedTokens("{{from}} {{domain}} {{x}} {{domain}} {{SOURCE:a.b-c}}"));
    }

    [Fact]
    public async Task KqlKpi_DryRunFailure_Returns400()
    {
        var definitions = new A4FakeKpiDefinitionStore();

        // Query not in the catalog.
        var (status, errors) = await PostAsync(definitions, new KpiSourceDoubles.CapturingTelemetrySource(BucketRows), await CatalogAsync(text: null));
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains(errors, e => e.StartsWith($"SourceKey '{SourceKey}':", StringComparison.Ordinal) && e.Contains("does not exist in the KQL query catalog"));

        // Token outside the allowed set.
        (status, errors) = await PostAsync(definitions, new KpiSourceDoubles.CapturingTelemetrySource(BucketRows), await CatalogAsync(ValidQuery + " {{domain}}"));
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains(errors, e => e.Contains("{{domain}}"));

        // The query fails in App Insights.
        (status, errors) = await PostAsync(definitions, new ThrowingTelemetrySource(new HttpRequestException("Syntax error: 'summarize' expected")), await CatalogAsync());
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains(errors, e => e.Contains("the one-hour dry run failed: Syntax error: 'summarize' expected"));

        // The query does not finish within the bounded timeout.
        (status, errors) = await PostAsync(definitions, new HangingTelemetrySource(), await CatalogAsync(), TimeSpan.FromMilliseconds(50));
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains(errors, e => e.Contains("did not finish within"));

        // The output breaks the contract.
        (status, errors) = await PostAsync(definitions,
            new KpiSourceDoubles.CapturingTelemetrySource([KpiSourceDoubles.Row("""{"BucketStart":"2026-11-05T11:00:00Z","Purchases":3}""")]), await CatalogAsync());
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains(errors, e => e.Contains("does not match the KPI output contract"));

        // No rows prove nothing about the contract.
        (status, errors) = await PostAsync(definitions, new KpiSourceDoubles.CapturingTelemetrySource([]), await CatalogAsync());
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains(errors, e => e.EndsWith(
            "Dry run returned no rows; cannot validate the output contract — widen the window or check the query.", StringComparison.Ordinal));
        Assert.Empty(definitions.Upserts);

        // A valid query is saved after one dry run over the last whole hour.
        var telemetry = new KpiSourceDoubles.CapturingTelemetrySource(BucketRows);
        (status, errors) = await PostAsync(definitions, telemetry, await CatalogAsync());
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Empty(errors);
        Assert.Equal(SourceKey, Assert.Single(definitions.Upserts).Definition.SourceKey);
        var request = Assert.Single(telemetry.Requests);
        Assert.Contains("let fromTs = datetime(2026-11-05T11:34:00Z);", request.Kql);
        Assert.Contains("let toTs = datetime(2026-11-05T12:34:00Z);", request.Kql);

        // Without a dry run the API fails closed for kql: keys.
        var noDryRun = Admin(definitions, null);
        var response = await noDryRun.UpsertAsync(
            A4Http.Request("http://localhost/api/settings/kpis", Body(), writeSecret: A4Http.WriteSecret), CancellationToken.None);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Single(definitions.Upserts);
    }

    [Fact]
    public async Task KqlKpi_OutputContractValidated()
    {
        Assert.Null(KqlKpiContract.ValidateOutput(BucketRows, KpiGrain.Domain));
        Assert.Null(KqlKpiContract.ValidateOutput([], KpiGrain.Domain));
        Assert.Null(KqlKpiContract.ValidateOutput(
            [
                KpiSourceDoubles.Row("""{"kind":"bucket","BucketStart":"2026-11-04T14:00:00Z","TotalCount":"3","Value":1.5,"EntityKey":""}"""),
                KpiSourceDoubles.Row("""{"kind":"sample","BucketStart":"2026-11-04T14:00:00Z","Timestamp":"2026-11-04T14:01:00Z","Value":812,"OperationId":"op"}"""),
            ], KpiGrain.Domain));

        (string Json, string Expected)[] invalid =
        [
            ("""{"BucketStart":"2026-11-04T14:00:00Z","TotalCount":1}""", "column 'kind' must be"),
            ("""{"kind":"total","BucketStart":"2026-11-04T14:00:00Z","TotalCount":1}""", "column 'kind' must be"),
            ("""{"kind":"bucket","BucketStart":"yesterday","TotalCount":1}""", "'BucketStart' must be a datetime"),
            ("""{"kind":"bucket","BucketStart":"2026-11-04T14:00:00Z"}""", "numeric 'TotalCount'"),
            ("""{"kind":"bucket","BucketStart":"2026-11-04T14:00:00Z","TotalCount":null}""", "numeric 'TotalCount'"),
            ("""{"kind":"bucket","BucketStart":"2026-11-04T14:00:00Z","TotalCount":1,"Value":"high"}""", "'Value' must be a number or null"),
            ("""{"kind":"sample","BucketStart":"2026-11-04T14:00:00Z"}""", "numeric 'Value'"),
            ("""{"kind":"bucket","BucketStart":"2026-11-04T14:00:00Z","TotalCount":1,"EntityKey":"club-1"}""", "must not return 'EntityKey'"),
        ];
        foreach (var (json, expected) in invalid)
        {
            var error = KqlKpiContract.ValidateOutput([KpiSourceDoubles.Row(json)], KpiGrain.Domain);
            Assert.NotNull(error);
            Assert.Contains(expected, error);
        }

        var duplicate = KqlKpiContract.ValidateOutput([BucketRows[0], BucketRows[0]], KpiGrain.Domain);
        Assert.Contains("more than one \"bucket\" row", duplicate);

        // The source turns a broken output into "unavailable" with the message, never into buckets.
        var source = Source(new KpiSourceDoubles.CapturingTelemetrySource([KpiSourceDoubles.Row("""{"BucketStart":"2026-11-04T14:00:00Z","Count":1}""")]), await CatalogAsync());
        var ex = await Assert.ThrowsAsync<KpiSourceUnavailableException>(() =>
            source.FetchAsync(Definition(), KpiSourceDoubles.From, KpiSourceDoubles.To, 0));
        Assert.Contains($"query '{QueryKey}': the query output does not match the KPI output contract: row 1: column 'kind' must be", ex.Message);
    }

    [Fact]
    public async Task KqlKpi_EntityGrainSupported()
    {
        var rows = new[]
        {
            KpiSourceDoubles.Row("""{"kind":"bucket","BucketStart":"2026-11-04T14:00:00Z","EntityKey":"club-1","TotalCount":10,"Value":250.0,"P50":120.0,"P95":250.0,"Mean":140.0,"FailedCount":2}"""),
            KpiSourceDoubles.Row("""{"kind":"bucket","BucketStart":"2026-11-04T14:00:00Z","EntityKey":"club-2","TotalCount":30,"Value":90.0,"P50":null,"P95":null,"Mean":null,"FailedCount":null}"""),
        };
        var telemetry = new KpiSourceDoubles.CapturingTelemetrySource(rows);
        var source = Source(telemetry, await CatalogAsync());

        var result = await source.FetchAsync(Definition(grain: KpiGrain.Entity), KpiSourceDoubles.From, KpiSourceDoubles.To, 0);

        Assert.Contains("Grain = \"entity\"", Assert.Single(telemetry.Requests).Kql);
        Assert.Empty(result.Samples);
        Assert.Equal(["club-2", "club-1"], result.Buckets.Select(b => b.EntityKey));
        var club1 = result.Buckets.Single(b => b.EntityKey == "club-1");
        Assert.Equal(10, club1.TotalCount);
        Assert.Equal(120.0, club1.P50);
        Assert.Equal(250.0, club1.P95);
        Assert.Equal(140.0, club1.Mean);
        Assert.Equal(2, club1.FailedCount);

        // Entity grain needs an EntityKey on every bucket row, one row per entity and bucket, and no samples.
        Assert.Contains("needs an 'EntityKey'", KqlKpiContract.ValidateOutput(BucketRows, KpiGrain.Entity));
        Assert.Contains("more than one \"bucket\" row for entity 'club-1'", KqlKpiContract.ValidateOutput([rows[0], rows[0]], KpiGrain.Entity));
        Assert.Contains("returns no \"sample\" rows", KqlKpiContract.ValidateOutput(
            [KpiSourceDoubles.Row("""{"kind":"sample","BucketStart":"2026-11-04T14:00:00Z","Value":1}""")], KpiGrain.Entity));
        Assert.Contains("'P95' must be a number or null", KqlKpiContract.ValidateOutput(
            [KpiSourceDoubles.Row("""{"kind":"bucket","BucketStart":"2026-11-04T14:00:00Z","EntityKey":"c","TotalCount":1,"P95":"slow"}""")], KpiGrain.Entity));
    }

    [Fact]
    public async Task KqlKpi_ForeignProfileAlias_Rejected()
    {
        // ValidQuery reads app('{{SOURCE:web-front}}'), a source of the incumbent only.
        var telemetry = new KpiSourceDoubles.CapturingTelemetrySource(BucketRows);
        var source = Source(telemetry, await CatalogAsync());

        var ex = await Assert.ThrowsAsync<KpiSourceUnavailableException>(() =>
            source.FetchAsync(Definition() with { ProfileKey = "acme" }, KpiSourceDoubles.From, KpiSourceDoubles.To, 0));
        Assert.Contains("Alias 'web-front' is not a telemetry source of profile 'acme'.", ex.Message);

        // The reverse direction too: the incumbent cannot read acme's source.
        var reverse = Source(telemetry, await CatalogAsync(ValidQuery.Replace("web-front", "acme-web")));
        ex = await Assert.ThrowsAsync<KpiSourceUnavailableException>(() =>
            reverse.FetchAsync(Definition(), KpiSourceDoubles.From, KpiSourceDoubles.To, 0));
        Assert.Contains("Alias 'acme-web' is not a telemetry source of profile 'arrivia'.", ex.Message);
        Assert.Empty(telemetry.Requests);

        // Save: 400 with the message, nothing stored, nothing queried.
        var definitions = new A4FakeKpiDefinitionStore();
        var (status, errors) = await PostAsync(definitions, telemetry, await CatalogAsync(), profileKey: "acme");
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains(errors, e => e.Contains("Alias 'web-front' is not a telemetry source of profile 'acme'."));
        Assert.Empty(definitions.Upserts);
        Assert.Empty(telemetry.Requests);
    }

    [Fact]
    public async Task KqlKpi_OwnAlias_Allowed()
    {
        var telemetry = new KpiSourceDoubles.CapturingTelemetrySource(BucketRows);
        var acmeQuery = ValidQuery.Replace("web-front", "acme-web");
        var source = Source(telemetry, await CatalogAsync(acmeQuery));

        var result = await source.FetchAsync(Definition() with { ProfileKey = "acme" }, KpiSourceDoubles.From, KpiSourceDoubles.To, 0);

        Assert.Equal(2, result.Buckets.Count);
        var request = Assert.Single(telemetry.Requests);
        Assert.Contains("app('/res/acme-web')", request.Kql);
        Assert.Equal("acme-app", request.AppInsightsAppId);

        // Any service of the KPI's own profile counts, not only the KPI's service (a cruises source for a hotels KPI).
        var crossService = Source(telemetry, await CatalogAsync(ValidQuery.Replace("web-front", "SEA-CORE")));
        await crossService.FetchAsync(Definition(), KpiSourceDoubles.From, KpiSourceDoubles.To, 0);
        Assert.Contains("app('/res/SEA-CORE')", telemetry.Requests[1].Kql);

        var definitions = new A4FakeKpiDefinitionStore();
        var (status, errors) = await PostAsync(definitions, telemetry, await CatalogAsync(acmeQuery), profileKey: "acme");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Empty(errors);
        Assert.Equal("acme", Assert.Single(definitions.Upserts).Definition.ProfileKey);
    }

    [Theory]
    [InlineData("app(@'8644ddf0-0000-0000-0000-000000000000').requests", "app")]
    [InlineData("workspace('00000000-0000-0000-0000-000000000000').AppRequests", "workspace")]
    [InlineData("app ('x').requests", "app")]
    [InlineData("app(h'x').requests", "app")]
    [InlineData("APP(\"x\").requests", "app")]
    [InlineData("resource('/subscriptions/0000/resourceGroups/rg/providers/microsoft.insights/components/c').requests", "resource")]
    [InlineData("cluster('help').database('Samples').StormEvents", "cluster")]
    [InlineData("union requests, app\n  // comment\n  ('x').requests", "app")]
    [InlineData("app(\"{{SOURCE:web-front}}\" ).requests | union app('{{SOURCE:web-front}}-x').requests", "app")]
    [InlineData("externaldata (x:string) [@'https://example.com/x.csv']", "externaldata")]
    [InlineData("evaluate http_request('https://example.com')", "http_request")]
    [InlineData("evaluate sql_request('Server=tcp:x', 'select 1')", "sql_request")]
    [InlineData("adx('https://help.kusto.windows.net/Samples').StormEvents", "adx")]
    [InlineData("external_table('t')", "external_table")]
    public async Task KqlKpi_CrossResourceCallsRejected(string body, string call)
    {
        var query = "let fromTs = {{from}};\n" + body + "\n| project ['kind'] = \"bucket\", BucketStart = fromTs, TotalCount = 1";
        Assert.Contains(call, KqlKpiContract.FindCrossResourceCalls(query));

        var telemetry = new KpiSourceDoubles.CapturingTelemetrySource(BucketRows);
        var source = Source(telemetry, await CatalogAsync(query));
        var ex = await Assert.ThrowsAsync<KpiSourceUnavailableException>(() =>
            source.FetchAsync(Definition(), KpiSourceDoubles.From, KpiSourceDoubles.To, 0));
        Assert.Contains($"A KPI query may not call {call}()", ex.Message);
        Assert.Empty(telemetry.Requests);
    }

    [Fact]
    public void KqlKpi_LegitQueriesAreNotCrossResource()
    {
        Assert.Empty(KqlKpiContract.FindCrossResourceCalls(ValidQuery));
        Assert.Empty(KqlKpiContract.FindCrossResourceCalls("""
            // app('not a call: this is a comment')
            app("{{SOURCE:web-front}}").requests
            | union app ( '{{SOURCE:sea-core}}' ).requests
            | extend application = "application('x') and app('y') in a string", Note = @'workspace(''w'')', H = h"cluster('c')"
            | extend Fenced = ```resource('r')```
            | where application_Version != "" and cloud_RoleName !has "app("
            | extend appName = tostring(customDimensions.app), myapp = strcat("a", "b")
            | summarize TotalCount = count() by BucketStart = bin(timestamp, 1h), application
            """));
        Assert.Equal(["app"], KqlKpiContract.FindCrossResourceCalls("print x = 'it''s fine', y = \"esc\\\" app('x')\" | union app('z').t"));
    }

    [Fact]
    public async Task KqlKpi_OnlyCustomKpiKeys()
    {
        var definitions = new A4FakeKpiDefinitionStore();
        var (status, errors) = await PostAsync(definitions, new KpiSourceDoubles.CapturingTelemetrySource(BucketRows), await CatalogAsync(),
            sourceKey: "kql:scout:drilldown");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("SourceKey 'kql:scout:drilldown' must reference a custom catalog query: kql:kpi:{name} (lowercase [a-z0-9:._-]).", errors);
        Assert.Empty(definitions.Upserts);
        Assert.Null(KqlKpiContract.QueryKeyOf("kql:journey:hotels"));
        Assert.Equal("kpi:x", KqlKpiContract.QueryKeyOf(" kql:kpi:x "));
    }

    [Fact]
    public void KqlKpi_RegisteredNextToAppInsightsSource()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IDataProtectionProvider>(new PassThroughDataProtection());
        services.AddJourneyMonitorInfrastructure(configuration);
        services.AddFabricHotelBookingKpis();
        services.AddJourneyMonitorMonitoring(configuration);
        services.AddSpec1000(configuration);
        services.AddSqlJourneyStore(configuration);

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = provider.CreateScope();
        var sp = scope.ServiceProvider;

        var sources = sp.GetServices<IKpiSource>().ToList();
        Assert.IsType<AppInsightsKpiSource>(sources.Single(s => s.CanHandle(AppInsightsKpiSource.LatencySourceKey)));
        Assert.IsType<CatalogKqlKpiSource>(sources.Single(s => s.CanHandle(SourceKey)));
        Assert.NotNull(sp.GetRequiredService<KqlKpiDryRun>());
        // The KQL admin is rebuilt with the KPI usage check (edit dry run, delete conflict).
        Assert.NotNull(sp.GetRequiredService<KqlQueryUsage>());
        Assert.NotNull(sp.GetRequiredService<JourneyMonitor.Core.UseCases.KqlQueryAdminService>());
        Assert.Single(services, d => d.ServiceType == typeof(JourneyMonitor.Core.UseCases.KqlQueryAdminService));
        Assert.NotNull(ActivatorUtilities.CreateInstance<KpiDefinitionAdminFunctions>(sp));
    }

    private sealed class PassThroughDataProtection : IDataProtectionProvider, IDataProtector
    {
        public IDataProtector CreateProtector(string purpose) => this;

        public byte[] Protect(byte[] plaintext) => plaintext;

        public byte[] Unprotect(byte[] protectedData) => protectedData;
    }

    private static KpiDefinitionAdminFunctions Admin(A4FakeKpiDefinitionStore definitions, KqlKpiDryRun? dryRun)
    {
        var profiles = new InMemoryCompanyProfileStore([KpiSourceDoubles.Incumbent(), KpiSourceDoubles.HostScoped()]);
        return new KpiDefinitionAdminFunctions(
            definitions,
            Options.Create(new MonitoringOptions { InternalApiSecret = A4Http.InternalSecret, SettingsWriteSecret = A4Http.WriteSecret }),
            new A4FakeTimeProvider(Now),
            NullLogger<KpiDefinitionAdminFunctions>.Instance,
            new KpiProfileResolver(Options.Create(new ProfileDrivenCompanyOptions { DefaultProfileKey = KpiProfiles.Default }), null, profiles),
            profiles,
            dryRun);
    }

    private static async Task<(HttpStatusCode Status, string[] Errors)> PostAsync(
        A4FakeKpiDefinitionStore definitions, ITelemetrySource telemetry, IKqlQueryStore queries, TimeSpan? timeout = null,
        string profileKey = KpiProfiles.Default, string sourceKey = SourceKey)
    {
        var dryRun = new KqlKpiDryRun([KpiSourceDoubles.Source(telemetry, KpiSourceDoubles.Incumbent(), KpiSourceDoubles.HostScoped()), Source(telemetry, queries)], new A4FakeTimeProvider(Now))
        {
            Timeout = timeout ?? KqlKpiDryRun.DefaultTimeout,
        };
        var response = await Admin(definitions, dryRun).UpsertAsync(
            A4Http.Request("http://localhost/api/settings/kpis", Body(profileKey, sourceKey), writeSecret: A4Http.WriteSecret), CancellationToken.None);
        var errors = response.StatusCode == HttpStatusCode.BadRequest
            ? response.ReadJson()["errors"]!.AsArray().Select(e => e!.GetValue<string>()).ToArray()
            : [];
        return (response.StatusCode, errors);
    }

    private static string Body(string profileKey = KpiProfiles.Default, string sourceKey = SourceKey) => $$"""
        {"kpiDefinitionId":0,"pillar":"Business","kpi":"Purchases","domain":"hotels","stage":"","sourceKey":"{{sourceKey}}",
         "direction":"decrease","aggregation":"count","evaluationFrequency":"00:05:00","bucketSize":"01:00:00","lookbackWindow":"1.00:00:00",
         "sampleSize":0,"minObservationsPerBucket":0,"detectionMethod":"robustZScore",
         "methodParamsJson":"{\"windowBuckets\":24,\"zThreshold\":3}","consecutiveCount":2,"cooldownMinutes":120,
         "enabled":false,"changedBy":"ana@example.com","profileKey":"{{profileKey}}"}
        """;

    private sealed class ThrowingTelemetrySource(Exception error) : ITelemetrySource
    {
        public Task<IReadOnlyList<TelemetryJourneyRow>> QueryAsync(TelemetryQueryRequest request, CancellationToken cancellationToken) =>
            Task.FromException<IReadOnlyList<TelemetryJourneyRow>>(error);
    }

    private sealed class HangingTelemetrySource : ITelemetrySource
    {
        public async Task<IReadOnlyList<TelemetryJourneyRow>> QueryAsync(TelemetryQueryRequest request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return [];
        }
    }
}
