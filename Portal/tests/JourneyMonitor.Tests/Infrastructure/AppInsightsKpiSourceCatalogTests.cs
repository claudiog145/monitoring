using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Configuration;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Infrastructure.AppInsights;
using static JourneyMonitor.Tests.Spec1000.A2.KpiSourceDoubles;

namespace JourneyMonitor.Tests.Spec1000.A2;

/// <summary>
/// Contract v4 §5, source resolution (task AG2): the KPI query target (app id + tenant) is resolved
/// service → TelemetrySourceAliases → monitoring.TelemetrySource; InsightsOptions is only the
/// fallback for a service that declares no alias.
/// </summary>
public sealed class AppInsightsKpiSourceCatalogTests
{
    private static readonly string[] SourceKeys = [AppInsightsKpiSource.LatencySourceKey, AppInsightsKpiSource.ExceptionsSourceKey];

    private static InsightsOptions HotelInsights() => new()
    {
        Hotels = new HotelInsightsOptions { AppInsightsAppId = "insights-hotel-app" },
    };

    private static CompanyProfile Multi() => HostScoped() with
    {
        Services =
        [
            Service("hotels", ["acme-primary", "acme-secondary"], ["rooms.acme.test"], ("10-browse", "10 Browse", 1, "/browse")),
        ],
    };

    [Fact]
    public async Task KpiSource_ResolvesAppFromProfileAliases()
    {
        var catalog = new CatalogStore(
        [
            new("acme-secondary", InsightsEnvironment.Production, TelemetrySourceKind.AppInsights, "/res/acme-secondary", AppId: "secondary-app"),
            new("acme-primary", InsightsEnvironment.Staging, TelemetrySourceKind.AppInsights, "/res/acme-primary-uat", AppId: "primary-uat-app"),
            new("ACME-PRIMARY", InsightsEnvironment.Production, TelemetrySourceKind.AppInsights, "/res/acme-primary", AppId: " primary-app ", TenantId: "acme-tenant"),
        ]);

        foreach (var sourceKey in SourceKeys)
        {
            var telemetry = new CapturingTelemetrySource();
            // InsightsOptions knows the domain too; the alias wins, so it is never consulted.
            var source = Source(telemetry, catalog, HotelInsights(), Multi());
            await source.FetchAsync(Definition(sourceKey, profileKey: "acme"), From, To, 10);

            var request = Assert.Single(telemetry.Requests);
            // First alias = primary execution app, in the configured environment; case-insensitive alias match.
            Assert.Equal("primary-app", request.AppInsightsAppId);
            Assert.Equal("acme-tenant", request.TenantId);
            // The query still unions every alias of the service.
            Assert.Contains("app(\"/res/acme-primary\")", request.Kql, StringComparison.Ordinal);
            Assert.Contains("app(\"/res/acme-secondary\")", request.Kql, StringComparison.Ordinal);
        }

        // The incumbent's shape: hotels and cars share one source, whose complete catalog row carries the app.
        var incumbentTelemetry = new CapturingTelemetrySource();
        var incumbent = Source(incumbentTelemetry, new CatalogStore(
            [new("web-front", InsightsEnvironment.Production, TelemetrySourceKind.AppInsights, "/res/web-front", AppId: "catalog-app", TenantId: "catalog-tenant")]),
            HotelInsights(), Incumbent());
        await incumbent.FetchAsync(Definition(domain: "hotels"), From, To, 10);
        await incumbent.FetchAsync(Definition(domain: "cars"), From, To, 10);
        Assert.All(incumbentTelemetry.Requests, r => Assert.Equal("catalog-app", r.AppInsightsAppId));
        Assert.All(incumbentTelemetry.Requests, r => Assert.Equal("catalog-tenant", r.TenantId));
        Assert.Equal(2, incumbentTelemetry.Requests.Count);

        // A row with AppId but no TenantId is used as is when InsightsOptions has nothing to offer.
        var noTenant = new CapturingTelemetrySource();
        await Source(noTenant, new CatalogStore(
            [new("web-front", InsightsEnvironment.Production, TelemetrySourceKind.AppInsights, "/res/web-front", AppId: "catalog-app")]),
            new InsightsOptions(), Incumbent()).FetchAsync(Definition(domain: "hotels"), From, To, 10);
        Assert.Equal("catalog-app", Assert.Single(noTenant.Requests).AppInsightsAppId);
        Assert.Null(noTenant.Requests[0].TenantId);
    }

    [Fact]
    public async Task KpiSource_CatalogRowWithoutAppId_FallsBackToInsightsOptions()
    {
        // A unique alias: the one-time Warning is process-wide.
        var alias = "fill-me-" + Guid.NewGuid().ToString("N")[..8];
        var profile = Incumbent() with
        {
            Services = [Service("hotels", [alias, "web-front"], [], ("02-search", "02 Hotel Search", 41, "search.json"))],
        };
        var catalog = new CatalogStore(
        [
            new(alias, InsightsEnvironment.Production, TelemetrySourceKind.AppInsights, "/res/" + alias),
            new("web-front", InsightsEnvironment.Production, TelemetrySourceKind.AppInsights, "/res/web-front", AppId: "second-app", TenantId: "t"),
        ]);
        var logger = new CapturingLogger();

        foreach (var sourceKey in SourceKeys)
        {
            var telemetry = new CapturingTelemetrySource();
            await Source(telemetry, catalog, HotelInsights(), logger, profile).FetchAsync(Definition(sourceKey), From, To, 10);
            // The primary row exists and is enabled but has no AppId: InsightsOptions, not the second alias.
            Assert.Equal("insights-hotel-app", Assert.Single(telemetry.Requests).AppInsightsAppId);
        }
        var warning = Assert.Single(logger.Warnings);
        Assert.Contains(alias, warning, StringComparison.Ordinal);
        Assert.Contains("AppId", warning, StringComparison.Ordinal);

        // Not configured in InsightsOptions either: unavailable.
        var unavailable = await Assert.ThrowsAsync<KpiSourceUnavailableException>(() =>
            Source(new CapturingTelemetrySource(), catalog, new InsightsOptions(), profile).FetchAsync(Definition(), From, To, 10));
        Assert.Contains("has no AppId", unavailable.Message, StringComparison.Ordinal);

        // A row with AppId but no TenantId also takes the InsightsOptions target when it is configured.
        var tenantless = new CapturingTelemetrySource();
        await Source(tenantless, new CatalogStore(
            [new(alias, InsightsEnvironment.Production, TelemetrySourceKind.AppInsights, "/res/" + alias, AppId: "row-app")]),
            new InsightsOptions { Hotels = new HotelInsightsOptions { AppInsightsAppId = "insights-hotel-app", TenantId = "99999999-8888-7777-6666-555555555555" } },
            logger, profile).FetchAsync(Definition(), From, To, 10);
        Assert.Equal("insights-hotel-app", Assert.Single(tenantless.Requests).AppInsightsAppId);
        Assert.Equal("99999999-8888-7777-6666-555555555555", tenantless.Requests[0].TenantId);
        Assert.Equal(2, logger.Warnings.Count);
        Assert.Contains("TenantId", logger.Warnings[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task KpiSource_FallsBackToInsightsOptionsForIncumbent()
    {
        // A4: an incumbent service (air) that declares no telemetry source alias.
        var air = Service("air", [], [], ("02-flight-search", "02 Flight Search", 50, "flightsearch"));
        var profile = Incumbent() with { Services = [.. Incumbent().Services.Where(s => s.Key != "air"), air] };
        var catalog = new CatalogStore(DefaultCatalog());
        var insights = new InsightsOptions
        {
            Air = new AirInsightsOptions { AppInsightsAppId = "insights-air-app", TenantId = "11111111-2222-3333-4444-555555555555" },
            Hotels = new HotelInsightsOptions { AppInsightsAppId = "insights-hotel-app" },
        };
        var source = Source(new CapturingTelemetrySource(), catalog, insights, profile);
        var hotelsCatalog = new CatalogStore(
            [new("web-front", InsightsEnvironment.Production, TelemetrySourceKind.AppInsights, "/res/web-front", AppId: "hotel-app", TenantId: "hotel-tenant")]);

        var (appId, tenantId) = await source.ResolveQueryTargetAsync(Definition(domain: "air"), air);
        Assert.Equal("insights-air-app", appId);
        Assert.Equal("11111111-2222-3333-4444-555555555555", tenantId);
        // The catalog is not consulted for a service without aliases.
        Assert.Equal(0, catalog.Reads);

        // A service WITH a complete catalog row never uses InsightsOptions, even when they configure the domain.
        var hotels = profile.FindService("hotels")!;
        var (hotelApp, hotelTenant) = await Source(new CapturingTelemetrySource(), hotelsCatalog, insights, profile)
            .ResolveQueryTargetAsync(Definition(domain: "hotels"), hotels);
        Assert.Equal("hotel-app", hotelApp);
        Assert.Equal("hotel-tenant", hotelTenant);
        Assert.Equal(1, hotelsCatalog.Reads);

        // End to end, the alias-backed service queries the catalog app.
        var telemetry = new CapturingTelemetrySource();
        await Source(telemetry, hotelsCatalog, insights, profile).FetchAsync(Definition(domain: "hotels"), From, To, 10);
        Assert.Equal("hotel-app", Assert.Single(telemetry.Requests).AppInsightsAppId);
    }

    [Fact]
    public async Task KpiSource_NoAlias_IsUnavailable()
    {
        foreach (var sourceKey in SourceKeys)
        {
            var telemetry = new CapturingTelemetrySource();

            // No alias and no InsightsOptions for the domain.
            var noAlias = Incumbent() with
            {
                Services = [Service("air", [], [], ("02-flight-search", "02 Flight Search", 50, "flightsearch"))],
            };
            var catalog = new CatalogStore(DefaultCatalog());
            var unconfigured = await Assert.ThrowsAsync<KpiSourceUnavailableException>(() =>
                Source(telemetry, catalog, new InsightsOptions(), noAlias).FetchAsync(Definition(sourceKey, domain: "air"), From, To, 10));
            Assert.Contains("no telemetry source alias", unconfigured.Message, StringComparison.Ordinal);
            Assert.Equal(0, catalog.Reads);

            // An alias the catalog does not register: never falls back to InsightsOptions.
            var unregistered = await Assert.ThrowsAsync<KpiSourceUnavailableException>(() =>
                Source(telemetry, new CatalogStore(DefaultCatalog()), HotelInsights(), Multi())
                    .FetchAsync(Definition(sourceKey, profileKey: "acme"), From, To, 10));
            Assert.Contains("acme-primary", unregistered.Message, StringComparison.Ordinal);

            // Registered only for another environment.
            var otherEnv = new CatalogStore(
                [new("acme-primary", InsightsEnvironment.Staging, TelemetrySourceKind.AppInsights, "/res/x", AppId: "uat")]);
            await Assert.ThrowsAsync<KpiSourceUnavailableException>(() =>
                Source(telemetry, otherEnv, HotelInsights(), Multi()).FetchAsync(Definition(sourceKey, profileKey: "acme"), From, To, 10));

            // Disabled, or without an app id: the primary alias is not skipped for the second one.
            var disabled = new CatalogStore(
            [
                new("acme-primary", InsightsEnvironment.Production, TelemetrySourceKind.AppInsights, "/res/p", AppId: "p", Enabled: false),
                new("acme-secondary", InsightsEnvironment.Production, TelemetrySourceKind.AppInsights, "/res/s", AppId: "s"),
            ]);
            var disabledEx = await Assert.ThrowsAsync<KpiSourceUnavailableException>(() =>
                Source(telemetry, disabled, HotelInsights(), Multi()).FetchAsync(Definition(sourceKey, profileKey: "acme"), From, To, 10));
            Assert.Contains("disabled", disabledEx.Message, StringComparison.Ordinal);

            // Without an AppId a non-incumbent row is unavailable (FX1: InsightsOptions is the incumbent's alone).
            var noAppId = new CatalogStore(
                [new("acme-primary", InsightsEnvironment.Production, TelemetrySourceKind.AppInsights, "/res/p")]);
            var noAppIdEx = await Assert.ThrowsAsync<KpiSourceUnavailableException>(() =>
                Source(telemetry, noAppId, HotelInsights(), Multi()).FetchAsync(Definition(sourceKey, profileKey: "acme"), From, To, 10));
            Assert.Equal("No telemetry source configured for service 'hotels' of profile 'acme'.", noAppIdEx.Message);

            // A catalog that cannot be read.
            await Assert.ThrowsAsync<KpiSourceUnavailableException>(() =>
                Source(telemetry, new FailingCatalog(), HotelInsights(), Multi()).FetchAsync(Definition(sourceKey, profileKey: "acme"), From, To, 10));

            Assert.Empty(telemetry.Requests);
        }
    }

    private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger<AppInsightsKpiSource>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == Microsoft.Extensions.Logging.LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }
    }

    private sealed class FailingCatalog : ITelemetrySourceStore
    {
        public Task<IReadOnlyList<TelemetrySourceEntry>> GetAllAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException("catalog offline");

        public Task<TelemetrySourceEntry> UpsertAsync(TelemetrySourceEntry source, string changedBy, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DeleteAsync(string alias, string environment, string changedBy, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<TelemetrySourceAuditEntry>> GetAuditAsync(int top, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
