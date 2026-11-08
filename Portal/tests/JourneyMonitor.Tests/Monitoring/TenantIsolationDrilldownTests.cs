using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Configuration;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring;
using JourneyMonitor.Core.Monitoring.Inspector;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Core.UseCases;
using JourneyMonitor.Infrastructure.Kql;
using JourneyMonitor.Infrastructure.Monitoring;
using JourneyMonitor.Tests.Company;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static JourneyMonitor.Tests.Spec1000.A2.KpiSourceDoubles;

namespace JourneyMonitor.Tests.Monitoring;

/// <summary>
/// FX4: the Scout drill-down and the Inspector's log tools read only the telemetry of the profile
/// the package/case belongs to. The incumbent's queries stay byte-identical; any other profile reads
/// its own aliases that pass the ownership check, and a profile with none is not queried at all.
/// </summary>
public sealed class TenantIsolationDrilldownTests
{
    private static readonly DateTimeOffset WindowFrom = new(2026, 11, 7, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset WindowTo = new(2026, 11, 7, 9, 5, 0, TimeSpan.Zero);

    private static readonly string[] IncumbentDrilldownAliases = ["sor-frontend", "cruise-core", "cruise-api"];

    private static TelemetrySourceEntry Entry(string alias, string? owner) =>
        new(alias, InsightsEnvironment.Production, TelemetrySourceKind.AppInsights, "/res/" + alias, OwnerProfileKey: owner);

    /// <summary>acme owns acme-web; acme-billing was transferred to globex; the incumbent owns its fixed sources.</summary>
    private static CatalogStore Catalog() => new(
    [
        Entry("acme-web", "acme"),
        Entry("acme-billing", "globex"),
        Entry("sor-frontend", KpiProfiles.Default),
        Entry("cruise-core", KpiProfiles.Default),
        Entry("cruise-api", KpiProfiles.Default),
    ]);

    private static ProfileTelemetrySourceResolver Resolver(ITelemetrySourceStore catalog, params CompanyProfile[] profiles) =>
        new(new InMemoryCompanyProfileStore(profiles), new TelemetrySourceOwnershipCheck(catalog));

    private static ScoutEvidenceCollector Collector(
        ITelemetrySource telemetry,
        ICompanyAdapterProvider? adapters = null,
        ProfileTelemetrySourceResolver? resolver = null) =>
        new(
            telemetry,
            new KqlQueryTextProvider(),
            new AliasResolver(),
            Options.Create(new InsightsOptions { AppInsightsAppId = "app-id" }),
            Options.Create(new ScoutOptions()),
            NullLogger<ScoutEvidenceCollector>.Instance,
            adapters,
            resolver);

    private static ScoutBoardRow BoardRow() => new(
        ScoutBoard.Exception,
        "SOR | Hotels | host:rooms.acme.test | 10 Browse | 2026-11-07T09:00:00Z",
        "SOR | Hotels | host:rooms.acme.test | 10 Browse",
        "Hotels",
        "host:rooms.acme.test",
        "10 Browse",
        "SOR",
        "Investigate now",
        3,
        "At least 100 exceptions",
        WindowFrom,
        WindowTo,
        """{"Ops":["op-1","op-2"]}""",
        "{}");

    [Fact]
    public async Task Drilldown_NonIncumbentCase_UsesOnlyOwnSources()
    {
        var telemetry = new CapturingTelemetrySource();
        var acme = HostScoped("acme");
        var collector = Collector(telemetry, new FakeCompanyAdapterProvider(acme), Resolver(Catalog(), acme));

        var evidence = await collector.CollectAsync(BoardRow());

        Assert.Null(evidence.Error);
        var kql = Assert.Single(telemetry.Requests).Kql;
        Assert.Equal(kql, evidence.Kql);
        // Its own source on both unions (requests and exceptions), labelled with the alias.
        Assert.Equal(2, CountOf(kql, "app(\"/res/acme-web\")"));
        Assert.Contains("Source=\"acme-web\"", kql, StringComparison.Ordinal);
        // Not the source transferred to another profile, and none of the incumbent's.
        Assert.DoesNotContain("acme-billing", kql, StringComparison.Ordinal);
        foreach (var alias in IncumbentDrilldownAliases)
        {
            Assert.DoesNotContain(alias, kql, StringComparison.Ordinal);
        }
        Assert.DoesNotContain("Source=\"SOR\"", kql, StringComparison.Ordinal);
        // The rest of the query is the drill-down's: same scoping by operation id and window.
        Assert.Contains("let opIds = dynamic([\"op-1\",\"op-2\"]);", kql, StringComparison.Ordinal);
        Assert.Contains("| where operation_Id in (opIds)", kql, StringComparison.Ordinal);
        Assert.Contains("union reqs, excs", kql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Drilldown_NonIncumbentWithoutSources_QueriesNothing()
    {
        var telemetry = new CapturingTelemetrySource();

        // A profile that declares no alias.
        var bare = HostScoped("acme") with { Services = [Service("hotels", [], ["rooms.acme.test"], ("10-browse", "10 Browse", 1, "/browse"))] };
        var noAlias = await Collector(telemetry, new FakeCompanyAdapterProvider(bare), Resolver(Catalog(), bare)).CollectAsync(BoardRow());
        Assert.Equal("No telemetry sources configured for profile 'acme'.", noAlias.Error);
        Assert.Equal("", noAlias.Kql);

        // A profile whose every alias belongs to another profile.
        var acme = HostScoped("acme");
        var allForeign = new CatalogStore([Entry("acme-web", "globex"), Entry("acme-billing", "globex")]);
        var foreign = await Collector(telemetry, new FakeCompanyAdapterProvider(acme), Resolver(allForeign, acme)).CollectAsync(BoardRow());
        Assert.Equal("No telemetry sources configured for profile 'acme'.", foreign.Error);

        // No resolver wired: a non-incumbent profile cannot be verified, so it is not queried either.
        var unverified = await Collector(telemetry, new FakeCompanyAdapterProvider(acme)).CollectAsync(BoardRow());
        Assert.Equal("No telemetry sources configured for profile 'acme'.", unverified.Error);

        // The Inspector's tools say the same and do not query.
        var package = Package("acme");
        foreach (var tool in Tools(telemetry, Resolver(Catalog(), bare)))
        {
            var result = await tool.ExecuteAsync("{}", Context(package));
            Assert.False(result.Success, tool.Name);
            Assert.Equal("No telemetry sources configured for profile 'acme'.", result.Error);
        }

        Assert.Empty(telemetry.Requests);
    }

    [Fact]
    public async Task InspectorLogs_NonIncumbentCase_UsesOnlyOwnSources()
    {
        var acme = HostScoped("acme");
        var telemetry = new CapturingTelemetrySource();
        var package = Package("acme");

        foreach (var tool in Tools(telemetry, Resolver(Catalog(), acme)))
        {
            var before = telemetry.Requests.Count;
            var result = await tool.ExecuteAsync("{}", Context(package));

            Assert.True(result.Success, $"{tool.Name}: {result.Error}");
            Assert.Equal(before + 1, telemetry.Requests.Count);
            var kql = telemetry.Requests[^1].Kql;
            Assert.Contains("app(\"/res/acme-web\")", kql, StringComparison.Ordinal);
            Assert.DoesNotContain("acme-billing", kql, StringComparison.Ordinal);
            foreach (var alias in IncumbentDrilldownAliases)
            {
                Assert.DoesNotContain(alias, kql, StringComparison.Ordinal);
            }
            Assert.DoesNotContain("{{SOURCE:", kql, StringComparison.Ordinal);
        }

        // The case's own ProfileKey wins over the package's (it is copied from it; a blank one falls back).
        var caseOnly = Context(Package(KpiProfiles.Default)) with
        {
            Case = new ForensicCase
            {
                InvestigationPackageId = package.Id,
                ProfileKey = "acme",
                CaseId = package.CaseId,
                EntityKey = package.EntityKey,
                Vertical = package.Vertical,
                Stage = package.Stage,
            },
        };
        var drill = Tools(telemetry, Resolver(Catalog(), acme)).First(t => t.Name == "drill_down_events");
        Assert.True((await drill.ExecuteAsync("{}", caseOnly)).Success);
        Assert.DoesNotContain("sor-frontend", telemetry.Requests[^1].Kql, StringComparison.Ordinal);
        Assert.Contains("/res/acme-web", telemetry.Requests[^1].Kql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Incumbent_DrilldownAndLogs_Unchanged()
    {
        var provider = new KqlQueryTextProvider();
        var resolverWithoutStore = new AliasResolver();
        var row = BoardRow();

        // The drill-down: byte-identical to the legacy collector and to the template as written.
        var legacyTelemetry = new CapturingTelemetrySource();
        await Collector(legacyTelemetry).CollectAsync(row);
        var scopedTelemetry = new CapturingTelemetrySource();
        var incumbent = Incumbent();
        await Collector(scopedTelemetry, new FakeCompanyAdapterProvider(incumbent), Resolver(Catalog(), incumbent)).CollectAsync(row);

        var expected = await resolverWithoutStore.ResolveAsync(
            ScoutEvidenceCollector.BuildKql(provider.GetScoutDrillDownKql(), row, ["op-1", "op-2"], new ScoutOptions().ResolvedMaxRawEvidenceRows),
            InsightsEnvironment.Production);
        Assert.Equal(expected, Assert.Single(legacyTelemetry.Requests).Kql);
        Assert.Equal(expected, Assert.Single(scopedTelemetry.Requests).Kql);
        foreach (var alias in IncumbentDrilldownAliases)
        {
            Assert.Contains("/res/" + alias, expected, StringComparison.Ordinal);
        }

        // The Inspector's tools: the same text with and without the FX4 resolver, for an incumbent case
        // and for a pre-multi-company case that carries no profile at all.
        foreach (var profileKey in new[] { KpiProfiles.Default, "ARRIVIA", "" })
        {
            var package = Package(profileKey);
            var legacy = new CapturingTelemetrySource();
            var scoped = new CapturingTelemetrySource();
            var legacyTools = Tools(legacy, null);
            var scopedTools = Tools(scoped, Resolver(Catalog(), incumbent));
            for (var i = 0; i < legacyTools.Length; i++)
            {
                Assert.True((await legacyTools[i].ExecuteAsync("{}", Context(package))).Success);
                Assert.True((await scopedTools[i].ExecuteAsync("{}", Context(package))).Success);
            }

            Assert.Equal(legacyTools.Length, scoped.Requests.Count);
            Assert.Equal(legacy.Requests.Select(r => r.Kql), scoped.Requests.Select(r => r.Kql));
            Assert.All(scoped.Requests, r => Assert.Contains("/res/sor-frontend", r.Kql, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Restrict_RefusesASourceOutsideAUnion()
    {
        // A query whose source is not in a rewritable union branch would still read the incumbent.
        const string kql = "let x = app(\"{{SOURCE:sor-frontend}}\").requests | take 1;\nx";
        Assert.Throws<InvalidOperationException>(() => TelemetrySourceScope.Restrict(kql, ["acme-web"]));
    }

    private static int CountOf(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
        {
            count++;
        }
        return count;
    }

    private static IAgentTool[] Tools(ITelemetrySource telemetry, ProfileTelemetrySourceResolver? resolver)
    {
        var provider = new KqlQueryTextProvider();
        var insights = Options.Create(new InsightsOptions { AppInsightsAppId = "app-id" });
        var scout = Options.Create(new ScoutOptions());
        var inspector = Options.Create(new InspectorOptions());
        return
        [
            new DrillDownEventsTool(telemetry, provider, new AliasResolver(), insights, scout, inspector, NullLogger<DrillDownEventsTool>.Instance, resolver),
            new ClubBaselineTool(telemetry, provider, new AliasResolver(), insights, scout, inspector, NullLogger<ClubBaselineTool>.Instance, resolver),
            new SignatureBreakdownTool(telemetry, provider, new AliasResolver(), insights, scout, inspector, NullLogger<SignatureBreakdownTool>.Instance, resolver),
            new ClientBreakdownTool(telemetry, provider, new AliasResolver(), insights, scout, inspector, NullLogger<ClientBreakdownTool>.Instance, resolver),
        ];
    }

    private static InspectorContext Context(InvestigationPackage package) =>
        new(
            new ForensicCase
            {
                InvestigationPackageId = package.Id,
                ProfileKey = package.ProfileKey,
                CaseId = package.CaseId,
                EntityKey = package.EntityKey,
                Vertical = package.Vertical,
                Stage = package.Stage,
            },
            package,
            new ClubIdentity(package.EntityKey, package.EntityKind, package.ClubId, null, package.BrandName, package.ClubName, package.ClubResolvedBy),
            []);

    /// <summary>No packaged evidence, so even drill_down_events has to query.</summary>
    private static InvestigationPackage Package(string profileKey) => new()
    {
        CaseId = "SOR | Hotels | host:rooms.acme.test | 10 Browse | 2026-11-07T09:00:00Z",
        ProfileKey = profileKey,
        Board = ScoutBoard.Exception,
        Vertical = "Hotels",
        Stage = "10 Browse",
        Source = "SOR",
        EntityKey = "host:rooms.acme.test",
        EntityKind = ClubEntityKind.Host,
        ClubResolvedBy = ClubResolution.Unresolved,
        Priority = "Investigate now",
        PriorityRank = 3,
        Reason = "At least 100 exceptions",
        WindowFrom = WindowFrom,
        WindowTo = WindowTo,
        DeterministicJson = """{"caseId":"c","groupPayload":{"Ops":["op-1","op-2"]}}""",
        AssessmentJson = "{}",
        RawEvidenceJson = null,
        ExceptionCount = 120,
        ScoutRunId = Guid.NewGuid(),
    };
}
