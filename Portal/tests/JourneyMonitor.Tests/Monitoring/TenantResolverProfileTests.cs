using System.Text.Json;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Monitoring;
using JourneyMonitor.Infrastructure.Monitoring;
using JourneyMonitor.Tests.Company;
using JourneyMonitor.Tests.Spec1000.D1;

namespace JourneyMonitor.Tests.Monitoring;

/// <summary>Fixed active pointer for <see cref="TenantResolver"/> tests; counts reads.</summary>
internal sealed class FixedActiveCompanyProfileStore(string? activeKey) : IActiveCompanyProfileStore
{
    public int Reads { get; private set; }

    public Task<string?> GetActiveAsync(CancellationToken ct = default)
    {
        Reads++;
        return Task.FromResult(activeKey);
    }

    public Task<ActiveCompanyProfile?> GetActiveDetailAsync(CancellationToken ct = default) =>
        Task.FromResult(activeKey is null ? null : new ActiveCompanyProfile(activeKey, DateTimeOffset.UnixEpoch, "test"));

    public Task SetActiveAsync(string profileKey, string changedBy, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>Profiles built for tenant-resolution tests. Shared with the App Insights auth tests.</summary>
internal static class TenantProfiles
{
    public const string SharedTenant = "aaaaaaaa-0000-0000-0000-000000000001";
    public const string OtherTenant = "bbbbbbbb-0000-0000-0000-000000000002";

    public static CompanyProfile Profile(string key, IReadOnlyList<TelemetryTenant>? tenants, bool enabled = true) =>
        new(
            key,
            key.ToUpperInvariant(),
            new CompanyLabels("Product area", "Journey", "Step", "Account", "Owning team"),
            [new ServiceDefinition("alpha", "Alpha", ["source-alias"], [],
                [new FlowDefinition("flow", "Flow", [new StepDefinition("step-a", "Step A", 1, [new StepMatcher(StepMatcherKind.UrlContains, "/a")])])])],
            [new EntityIdentitySource(1, EntityIdentitySourceKind.CustomDimension, "AccountId", "account")],
            new CompanyGlossary("product area", "journey", "step", "account", "owning team", "A synthetic company."),
            Enabled: enabled,
            TelemetryTenants: tenants);

    public static TenantResolver Resolver(string? active, params CompanyProfile[] profiles) =>
        new(new InMemoryCompanyProfileStore(profiles), new FixedActiveCompanyProfileStore(active));
}

public class TenantResolverProfileTests
{
    private static readonly IReadOnlyList<TelemetryTenant> SharedTenants =
    [
        new("storefront", TenantProfiles.SharedTenant, ["web", "mobile"]),
        new("ships", TenantProfiles.SharedTenant, ["sea"]),
        new("partner", TenantProfiles.OtherTenant, ["flights"]),
    ];

    [Fact]
    public async Task TenantResolver_MapsTenantAndDomainFromProfile()
    {
        var resolver = TenantProfiles.Resolver("subject", TenantProfiles.Profile("subject", SharedTenants));

        Assert.Equal("storefront", await resolver.ResolveTenantKeyAsync(TenantProfiles.SharedTenant, "web"));
        Assert.Equal("storefront", await resolver.ResolveTenantKeyAsync(TenantProfiles.SharedTenant, "MOBILE"));
        Assert.Equal("ships", await resolver.ResolveTenantKeyAsync(TenantProfiles.SharedTenant.ToUpperInvariant(), "sea"));
        Assert.Equal("partner", await resolver.ResolveTenantKeyAsync(TenantProfiles.OtherTenant, "flights"));

        // UUID alone (or a domain nobody declares) matches several keys: first in profile order.
        Assert.Equal("storefront", await resolver.ResolveTenantKeyAsync(TenantProfiles.SharedTenant));
        Assert.Equal("storefront", await resolver.ResolveTenantKeyAsync(TenantProfiles.SharedTenant, "unknown-domain"));
        Assert.Equal("partner", await resolver.ResolveTenantKeyAsync(TenantProfiles.OtherTenant, "sea"));
    }

    [Fact]
    public async Task TenantResolver_UnknownTenant_ReturnsUuid()
    {
        const string unknown = "dddddddd-0000-0000-0000-000000000004";
        var resolver = TenantProfiles.Resolver("subject", TenantProfiles.Profile("subject", SharedTenants));

        Assert.Equal(unknown, await resolver.ResolveTenantKeyAsync(unknown, "web"));
        Assert.Equal(unknown, await resolver.ResolveTenantKeyAsync(unknown));
        Assert.Equal(unknown, await new TenantResolver().ResolveTenantKeyAsync(unknown, "web"));
    }

    [Fact]
    public async Task TenantResolver_ProfileWithoutTenants_ReturnsUuid()
    {
        var nullTenants = TenantProfiles.Resolver("subject", TenantProfiles.Profile("subject", null));
        var emptyTenants = TenantProfiles.Resolver("subject", TenantProfiles.Profile("subject", []));

        Assert.Equal(TenantProfiles.SharedTenant, await nullTenants.ResolveTenantKeyAsync(TenantProfiles.SharedTenant, "web"));
        Assert.Equal(TenantProfiles.SharedTenant, await emptyTenants.ResolveTenantKeyAsync(TenantProfiles.SharedTenant));
        Assert.Empty(await emptyTenants.GetAllTenantsAsync());
    }

    [Fact]
    public async Task TenantResolver_ActiveProfileWins_OthersConsultedWhenItLacksTheTenant()
    {
        var active = TenantProfiles.Profile("active", [new TelemetryTenant("active-key", TenantProfiles.SharedTenant, ["web"])]);
        var other = TenantProfiles.Profile("other", [
            new TelemetryTenant("other-shared", TenantProfiles.SharedTenant, ["web"]),
            new TelemetryTenant("other-only", TenantProfiles.OtherTenant, []),
        ]);
        var disabled = TenantProfiles.Profile("disabled", [new TelemetryTenant("disabled-key", "cccccccc-0000-0000-0000-000000000003", [])], enabled: false);

        // "other" sorts first in the store; the active profile still wins for a tenant it declares.
        var resolver = TenantProfiles.Resolver("active", other, active, disabled);

        Assert.Equal("active-key", await resolver.ResolveTenantKeyAsync(TenantProfiles.SharedTenant, "web"));
        Assert.Equal("other-only", await resolver.ResolveTenantKeyAsync(TenantProfiles.OtherTenant));
        Assert.Equal("cccccccc-0000-0000-0000-000000000003", await resolver.ResolveTenantKeyAsync("cccccccc-0000-0000-0000-000000000003"));
    }

    [Fact]
    public async Task TenantResolver_GetTenantConfig_IsBackedByProfileTenants()
    {
        var resolver = TenantProfiles.Resolver("subject", TenantProfiles.Profile("subject", SharedTenants));

        var all = await resolver.GetAllTenantsAsync();
        Assert.Equal(["storefront", "ships", "partner"], all.Select(t => t.TenantKey));

        var ships = await resolver.GetTenantConfigAsync("SHIPS");
        Assert.Equal(TenantProfiles.SharedTenant, ships.AadTenantId);
        Assert.Equal(["sea"], ships.Domains);

        await Assert.ThrowsAsync<KeyNotFoundException>(() => resolver.GetTenantConfigAsync("missing"));
        await Assert.ThrowsAsync<ArgumentException>(() => resolver.GetTenantConfigAsync(" "));
        Assert.Empty(await new TenantResolver().GetAllTenantsAsync());
    }

    [Fact]
    public async Task TenantResolver_CachesProfilesForSixtySeconds()
    {
        var store = new InMemoryCompanyProfileStore([TenantProfiles.Profile("subject", SharedTenants)]);
        var active = new FixedActiveCompanyProfileStore("subject");
        var time = new D1FixedTimeProvider(new DateTimeOffset(2026, 11, 6, 12, 0, 0, TimeSpan.Zero));
        var resolver = new TenantResolver(store, active, time: time);

        Assert.Equal("ships", await resolver.ResolveTenantKeyAsync(TenantProfiles.SharedTenant, "sea"));
        await store.UpsertAsync(TenantProfiles.Profile("subject", [new TelemetryTenant("renamed", TenantProfiles.SharedTenant, ["sea"])]), "test");

        time.Now = time.Now.AddSeconds(59);
        Assert.Equal("ships", await resolver.ResolveTenantKeyAsync(TenantProfiles.SharedTenant, "sea"));
        Assert.Equal(1, active.Reads);

        time.Now = time.Now.AddSeconds(2);
        Assert.Equal("renamed", await resolver.ResolveTenantKeyAsync(TenantProfiles.SharedTenant, "sea"));
        Assert.Equal(2, active.Reads);
    }

    [Fact]
    public async Task TenantResolver_StoreFailure_ReturnsUuid()
    {
        var resolver = new TenantResolver(new ThrowingProfileStore());

        Assert.Equal(TenantProfiles.SharedTenant, await resolver.ResolveTenantKeyAsync(TenantProfiles.SharedTenant, "web"));
    }

    [Fact]
    public void CompanyProfileValidator_RejectsDuplicateTenantKey()
    {
        var profile = TenantProfiles.Profile("subject", [
            new TelemetryTenant("storefront", TenantProfiles.SharedTenant, ["web"]),
            new TelemetryTenant("STOREFRONT", TenantProfiles.OtherTenant, ["sea"]),
        ]);

        var finding = Assert.Single(CompanyProfileValidator.Validate(profile));
        Assert.True(finding.IsError);
        Assert.Equal("telemetry-tenant-key-duplicate", finding.Code);
        Assert.Equal("$.telemetryTenants[1].tenantKey", finding.Path);

        var blank = TenantProfiles.Profile("subject", [new TelemetryTenant(" ", TenantProfiles.SharedTenant, [])]);
        Assert.Equal("telemetry-tenant-key-missing", Assert.Single(CompanyProfileValidator.Validate(blank)).Code);

        Assert.Empty(CompanyProfileValidator.Validate(TenantProfiles.Profile("subject", SharedTenants)));
        Assert.Empty(CompanyProfileValidator.Validate(TenantProfiles.Profile("subject", null)));
        Assert.Empty(CompanyProfileValidator.Validate(TenantProfiles.Profile("subject", [])));
    }

    [Theory]
    [InlineData("YOUR-AAD-TENANT-ID")]
    [InlineData("")]
    [InlineData("aaaaaaaa-0000-0000-0000-00000000000")]
    public void CompanyProfileValidator_RejectsNonGuidAadTenantId(string aadTenantId)
    {
        var profile = TenantProfiles.Profile("subject", [new TelemetryTenant("storefront", aadTenantId, ["web"])]);

        var finding = Assert.Single(CompanyProfileValidator.Validate(profile));
        Assert.True(finding.IsError);
        Assert.Equal("telemetry-tenant-aad-invalid", finding.Code);
        Assert.Equal("$.telemetryTenants[0].aadTenantId", finding.Path);
    }

    [Fact]
    public async Task ArriviaSeed_DeclaresTelemetryTenants()
    {
        const string hotelsTenant = "60396ff5-e90f-4bc5-aced-a4f462f2636e";
        const string airTenant = "b0298d22-1db0-4550-8fa6-a116a5ab4208";

        var json = ArriviaProfileSeedTests.SeedJsonFor("arrivia");
        var profile = JsonSerializer.Deserialize<CompanyProfile>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        Assert.NotNull(profile.TelemetryTenants);
        Assert.Collection(profile.TelemetryTenants!,
            t => { Assert.Equal("arrivia", t.TenantKey); Assert.Equal(hotelsTenant, t.AadTenantId); Assert.Equal(["hotels", "cars"], t.Domains); },
            t => { Assert.Equal("cruises", t.TenantKey); Assert.Equal(hotelsTenant, t.AadTenantId); Assert.Equal(["cruises"], t.Domains); },
            t => { Assert.Equal("ice", t.TenantKey); Assert.Equal(airTenant, t.AadTenantId); Assert.Equal(["air"], t.Domains); });
        Assert.DoesNotContain(CompanyProfileValidator.Validate(profile), f => f.IsError);

        // The keys the token store and the refresh daemon already use keep resolving the same way.
        var resolver = TenantProfiles.Resolver("arrivia", profile);
        Assert.Equal("arrivia", await resolver.ResolveTenantKeyAsync(hotelsTenant, "hotels"));
        Assert.Equal("arrivia", await resolver.ResolveTenantKeyAsync(hotelsTenant));
        Assert.Equal("cruises", await resolver.ResolveTenantKeyAsync(hotelsTenant, "cruises"));
        Assert.Equal("ice", await resolver.ResolveTenantKeyAsync(airTenant, "air"));
        Assert.Equal("ice", await resolver.ResolveTenantKeyAsync(airTenant));
    }

    private sealed class ThrowingProfileStore : ICompanyProfileStore
    {
        public Task<CompanyProfile?> GetAsync(string profileKey, CancellationToken ct = default) => throw new InvalidOperationException("down");
        public Task<IReadOnlyList<CompanyProfile>> GetAllAsync(CancellationToken ct = default) => throw new InvalidOperationException("down");
        public Task<CompanyProfile> UpsertAsync(CompanyProfile profile, string changedBy, CancellationToken ct = default) => throw new InvalidOperationException("down");
        public Task DeleteAsync(string profileKey, string changedBy, CancellationToken ct = default) => throw new InvalidOperationException("down");
        public Task<IReadOnlyList<CompanyProfileAuditEntry>> GetAuditAsync(int top, CancellationToken ct = default) => throw new InvalidOperationException("down");
    }
}
