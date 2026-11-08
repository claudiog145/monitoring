using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Configuration;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring;
using JourneyMonitor.Core.Monitoring.Kpi;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Core.UseCases;
using JourneyMonitor.Infrastructure.AppInsights;
using JourneyMonitor.Infrastructure.Company;
using JourneyMonitor.Infrastructure.Kql;
using JourneyMonitor.Infrastructure.Monitoring;
using JourneyMonitor.Tests.Company;
using JourneyMonitor.Tests.Spec1000.A2;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using static JourneyMonitor.Tests.Spec1000.A2.KpiSourceDoubles;

namespace JourneyMonitor.Tests.Monitoring;

/// <summary>
/// FX1 (review H1 + M2): tenant isolation is enforced when telemetry is READ, not only when a profile
/// is saved. InsightsOptions is the incumbent's fallback alone, and an alias the catalog gives to
/// another profile is unreadable by every resolution path (KPI sources, kql: KPIs and their dry run,
/// the Scout board), including right after a transfer.
/// </summary>
public sealed class TenantIsolationReadTests
{
    private static readonly string[] AppInsightsKeys = [AppInsightsKpiSource.LatencySourceKey, AppInsightsKpiSource.ExceptionsSourceKey];

    private const string KqlQueryKey = "kpi:fx1-purchases";

    // Reads `requests` with no app(): on the target app, which is exactly what the H1 fallback leaked.
    private const string BareTableQuery = """
        let fromTs = {{from}};
        let toTs = {{to}};
        let bucketSize = {{bucket}};
        requests
        | where timestamp >= fromTs and timestamp < toTs
        | summarize TotalCount = sum(itemCount) by BucketStart = bin_at(timestamp, bucketSize, fromTs)
        | project ['kind'] = "bucket", BucketStart, TotalCount, Value = todouble(TotalCount), Grain = "{{grain}}"
        """;

    private const string WebFrontQuery = """
        let fromTs = {{from}};
        let toTs = {{to}};
        let bucketSize = {{bucket}};
        app('{{SOURCE:web-front}}').requests
        | where timestamp >= fromTs and timestamp < toTs
        | summarize TotalCount = sum(itemCount) by BucketStart = bin_at(timestamp, bucketSize, fromTs)
        | project ['kind'] = "bucket", BucketStart, TotalCount, Value = todouble(TotalCount), Grain = "{{grain}}"
        """;

    private static InsightsOptions IncumbentInsights() => new()
    {
        Hotels = new HotelInsightsOptions { AppInsightsAppId = "arrivia-hotel-app", TenantId = "11111111-2222-3333-4444-555555555555" },
        Air = new AirInsightsOptions { AppInsightsAppId = "arrivia-air-app", TenantId = "11111111-2222-3333-4444-555555555555" },
    };

    /// <summary>A second company with a hotels service and NO telemetry source alias.</summary>
    private static CompanyProfile AcmeWithoutAlias() => HostScoped("acme") with
    {
        Services = [Service("hotels", [], ["rooms.acme.test"], ("10-browse", "10 Browse", 1, "/browse"))],
    };

    private static TelemetrySourceEntry Row(string alias, string? owner, string? appId = null, string? tenantId = null) =>
        new(alias, InsightsEnvironment.Production, TelemetrySourceKind.AppInsights, "/res/" + alias,
            AppId: appId, TenantId: tenantId, OwnerProfileKey: owner);

    private static AppInsightsKpiSource KpiSource(
        ITelemetrySource telemetry, ITelemetrySourceStore catalog, InsightsOptions insights,
        TelemetrySourceOwnershipCheck? ownership, params CompanyProfile[] profiles) =>
        new(
            telemetry,
            Options.Create(insights),
            Options.Create(new JourneySyncOptions()),
            new InMemoryCompanyProfileStore(profiles),
            new AliasResolver(),
            catalog,
            null,
            ownership);

    private static async Task<CatalogKqlKpiSource> KqlSourceAsync(
        ITelemetrySource telemetry, ITelemetrySourceStore catalog, string text, params CompanyProfile[] profiles)
    {
        var queries = new InMemoryKqlQueryStore();
        await queries.UpsertAsync(new KqlQueryEntry(KqlQueryKey, text, Description: "FX1", Enabled: true), "test");
        return new CatalogKqlKpiSource(
            telemetry,
            queries,
            new InMemoryCompanyProfileStore(profiles),
            new AliasResolver(),
            Options.Create(new JourneySyncOptions()),
            KpiSource(telemetry, catalog, IncumbentInsights(), null, profiles));
    }

    [Fact]
    public async Task Fallback_InsightsOptions_OnlyForIncumbent()
    {
        foreach (var sourceKey in AppInsightsKeys)
        {
            // The incumbent keeps its fallback: no alias...
            var air = Service("air", [], [], ("02-flight-search", "02 Flight Search", 50, "flightsearch"));
            var incumbent = Incumbent() with { Services = [.. Incumbent().Services.Where(s => s.Key != "air"), air] };
            var (airApp, _) = await KpiSource(new CapturingTelemetrySource(), new CatalogStore([]), IncumbentInsights(), null, incumbent)
                .ResolveQueryTargetAsync(Definition(sourceKey, domain: "air"), air);
            Assert.Equal("arrivia-air-app", airApp);

            // ...and a row without AppId.
            var incompleteTelemetry = new CapturingTelemetrySource();
            await KpiSource(incompleteTelemetry, new CatalogStore([Row("web-front", KpiProfiles.Default)]), IncumbentInsights(), null, Incumbent())
                .FetchAsync(Definition(sourceKey), From, To, 10);
            Assert.Equal("arrivia-hotel-app", Assert.Single(incompleteTelemetry.Requests).AppInsightsAppId);

            // A second company never reads the incumbent's apps through InsightsOptions: not without an alias...
            var telemetry = new CapturingTelemetrySource();
            var noAlias = await Assert.ThrowsAsync<KpiSourceUnavailableException>(() =>
                KpiSource(telemetry, new CatalogStore([]), IncumbentInsights(), null, AcmeWithoutAlias())
                    .FetchAsync(Definition(sourceKey, profileKey: "acme"), From, To, 10));
            Assert.Equal("No telemetry source configured for service 'hotels' of profile 'acme'.", noAlias.Message);

            // ...nor with a catalog row that has no AppId.
            var acme = HostScoped("acme");
            var noAppId = await Assert.ThrowsAsync<KpiSourceUnavailableException>(() =>
                KpiSource(telemetry, new CatalogStore([Row("acme-web", "acme")]), IncumbentInsights(), null, acme)
                    .FetchAsync(Definition(sourceKey, profileKey: "acme"), From, To, 10));
            Assert.Equal("No telemetry source configured for service 'hotels' of profile 'acme'.", noAppId.Message);
            Assert.Empty(telemetry.Requests);

            // A row with AppId and no TenantId is used as is: the incumbent's tenant is not borrowed either.
            var ownApp = new CapturingTelemetrySource();
            await KpiSource(ownApp, new CatalogStore([Row("acme-web", "acme", appId: "acme-app")]), IncumbentInsights(), null, acme)
                .FetchAsync(Definition(sourceKey, profileKey: "acme"), From, To, 10);
            var request = Assert.Single(ownApp.Requests);
            Assert.Equal("acme-app", request.AppInsightsAppId);
            Assert.Null(request.TenantId);
        }

        Assert.True(AppInsightsKpiSource.IsIncumbent(" ARRIVIA "));
        Assert.False(AppInsightsKpiSource.IsIncumbent("acme"));
        Assert.False(AppInsightsKpiSource.IsIncumbent(null));
    }

    [Fact]
    public async Task KqlKpi_NonIncumbentWithoutAlias_Unavailable()
    {
        var telemetry = new CapturingTelemetrySource([KpiSourceDoubles.Row("""{"kind":"bucket","BucketStart":"2026-11-04T14:00:00Z","TotalCount":1,"Value":1.0}""")]);
        var source = await KqlSourceAsync(telemetry, new CatalogStore([]), BareTableQuery, Incumbent(), AcmeWithoutAlias());
        var definition = Definition("kql:" + KqlQueryKey, profileKey: "acme");

        var run = await Assert.ThrowsAsync<KpiSourceUnavailableException>(() => source.FetchAsync(definition, From, To, 10));
        Assert.Equal("No telemetry source configured for service 'hotels' of profile 'acme'.", run.Message);

        // The one-hour dry run of a catalog edit takes the same path.
        var dryRun = await Assert.ThrowsAsync<KpiSourceUnavailableException>(() =>
            source.FetchWithTextAsync(definition, BareTableQuery, From, From.AddHours(1), 10));
        Assert.Equal("No telemetry source configured for service 'hotels' of profile 'acme'.", dryRun.Message);

        // Not one query reached the incumbent's hotels app.
        Assert.Empty(telemetry.Requests);

        // The incumbent's own kql: KPI on a service without alias still uses InsightsOptions.
        var incumbent = Incumbent() with
        {
            Services = [Service("hotels", [], [], ("02-search", "02 Hotel Search", 41, "search.json"))],
        };
        var incumbentSource = await KqlSourceAsync(telemetry, new CatalogStore([]), BareTableQuery, incumbent);
        await incumbentSource.FetchAsync(Definition("kql:" + KqlQueryKey), From, To, 10);
        Assert.Equal("arrivia-hotel-app", Assert.Single(telemetry.Requests).AppInsightsAppId);
    }

    [Fact]
    public async Task Resolution_AliasOwnedByOtherProfile_Unavailable_Kpi()
    {
        // web-front (the incumbent's hotels/cars source) now belongs to acme.
        var foreign = new CatalogStore([Row("web-front", "acme", appId: "hotel-app", tenantId: "t")]);
        const string expected = "Alias 'web-front' belongs to profile 'acme'.";

        foreach (var sourceKey in AppInsightsKeys)
        {
            var telemetry = new CapturingTelemetrySource();
            var ex = await Assert.ThrowsAsync<KpiSourceUnavailableException>(() =>
                KpiSource(telemetry, foreign, IncumbentInsights(), null, Incumbent()).FetchAsync(Definition(sourceKey), From, To, 10));
            Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
            Assert.Empty(telemetry.Requests);
        }

        // Entity grain and the partner dependency KPI resolve through the same path.
        var entityTelemetry = new CapturingTelemetrySource();
        var entity = await Assert.ThrowsAsync<KpiSourceUnavailableException>(() =>
            KpiSource(entityTelemetry, foreign, IncumbentInsights(), null, Incumbent())
                .FetchAsync(Definition(grain: KpiGrain.Entity), From, To, 10));
        Assert.Contains(expected, entity.Message, StringComparison.Ordinal);
        var dependency = await Assert.ThrowsAsync<KpiSourceUnavailableException>(() =>
            KpiSource(entityTelemetry, foreign, IncumbentInsights(), null, Incumbent())
                .FetchAsync(Definition(AppInsightsKpiSource.DependencySourceKey, grain: KpiGrain.Entity), From, To, 10));
        Assert.Contains(expected, dependency.Message, StringComparison.Ordinal);
        Assert.Empty(entityTelemetry.Requests);

        // A second alias of the service owned elsewhere is enough: the query unions every alias.
        var cruises = await Assert.ThrowsAsync<KpiSourceUnavailableException>(() =>
            KpiSource(new CapturingTelemetrySource(),
                new CatalogStore([Row("sea-core", KpiProfiles.Default, appId: "sea", tenantId: "t"), Row("sea-api", "acme")]),
                IncumbentInsights(), null, Incumbent())
                .FetchAsync(Definition(domain: "cruises"), From, To, 10));
        Assert.Contains("Alias 'sea-api' belongs to profile 'acme'.", cruises.Message, StringComparison.Ordinal);

        // kql: KPIs, scheduled and dry run, even though the profile declares the alias.
        var kqlTelemetry = new CapturingTelemetrySource();
        var kql = await KqlSourceAsync(kqlTelemetry, foreign, WebFrontQuery, Incumbent());
        var kqlDefinition = Definition("kql:" + KqlQueryKey);
        Assert.Contains(expected, (await Assert.ThrowsAsync<KpiSourceUnavailableException>(() =>
            kql.FetchAsync(kqlDefinition, From, To, 10))).Message, StringComparison.Ordinal);
        Assert.Contains(expected, (await Assert.ThrowsAsync<KpiSourceUnavailableException>(() =>
            kql.FetchWithTextAsync(kqlDefinition, WebFrontQuery, From, From.AddHours(1), 10))).Message, StringComparison.Ordinal);
        Assert.Empty(kqlTelemetry.Requests);

        // Owned by the reader, or shared (null owner): readable, unchanged.
        foreach (var owner in new string?[] { KpiProfiles.Default, null })
        {
            var ok = new CapturingTelemetrySource();
            await KpiSource(ok, new CatalogStore([Row("web-front", owner, appId: "hotel-app", tenantId: "t")]), IncumbentInsights(), null, Incumbent())
                .FetchAsync(Definition(), From, To, 10);
            Assert.Equal("hotel-app", Assert.Single(ok.Requests).AppInsightsAppId);
        }
    }

    [Fact]
    public async Task Resolution_AliasOwnedByOtherProfile_Unavailable_ScoutBoard()
    {
        var acme = HostScoped("acme");
        var catalog = new MutableCatalog([Row("acme-web", "acme"), Row("acme-billing", "globex")]);
        var logger = new CapturingLogger<ComposedScoutBoardQueryTextSource>();
        var ownership = new TelemetrySourceOwnershipCheck(catalog);
        var telemetry = new CapturingTelemetrySource();

        var service = Board(telemetry, acme, new ComposedScoutBoardQueryTextSource(new InMemoryCompanyProfileStore([acme]), ownership, logger), ownership);
        var result = await service.ReadAsync(BoardRequest());

        // The foreign source is skipped with a Warning; the board still runs on the company's own source.
        var kql = Assert.Single(telemetry.Requests).Kql;
        Assert.Contains("/res/acme-web", kql, StringComparison.Ordinal);
        Assert.DoesNotContain("acme-billing", kql, StringComparison.Ordinal);
        Assert.Equal(kql, result.Kql);
        var warning = Assert.Single(logger.Warnings);
        Assert.Contains("acme-billing", warning, StringComparison.Ordinal);
        Assert.Contains("globex", warning, StringComparison.Ordinal);

        // Every source owned elsewhere: the cycle fails, naming why, and nothing is queried.
        catalog.Entries = [Row("acme-web", "globex"), Row("acme-billing", "globex")];
        ownership.Invalidate();
        var none = await Assert.ThrowsAsync<CompanyProfileResolutionException>(() => service.ReadAsync(BoardRequest()));
        Assert.Contains("no telemetry source it may read", none.Message, StringComparison.Ordinal);
        Assert.Contains("Alias 'acme-web' belongs to profile 'globex'.", none.Message, StringComparison.Ordinal);
        Assert.Single(telemetry.Requests);

        // A fixed board text cannot drop one branch of its union: it refuses to run instead.
        catalog.Entries = [Row("acme-web", "acme"), Row("acme-billing", "globex")];
        ownership.Invalidate();
        var fixedText = "app('{{SOURCE:acme-web}}').requests | union app('{{SOURCE:acme-billing}}').requests";
        var fixedBoard = Board(telemetry, acme, new FixedBoardText(fixedText), ownership);
        var refused = await Assert.ThrowsAsync<KqlSourceResolutionException>(() => fixedBoard.ReadAsync(BoardRequest()));
        Assert.Contains("Alias 'acme-billing' belongs to profile 'globex'.", refused.Message, StringComparison.Ordinal);
        Assert.Single(telemetry.Requests);

        // The incumbent's composed board over sources it owns is untouched: no warning, every alias kept.
        var incumbentLogger = new CapturingLogger<ComposedScoutBoardQueryTextSource>();
        var incumbentCatalog = new MutableCatalog([Row("sea-core", KpiProfiles.Default), Row("sea-api", KpiProfiles.Default), Row("web-front", KpiProfiles.Default), Row("air-front", KpiProfiles.Default)]);
        var incumbentOwnership = new TelemetrySourceOwnershipCheck(incumbentCatalog);
        var incumbentTelemetry = new CapturingTelemetrySource();
        await Board(incumbentTelemetry, Incumbent(),
            new ComposedScoutBoardQueryTextSource(new InMemoryCompanyProfileStore([Incumbent()]), incumbentOwnership, incumbentLogger),
            incumbentOwnership).ReadAsync(BoardRequest());
        var incumbentKql = Assert.Single(incumbentTelemetry.Requests).Kql;
        foreach (var alias in new[] { "sea-core", "sea-api", "web-front", "air-front" })
        {
            Assert.Contains("/res/" + alias, incumbentKql, StringComparison.Ordinal);
        }
        Assert.Empty(incumbentLogger.Warnings);
    }

    [Fact]
    public async Task Resolution_AfterTransfer_OldOwnerLosesAccess()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 11, 7, 8, 0, 0, TimeSpan.Zero));
        var catalog = new MutableCatalog([Row("web-front", KpiProfiles.Default, appId: "hotel-app", tenantId: "t")]);
        var ownership = new TelemetrySourceOwnershipCheck(catalog, clock);
        var acme = HostScoped("acme") with
        {
            Services = [Service("hotels", ["web-front"], ["rooms.acme.test"], ("10-browse", "10 Browse", 1, "/browse"))],
        };
        var telemetry = new CapturingTelemetrySource();
        var source = KpiSource(telemetry, catalog, IncumbentInsights(), ownership, Incumbent(), acme);

        // Before the transfer: arrivia reads, acme (which declares the alias) does not.
        await source.FetchAsync(Definition(), From, To, 10);
        Assert.Single(telemetry.Requests);
        await Assert.ThrowsAsync<KpiSourceUnavailableException>(() => source.FetchAsync(Definition(profileKey: "acme"), From, To, 10));

        // The alias is transferred to acme.
        catalog.Entries = [Row("web-front", "acme", appId: "hotel-app", tenantId: "t")];

        // Within the cache window the snapshot is still the old one (at most CacheTtl)...
        Assert.InRange(TelemetrySourceOwnershipCheck.CacheTtl, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60));
        clock.Advance(TelemetrySourceOwnershipCheck.CacheTtl - TimeSpan.FromSeconds(1));
        await source.FetchAsync(Definition(), From, To, 10);
        Assert.Equal(2, telemetry.Requests.Count);
        Assert.Equal(1, catalog.Reads);

        // ...and on the next cycle, once it has expired, the previous owner has lost access.
        clock.Advance(TimeSpan.FromSeconds(2));
        var lost = await Assert.ThrowsAsync<KpiSourceUnavailableException>(() => source.FetchAsync(Definition(), From, To, 10));
        Assert.Contains("Alias 'web-front' belongs to profile 'acme'.", lost.Message, StringComparison.Ordinal);
        Assert.Equal(2, telemetry.Requests.Count);
        Assert.Equal(2, catalog.Reads);

        // The new owner reads it.
        await source.FetchAsync(Definition(profileKey: "acme"), From, To, 10);
        Assert.Equal(3, telemetry.Requests.Count);

        // Transferred back, with the cache bypassed: arrivia reads again at once, acme no longer does.
        catalog.Entries = [Row("web-front", KpiProfiles.Default, appId: "hotel-app", tenantId: "t")];
        ownership.Invalidate();
        await source.FetchAsync(Definition(), From, To, 10);
        await Assert.ThrowsAsync<KpiSourceUnavailableException>(() => source.FetchAsync(Definition(profileKey: "acme"), From, To, 10));
        Assert.Equal(4, telemetry.Requests.Count);

        // The Scout board follows the same snapshot: after a transfer of acme-billing the old owner's board skips it.
        var acmeBoard = HostScoped("acme");
        catalog.Entries = [Row("acme-web", "acme"), Row("acme-billing", "acme")];
        ownership.Invalidate();
        var boardTelemetry = new CapturingTelemetrySource();
        var board = Board(boardTelemetry, acmeBoard,
            new ComposedScoutBoardQueryTextSource(new InMemoryCompanyProfileStore([acmeBoard]), ownership), ownership);
        await board.ReadAsync(BoardRequest());
        Assert.Contains("/res/acme-billing", boardTelemetry.Requests[^1].Kql, StringComparison.Ordinal);

        catalog.Entries = [Row("acme-web", "acme"), Row("acme-billing", "globex")];
        clock.Advance(TelemetrySourceOwnershipCheck.CacheTtl + TimeSpan.FromSeconds(1));
        await board.ReadAsync(BoardRequest());
        Assert.DoesNotContain("acme-billing", boardTelemetry.Requests[^1].Kql, StringComparison.Ordinal);
        Assert.Contains("/res/acme-web", boardTelemetry.Requests[^1].Kql, StringComparison.Ordinal);
    }

    private static ScoutBoardRequest BoardRequest() => new(
        ScoutBoard.Latency,
        new DateTimeOffset(2026, 11, 7, 6, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 11, 7, 10, 0, 0, TimeSpan.Zero),
        BucketMinutes: 5,
        TakePerClub: 3,
        Vertical: "All",
        ClubFilter: "*");

    private static ScoutBoardService Board(
        ITelemetrySource telemetry, CompanyProfile profile, IScoutBoardQueryTextSource text, TelemetrySourceOwnershipCheck ownership) =>
        new(
            telemetry,
            new KqlQueryTextProvider(),
            new AliasResolver(),
            new FakeCompanyAdapterProvider(profile),
            Options.Create(new InsightsOptions { AppInsightsAppId = "app-id" }),
            new ScoutOptionsMonitor(new ScoutOptions()),
            text,
            ownership);

    private sealed class FixedBoardText(string text) : IScoutBoardQueryTextSource
    {
        public Task<string> GetBoardQueryTextAsync(ScoutBoardQueryTextRequest request, CancellationToken ct = default) =>
            Task.FromResult(text);
    }

    private sealed class ScoutOptionsMonitor(ScoutOptions options) : IOptionsMonitor<ScoutOptions>
    {
        public ScoutOptions CurrentValue => options;

        public ScoutOptions Get(string? name) => options;

        public IDisposable? OnChange(Action<ScoutOptions, string?> listener) => null;
    }

    /// <summary>A catalog whose rows a test rewrites, as a transfer does in SQL.</summary>
    private sealed class MutableCatalog(IReadOnlyList<TelemetrySourceEntry> entries) : ITelemetrySourceStore
    {
        public IReadOnlyList<TelemetrySourceEntry> Entries { get; set; } = entries;

        public int Reads { get; private set; }

        public Task<IReadOnlyList<TelemetrySourceEntry>> GetAllAsync(CancellationToken ct = default)
        {
            Reads++;
            return Task.FromResult(Entries);
        }

        public Task<TelemetrySourceEntry> UpsertAsync(TelemetrySourceEntry source, string changedBy, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DeleteAsync(string alias, string environment, string changedBy, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<TelemetrySourceAuditEntry>> GetAuditAsync(int top, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public void Advance(TimeSpan by) => _now = _now.Add(by);

        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }
    }
}
