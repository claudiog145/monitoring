using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Configuration;
using JourneyMonitor.Core.Monitoring.Kpi;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Core.UseCases;
using JourneyMonitor.Functions.Functions;
using JourneyMonitor.Infrastructure.Monitoring;
using JourneyMonitor.Tests.Onboarding;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Tests.Company;

/// <summary>
/// AG8: a telemetry source alias belongs to one company profile (or is shared), so one company
/// cannot put another company's alias into its own service and read that telemetry through
/// Scouts, KPIs and kql: queries.
/// </summary>
public sealed class TelemetrySourceOwnershipTests
{
    private const string WriteSecret = "ag8-secret";
    private const string AcmeAlias = "acme-web";
    private const string SharedAlias = "shared-edge";
    private const string ResourceId = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg/providers/microsoft.insights/components/acme-web";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Profile_CannotUseAliasOwnedByAnotherProfile()
    {
        var sources = await CatalogAsync();
        var profiles = new InMemoryCompanyProfileStore();
        var admin = ProfileAdmin(profiles, sources);

        // Another company's alias.
        var stolen = await PutProfileAsync(admin, Profile("contoso", AcmeAlias));
        Assert.Equal(HttpStatusCode.BadRequest, stolen.StatusCode);
        Assert.Equal("Alias 'acme-web' belongs to profile 'acme'.", stolen.ReadJson()["error"]!.GetValue<string>());

        // The incumbent's aliases are owned too, after the backfill.
        var incumbent = await PutProfileAsync(admin, Profile("contoso", SharedAlias, TelemetrySourceDefaults.CruiseCore));
        Assert.Equal(HttpStatusCode.BadRequest, incumbent.StatusCode);
        Assert.Equal("Alias 'cruise-core' belongs to profile 'arrivia'.", incumbent.ReadJson()["error"]!.GetValue<string>());

        // Every offending alias is listed, and nothing was stored.
        var both = await PutProfileAsync(admin, Profile("contoso", AcmeAlias, TelemetrySourceDefaults.Club));
        var problems = both.ReadJson()["problems"]!.AsArray().Select(p => p!.GetValue<string>()).ToList();
        Assert.Equal(["Alias 'acme-web' belongs to profile 'acme'.", "Alias 'club' belongs to profile 'arrivia'."], problems);
        Assert.Empty(await profiles.GetAllAsync());

        // The pure use case agrees, independent of the HTTP path, and is case-insensitive.
        Assert.Equal(
            ["Alias 'ACME-WEB' belongs to profile 'acme'."],
            TelemetrySourceOwnership.FindForeignAliases(Profile("contoso", "ACME-WEB"), await sources.GetAllAsync()));

        // An alias the catalog does not know cannot be pre-claimed either.
        var squatted = await PutProfileAsync(admin, Profile("contoso", "newco-web"));
        Assert.Equal(HttpStatusCode.BadRequest, squatted.StatusCode);
        Assert.Equal("Alias 'newco-web' is not in the telemetry source catalog.", squatted.ReadJson()["error"]!.GetValue<string>());

        // The demo's own alias is not the incumbent's to take.
        var demoAlias = await PutProfileAsync(admin, Profile(KpiProfiles.Default, TelemetrySourceDefaults.DemoClub));
        Assert.Equal("Alias 'demo-club' belongs to profile 'demo'.", demoAlias.ReadJson()["error"]!.GetValue<string>());
        Assert.Empty(await profiles.GetAllAsync());
    }

    [Fact]
    public async Task Profile_CanUseOwnOrSharedAlias()
    {
        var sources = await CatalogAsync();
        var profiles = new InMemoryCompanyProfileStore();
        var admin = ProfileAdmin(profiles, sources);

        // Own alias (owner compared case-insensitively) and a shared alias.
        var own = await PutProfileAsync(admin, Profile("ACME", AcmeAlias, SharedAlias));
        Assert.True(own.StatusCode == HttpStatusCode.OK, own.ReadJson().ToJsonString());

        // Another company may use the shared alias.
        var shared = await PutProfileAsync(admin, Profile("contoso", SharedAlias));
        Assert.True(shared.StatusCode == HttpStatusCode.OK, shared.ReadJson().ToJsonString());

        // The incumbent profile, exactly as the SQL seed ships it, still saves - and saves unchanged.
        var seed = JsonSerializer.Deserialize<CompanyProfile>(ArriviaProfileSeedTests.SeedJsonFor(KpiProfiles.Default), JsonOptions)!;
        var incumbent = await PutProfileAsync(admin, seed);
        Assert.True(incumbent.StatusCode == HttpStatusCode.OK, incumbent.ReadJson().ToJsonString());
        var stored = await profiles.GetAsync(KpiProfiles.Default);
        Assert.NotNull(stored);
        Assert.Equal(
            seed.Services.Select(s => string.Join(",", s.TelemetrySourceAliases)),
            stored.Services.Select(s => string.Join(",", s.TelemetrySourceAliases)));
        Assert.Equal(seed.Services.Count, stored.Services.Count);
    }

    /// <summary>The demo seed, exactly as shipped (after the AG8 move to its own alias), still saves unchanged.</summary>
    [Fact]
    public async Task DemoSeed_UsesItsOwnAlias_AndSavesUnchanged()
    {
        var profiles = new InMemoryCompanyProfileStore();
        var admin = ProfileAdmin(profiles, new InMemoryTelemetrySourceStore());
        var seedJson = ArriviaProfileSeedTests.SeedJsonFor(TelemetrySourceDefaults.DemoOwnerProfileKey);
        var seed = JsonSerializer.Deserialize<CompanyProfile>(seedJson, JsonOptions)!;

        Assert.Equal([TelemetrySourceDefaults.DemoClub], seed.Services.SelectMany(s => s.TelemetrySourceAliases));

        var response = await PutProfileAsync(admin, seed);
        Assert.True(response.StatusCode == HttpStatusCode.OK, response.ReadJson().ToJsonString());
        var stored = await profiles.GetAsync(TelemetrySourceDefaults.DemoOwnerProfileKey);
        Assert.NotNull(stored);
        Assert.Equal(
            JsonSerializer.Serialize(seed with { UpdatedAt = null, UpdatedBy = null, Version = 0 }, JsonOptions),
            JsonSerializer.Serialize(stored with { UpdatedAt = null, UpdatedBy = null, Version = 0 }, JsonOptions));

        // The catalog row it points at: the demo's, disabled, and no real (non-placeholder) resource.
        var demoRows = (await new InMemoryTelemetrySourceStore().GetAllAsync()).Where(s => s.Alias == TelemetrySourceDefaults.DemoClub).ToList();
        Assert.Equal(2, demoRows.Count);
        Assert.All(demoRows, r =>
        {
            Assert.Equal(TelemetrySourceDefaults.DemoOwnerProfileKey, r.OwnerProfileKey);
            Assert.False(r.Enabled);
            Assert.Contains("/subscriptions/00000000-0000-0000-0000-000000000000/", r.ResourceId);
        });

        // The SQL scripts: the catalog seeds the row for 'demo', and an existing demo row that still
        // lists 'club' is migrated (versioned and audited), guarded so a re-run is a no-op.
        var portal = PortalDirectory();
        var sourcesSql = File.ReadAllText(Path.Combine(portal, "src", "JourneyMonitor.Infrastructure", "Sql", "monitoring", "CreateMonitoringTelemetrySource.sql"));
        Assert.Contains("(N'demo-club', N'production')", sourcesSql);
        Assert.Contains("N'seed', N'demo');", sourcesSql);
        var profilesSql = File.ReadAllText(Path.Combine(portal, "src", "JourneyMonitor.Infrastructure", "Sql", "monitoring", "CreateMonitoringCompanyProfile.sql"));
        Assert.Contains("WHERE p.ProfileKey = N'demo' AND ISJSON(p.ProfileJson) = 1 AND LOWER(LTRIM(RTRIM(a.value))) = N'club')", profilesSql);
        Assert.Contains("VALUES (N'demo', @DemoVersion, @DemoJson, 0, N'seed:demo-own-alias');", profilesSql);
    }

    /// <summary>Ownership is per alias: a new environment of somebody else's alias cannot be claimed (the tester's cruise-api/staging case).</summary>
    [Fact]
    public async Task Source_NewEnvironmentOfForeignAlias_Returns409()
    {
        var store = new InMemoryTelemetrySourceStore();
        var functions = SourceAdmin(store, activeProfileKey: "beta");
        Assert.DoesNotContain(await store.GetAllAsync(), s => s.Alias == TelemetrySourceDefaults.CruiseApi && s.Environment == InsightsEnvironment.Staging);

        foreach (var request in new[]
        {
            (Body: (object)new { alias = TelemetrySourceDefaults.CruiseApi, environment = "staging", resourceId = ResourceId, profileKey = "acme" }, Query: ""),
            (Body: (object)new { alias = TelemetrySourceDefaults.CruiseApi, environment = "staging", resourceId = ResourceId }, Query: "?profileKey=demo"),
        })
        {
            var claimed = await PutSourceAsync(functions, request.Body, request.Query);
            Assert.Equal(HttpStatusCode.Conflict, claimed.StatusCode);
            Assert.Equal("Source 'cruise-api' belongs to profile 'arrivia'.", claimed.ReadJson()["error"]!.GetValue<string>());
        }
        Assert.DoesNotContain(await store.GetAllAsync(), s => s.Alias == TelemetrySourceDefaults.CruiseApi && s.Environment == InsightsEnvironment.Staging);

        // Naming nobody, the new environment inherits the alias's owner - not the active profile.
        var inherited = await PutSourceAsync(functions, new { alias = TelemetrySourceDefaults.CruiseApi, environment = "staging", resourceId = ResourceId });
        Assert.Equal(HttpStatusCode.OK, inherited.StatusCode);
        Assert.Equal(KpiProfiles.Default, inherited.ReadJson()["ownerProfileKey"]!.GetValue<string>());

        // The incumbent profile still saves against the resulting catalog.
        var seed = JsonSerializer.Deserialize<CompanyProfile>(ArriviaProfileSeedTests.SeedJsonFor(KpiProfiles.Default), JsonOptions)!;
        var saved = await PutProfileAsync(ProfileAdmin(new InMemoryCompanyProfileStore(), store), seed);
        Assert.True(saved.StatusCode == HttpStatusCode.OK, saved.ReadJson().ToJsonString());

        // An explicit transfer moves the alias in EVERY environment, each move audited.
        var moved = await PutSourceAsync(functions, new { alias = TelemetrySourceDefaults.Club, environment = "staging", resourceId = ResourceId, transferOwnerTo = "acme" });
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        Assert.All((await store.GetAllAsync()).Where(s => s.Alias == TelemetrySourceDefaults.Club), s => Assert.Equal("acme", s.OwnerProfileKey));
        Assert.Equal(2, (await store.GetAuditAsync(50)).Count(a => a.Alias == TelemetrySourceDefaults.Club && a.OwnerProfileKey == "acme" && a.PreviousOwnerProfileKey == KpiProfiles.Default));
    }

    /// <summary>Deleting an alias does not free it: recreating it for another profile still needs an explicit transfer.</summary>
    [Fact]
    public async Task Source_DeleteAndRecreateAsOtherOwner_RequiresTransfer()
    {
        var store = new InMemoryTelemetrySourceStore();
        var functions = SourceAdmin(store, activeProfileKey: "beta");
        const string alias = TelemetrySourceDefaults.SorFrontend;

        // A delete naming somebody other than the owner is refused; naming nobody (the Settings UI) is allowed.
        var wrongOwner = await DeleteSourceAsync(functions, alias, new { profileKey = "acme" });
        Assert.Equal(HttpStatusCode.Conflict, wrongOwner.StatusCode);
        Assert.Equal("Source 'sor-frontend' belongs to profile 'arrivia'.", wrongOwner.ReadJson()["error"]!.GetValue<string>());
        Assert.Contains(await store.GetAllAsync(), s => s.Alias == alias);
        Assert.Equal(HttpStatusCode.OK, (await DeleteSourceAsync(functions, alias, new { changedBy = "ana@example.com" })).StatusCode);
        Assert.DoesNotContain(await store.GetAllAsync(), s => s.Alias == alias);

        // Recreated for another profile: 409, the audit still knows whose it was.
        var recreated = await PutSourceAsync(functions, new { alias, resourceId = ResourceId, profileKey = "acme" });
        Assert.Equal(HttpStatusCode.Conflict, recreated.StatusCode);
        Assert.Equal("Source 'sor-frontend' belongs to profile 'arrivia'.", recreated.ReadJson()["error"]!.GetValue<string>());
        Assert.DoesNotContain(await store.GetAllAsync(), s => s.Alias == alias);

        // Recreated naming nobody: it goes back to its previous owner, not to the active profile.
        Assert.Equal(HttpStatusCode.OK, (await PutSourceAsync(functions, new { alias, resourceId = ResourceId })).StatusCode);
        Assert.Equal(KpiProfiles.Default, (await FindAsync(store, alias)).OwnerProfileKey);

        // Only an explicit transfer hands it over.
        Assert.Equal(HttpStatusCode.OK, (await DeleteSourceAsync(functions, alias, new { profileKey = "arrivia" })).StatusCode);
        var transferred = await PutSourceAsync(functions, new { alias, resourceId = ResourceId, transferOwnerTo = "acme" });
        Assert.Equal(HttpStatusCode.OK, transferred.StatusCode);
        Assert.Equal("acme", (await FindAsync(store, alias)).OwnerProfileKey);
    }

    [Fact]
    public async Task Source_EditWithOtherProfileKey_Returns409()
    {
        var store = new InMemoryTelemetrySourceStore();
        var functions = SourceAdmin(store, activeProfileKey: "beta");

        // Body and query string alike: naming another profile on an edit does not move the row.
        var body = await PutSourceAsync(functions, new { alias = TelemetrySourceDefaults.Club, resourceId = ResourceId, profileKey = "acme" });
        Assert.Equal(HttpStatusCode.Conflict, body.StatusCode);
        Assert.Equal("Source 'club' belongs to profile 'arrivia'.", body.ReadJson()["error"]!.GetValue<string>());
        var query = await PutSourceAsync(functions, new { alias = TelemetrySourceDefaults.Club, resourceId = ResourceId }, "?profileKey=acme");
        Assert.Equal(HttpStatusCode.Conflict, query.StatusCode);

        var club = await FindAsync(store, TelemetrySourceDefaults.Club);
        Assert.Equal(KpiProfiles.Default, club.OwnerProfileKey);
        Assert.NotEqual(ResourceId, club.ResourceId);
        Assert.Empty(await store.GetAuditAsync(10));

        // Naming the current owner is just an edit.
        Assert.Equal(HttpStatusCode.OK, (await PutSourceAsync(functions, new { alias = TelemetrySourceDefaults.Club, resourceId = ResourceId, profileKey = "ARRIVIA" })).StatusCode);
        Assert.Equal(KpiProfiles.Default, (await FindAsync(store, TelemetrySourceDefaults.Club)).OwnerProfileKey);
    }

    [Fact]
    public async Task Source_TransferToUnknownProfile_Returns400()
    {
        var store = new InMemoryTelemetrySourceStore();
        var functions = SourceAdmin(store, activeProfileKey: "beta");

        var transfer = await PutSourceAsync(functions, new { alias = TelemetrySourceDefaults.Club, resourceId = ResourceId, transferOwnerTo = "ghost" });
        Assert.Equal(HttpStatusCode.BadRequest, transfer.StatusCode);
        Assert.Equal("Unknown profileKey 'ghost'.", transfer.ReadJson()["error"]!.GetValue<string>());
        Assert.Equal(KpiProfiles.Default, (await FindAsync(store, TelemetrySourceDefaults.Club)).OwnerProfileKey);

        // A new row may not be created for a profile that does not exist either.
        var create = await PutSourceAsync(functions, new { alias = "ghost-web", resourceId = ResourceId, profileKey = "ghost" });
        Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);
        Assert.Equal("Unknown profileKey 'ghost'.", create.ReadJson()["error"]!.GetValue<string>());
        Assert.DoesNotContain(await store.GetAllAsync(), s => s.Alias == "ghost-web");
    }

    [Fact]
    public async Task Source_ExplicitTransfer_IsAudited()
    {
        var store = new InMemoryTelemetrySourceStore();
        var functions = SourceAdmin(store, activeProfileKey: "beta");

        var transfer = await PutSourceAsync(functions, new { alias = TelemetrySourceDefaults.CruiseApi, resourceId = ResourceId, transferOwnerTo = "acme", changedBy = "ana@example.com" });
        Assert.Equal(HttpStatusCode.OK, transfer.StatusCode);
        Assert.Equal("acme", (await FindAsync(store, TelemetrySourceDefaults.CruiseApi)).OwnerProfileKey);

        var entry = Assert.Single(await store.GetAuditAsync(10));
        Assert.Equal(TelemetrySourceDefaults.CruiseApi, entry.Alias);
        Assert.Equal("ana@example.com", entry.ChangedBy);
        Assert.Equal(KpiProfiles.Default, entry.PreviousOwnerProfileKey);
        Assert.Equal("acme", entry.OwnerProfileKey);

        // The audit endpoint carries both owners.
        var audit = await functions.AuditAsync(Request("GET", "settings/telemetry-sources-audit", ""), default);
        var json = audit.ReadJson()["entries"]!.AsArray()[0]!;
        Assert.Equal("arrivia", json["previousOwnerProfileKey"]!.GetValue<string>());
        Assert.Equal("acme", json["ownerProfileKey"]!.GetValue<string>());

        // A later plain edit keeps the new owner, and the audit shows no change of hands.
        Assert.Equal(HttpStatusCode.OK, (await PutSourceAsync(functions, new { alias = TelemetrySourceDefaults.CruiseApi, resourceId = ResourceId, notes = "n" })).StatusCode);
        var latest = (await store.GetAuditAsync(10))[0];
        Assert.Equal("acme", latest.OwnerProfileKey);
        Assert.Equal("acme", latest.PreviousOwnerProfileKey);
    }

    [Fact]
    public async Task TelemetrySource_NewRowOwnedByCreatingProfile()
    {
        var store = new InMemoryTelemetrySourceStore();
        var functions = SourceAdmin(store, activeProfileKey: "beta");

        // Named in the body.
        Assert.Equal(HttpStatusCode.OK, (await PutSourceAsync(functions, new { alias = AcmeAlias, resourceId = ResourceId, profileKey = "acme" })).StatusCode);
        Assert.Equal("acme", (await FindAsync(store, AcmeAlias)).OwnerProfileKey);

        // Named in the query string.
        Assert.Equal(HttpStatusCode.OK, (await PutSourceAsync(functions, new { alias = "gamma-web", resourceId = ResourceId }, "?profileKey=gamma")).StatusCode);
        Assert.Equal("gamma", (await FindAsync(store, "gamma-web")).OwnerProfileKey);

        // Not named: the active profile creates it.
        var created = await PutSourceAsync(functions, new { alias = "beta-web", resourceId = ResourceId });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        Assert.Equal("beta", created.ReadJson()["ownerProfileKey"]!.GetValue<string>());
        Assert.Equal("beta", (await FindAsync(store, "beta-web")).OwnerProfileKey);

        // An edit that names nobody keeps the stored owner instead of re-homing the row to the active profile.
        Assert.Equal(HttpStatusCode.OK, (await PutSourceAsync(functions, new { alias = AcmeAlias, resourceId = ResourceId, notes = "edited" })).StatusCode);
        var edited = await FindAsync(store, AcmeAlias);
        Assert.Equal("acme", edited.OwnerProfileKey);
        Assert.Equal("edited", edited.Notes);

        // Without a profile store or resolver (bare host) a new row falls back to the incumbent, never to "shared".
        Assert.Equal(HttpStatusCode.OK, (await PutSourceAsync(new TelemetrySourceAdminFunctions(
            store, new NoopResolver(), Options.Create(new MonitoringOptions { SettingsWriteSecret = WriteSecret }),
            NullLogger<TelemetrySourceAdminFunctions>.Instance), new { alias = "bare-web", resourceId = ResourceId })).StatusCode);
        Assert.Equal(KpiProfiles.Default, (await FindAsync(store, "bare-web")).OwnerProfileKey);

        // A malformed owner key is refused.
        var bad = await PutSourceAsync(functions, new { alias = "bad-web", resourceId = ResourceId, profileKey = "*" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.DoesNotContain(await store.GetAllAsync(), s => s.Alias == "bad-web");

        // The list endpoint carries the owner.
        var list = await functions.ListAsync(Request("GET", "settings/telemetry-sources", ""), default);
        Assert.Contains(list.ReadJson()["sources"]!.AsArray(), s => s!["alias"]!.GetValue<string>() == AcmeAlias && s["ownerProfileKey"]!.GetValue<string>() == "acme");
    }

    [Fact]
    public async Task ExistingCatalogRows_OwnedByIncumbent()
    {
        static bool IsDemo(TelemetrySourceEntry s) => s.Alias == TelemetrySourceDefaults.DemoClub;
        Assert.All(TelemetrySourceDefaults.All.Where(s => !IsDemo(s)), s => Assert.Equal(KpiProfiles.Default, s.OwnerProfileKey));
        Assert.All((await new InMemoryTelemetrySourceStore().GetAllAsync()).Where(s => !IsDemo(s)), s => Assert.Equal(KpiProfiles.Default, s.OwnerProfileKey));
        Assert.All(TelemetrySourceDefaults.All.Where(IsDemo), s => Assert.Equal(TelemetrySourceDefaults.DemoOwnerProfileKey, s.OwnerProfileKey));

        // The script that reaches the database: the column is added once, rows that predate it are
        // backfilled to the incumbent in that same guarded batch (so a re-run never overwrites a
        // row an operator made shared), and the seed inserts incumbent-owned rows.
        var sql = File.ReadAllText(Path.Combine(PortalDirectory(), "src", "JourneyMonitor.Infrastructure", "Sql", "monitoring", "CreateMonitoringTelemetrySource.sql"));
        var guarded = Regex.Match(sql,
            @"IF COL_LENGTH\(N'monitoring\.TelemetrySource', N'OwnerProfileKey'\) IS NULL\s+BEGIN\s+ALTER TABLE monitoring\.TelemetrySource ADD OwnerProfileKey nvarchar\(128\) NULL;\s+EXEC\(N'UPDATE monitoring\.TelemetrySource SET OwnerProfileKey = N''arrivia'' WHERE OwnerProfileKey IS NULL'\);\s+END",
            RegexOptions.IgnoreCase);
        Assert.True(guarded.Success, "The OwnerProfileKey column + incumbent backfill must be one batch guarded by COL_LENGTH IS NULL.");
        Assert.Contains("OwnerProfileKey nvarchar(128)  NULL", sql);
        Assert.Contains("source.Notes, N'seed', N'arrivia')", sql);
        // An edit that omits the owner keeps the stored one; shared has to be asked for.
        Assert.Contains("ELSE COALESCE(@OwnerProfileKey, target.OwnerProfileKey) END", sql);
        Assert.Contains("@MakeShared bit = 0", sql);
        Assert.Contains("IF COL_LENGTH(N'monitoring.TelemetrySourceAudit', N'PreviousOwnerProfileKey') IS NULL", sql);
    }

    private static async Task<InMemoryTelemetrySourceStore> CatalogAsync()
    {
        var store = new InMemoryTelemetrySourceStore();
        await store.UpsertAsync(new TelemetrySourceEntry(AcmeAlias, InsightsEnvironment.Production, TelemetrySourceKind.AppInsights, ResourceId, OwnerProfileKey: "acme"), "test");
        await store.UpsertAsync(new TelemetrySourceEntry(AcmeAlias, InsightsEnvironment.Staging, TelemetrySourceKind.AppInsights, ResourceId, OwnerProfileKey: "acme"), "test");
        await store.UpsertAsync(new TelemetrySourceEntry(SharedAlias, InsightsEnvironment.Production, TelemetrySourceKind.AppInsights, ResourceId, OwnerProfileKey: null), "test");
        return store;
    }

    private static CompanyProfileAdminFunctions ProfileAdmin(ICompanyProfileStore profiles, ITelemetrySourceStore sources) => new(
        profiles,
        Options.Create(new MonitoringOptions { SettingsWriteSecret = WriteSecret }),
        NullLogger<CompanyProfileAdminFunctions>.Instance,
        aliasOwnership: new TelemetrySourceOwnershipGuard(sources));

    /// <summary>Profiles that exist in these tests; "ghost" deliberately does not.</summary>
    private static readonly string[] KnownProfiles = [KpiProfiles.Default, "demo", "acme", "beta", "gamma"];

    private static TelemetrySourceAdminFunctions SourceAdmin(ITelemetrySourceStore store, string activeProfileKey) => new(
        store,
        new NoopResolver(),
        Options.Create(new MonitoringOptions { SettingsWriteSecret = WriteSecret }),
        NullLogger<TelemetrySourceAdminFunctions>.Instance,
        new TelemetrySourceOwnerPolicy(
            new InMemoryCompanyProfileStore(KnownProfiles.Select(k => Profile(k) with { Enabled = k != "gamma" })),
            new KpiProfileResolver(Options.Create(new ProfileDrivenCompanyOptions { DefaultProfileKey = activeProfileKey }))));

    private static Task<Microsoft.Azure.Functions.Worker.Http.HttpResponseData> PutProfileAsync(CompanyProfileAdminFunctions admin, CompanyProfile profile) =>
        admin.UpsertAsync(
            Request("PUT", $"settings/company-profiles/{profile.ProfileKey}", JsonSerializer.Serialize(profile, JsonOptions)),
            profile.ProfileKey,
            default);

    private static Task<Microsoft.Azure.Functions.Worker.Http.HttpResponseData> PutSourceAsync(TelemetrySourceAdminFunctions functions, object body, string query = "") =>
        functions.UpsertAsync(Request("PUT", "settings/telemetry-sources" + query, JsonSerializer.Serialize(body, JsonOptions)), default);

    private static Task<Microsoft.Azure.Functions.Worker.Http.HttpResponseData> DeleteSourceAsync(TelemetrySourceAdminFunctions functions, string alias, object body) =>
        functions.DeleteAsync(Request("POST", $"settings/telemetry-sources/{alias}/production/delete", JsonSerializer.Serialize(body, JsonOptions)), alias, "production", default);

    private static OnboardingHttpRequest Request(string method, string route, string body) =>
        new OnboardingHttpRequest(method, "https://localhost/api/" + route, body).WithHeader("X-Settings-Write-Secret", WriteSecret);

    private static async Task<TelemetrySourceEntry> FindAsync(ITelemetrySourceStore store, string alias) =>
        Assert.Single(await store.GetAllAsync(), s => s.Alias == alias && s.Environment == InsightsEnvironment.Production);

    private static CompanyProfile Profile(string key, params string[] aliases) => new(
        ProfileKey: key,
        DisplayName: key,
        Labels: new CompanyLabels("Service", "Flow", "Step", "Entity", "Owner"),
        Services: [new ServiceDefinition("checkout", "Checkout", aliases, [], [])],
        EntityIdentitySources: [new EntityIdentitySource(1, EntityIdentitySourceKind.CustomDimension, "TenantId", "tenant")],
        Glossary: new CompanyGlossary("service", "flow", "step", "entity", "owner", "Sells widgets."));

    private static string PortalDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "JourneyMonitor.slnx"))) return dir.FullName;
        }
        throw new InvalidOperationException("Could not find the Portal directory (JourneyMonitor.slnx) above the test output.");
    }

    private sealed class NoopResolver : IKqlSourceResolver
    {
        public Task<string> ResolveAsync(string kql, string environment, CancellationToken ct = default) => Task.FromResult(kql);

        public void Invalidate()
        {
        }
    }
}
