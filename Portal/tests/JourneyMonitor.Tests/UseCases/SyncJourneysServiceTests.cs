using System.Text.Json;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Filtering;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Core.UseCases;
using JourneyMonitor.Infrastructure.Caching;
using JourneyMonitor.Infrastructure.Kql;
using Microsoft.Extensions.Options;
using JourneyMonitor.Tests.Fakes;

namespace JourneyMonitor.Tests.UseCases;

public class SyncJourneysServiceTests
{
    private static readonly BrandClub[] Brands =
    [
        new("T-Mobile", "15639", "308", "stays.tmobiletravel.com"),
        new("WeSalute", "15827", "323", "book.wesalute.com"),
        new("AARP Go", "15893", "333", "members.aarparrivia.com"),
        new("AARP Go", "16108", "333", "members.aarparrivia.com"),
    ];

    [Fact]
    public async Task TimerStoresEveryCarClubFromBrandInformation()
    {
        var store = new RecordingJourneyStore();
        var service = Build(store, new StaticBrandCatalog(Brands), "cars");

        var passes = await service.SyncAllAsync(CancellationToken.None);

        var stored = passes.Where(p => p.Stored).Select(p => p.ClubId).ToList();
        Assert.Equal(["15639", "15827", "15893", "16108"], stored);
        Assert.Equal(
            ["stays.tmobiletravel.com", "book.wesalute.com", "members.aarparrivia.com", "members.aarparrivia.com"],
            store.Requests.Select(r => r.Host));
        Assert.All(store.Requests, r => Assert.Equal("cars", r.Domain));
    }

    [Fact]
    public async Task TimerCollapsesAirAndCruiseClubsToThePartnerId()
    {
        var store = new RecordingJourneyStore();
        var service = Build(store, new StaticBrandCatalog(Brands), "air");

        var passes = await service.SyncAllAsync(CancellationToken.None);

        var stored = passes.Where(p => p.Stored).Select(p => p.ClubId).ToList();
        Assert.Equal(["308", "323", "333"], stored);
    }

    [Fact]
    public async Task EmptyQueryKeepsTheOtherClubs()
    {
        var store = new RecordingJourneyStore();
        var telemetry = new ScriptedTelemetry(skipWhenKqlContains: "15827");
        var service = Build(store, new StaticBrandCatalog(Brands), "cars", telemetry);

        var passes = await service.SyncAllAsync(CancellationToken.None);

        var skipped = passes.Single(p => p.ClubId == "15827");
        Assert.False(skipped.Stored);
        Assert.Equal("query returned no rows", skipped.SkipReason);
        Assert.Equal(["15639", "15893", "16108"], passes.Where(p => p.Stored).Select(p => p.ClubId));
    }

    [Fact]
    public async Task SyncDomainAsyncQueriesEachVerticalOnceAndUpsertsByClub()
    {
        var store = new RecordingJourneyStore();
        var telemetry = new ScriptedTelemetry(skipWhenKqlContains: null);
        var service = Build(store, new StaticBrandCatalog(Brands), "hotels,cruises", telemetry);

        var hotelPasses = await service.SyncDomainAsync("hotels", CancellationToken.None);

        Assert.Equal(3, telemetry.QueryCount);
        Assert.Single(telemetry.Kqls, kql => kql.Contains("clubByUser", StringComparison.Ordinal));
        Assert.Equal(2, telemetry.Kqls.Count(kql => kql.Contains("Host in (tmoHosts)", StringComparison.Ordinal)));
        Assert.Equal(["15639", "15827"], hotelPasses.Where(p => p.Stored).Select(p => p.ClubId));
        Assert.All(store.Requests, r => Assert.Equal("hotels", r.Domain));
        Assert.Contains(hotelPasses, p => p.ClubId == "15893" && p.SkipReason == "query returned no rows");
        Assert.DoesNotContain(store.Requests.SelectMany(r => r.Rows), row => row.Extra.ContainsKey("ClubId"));

        store.Requests.Clear();
        telemetry.Reset();
        var cruisePasses = await service.SyncDomainAsync("cruises", CancellationToken.None);

        Assert.Equal(3, telemetry.QueryCount);
        Assert.Single(telemetry.Kqls, kql => kql.Contains("partnerBySession", StringComparison.Ordinal));
        Assert.Equal(["308", "323"], cruisePasses.Where(p => p.Stored).Select(p => p.ClubId));
        Assert.All(store.Requests, r => Assert.Equal("cruises", r.Domain));
        Assert.Contains(cruisePasses, p => p.ClubId == "333" && p.SkipReason == "query returned no rows");

        store.Requests.Clear();
        var skipped = await service.SyncDomainAsync("air", CancellationToken.None);
        var pass = Assert.Single(skipped);
        Assert.False(pass.Stored);
        Assert.Equal("JourneySync:Domains does not include 'air'.", pass.SkipReason);
        Assert.Empty(store.Requests);
    }

    [Fact]
    public async Task StagingTimerQueriesTheKnownStagingHosts()
    {
        var store = new RecordingJourneyStore();
        var telemetry = new ScriptedTelemetry(skipWhenKqlContains: null, hotelClubIds: ["15707"]);
        var catalog = new StaticBrandCatalog(
        [
            new BrandClub("T-Mobile", "15639", "308", "stays.tmobiletravel.com"),
            new BrandClub("Synchrony Travel", "15039", "306", "travel.mysynchronytravel.saveonuat.com"),
        ]);
        var service = Build(store, catalog, "hotels,cruises", telemetry, environment: "staging");

        await service.SyncDomainAsync("hotels", CancellationToken.None);

        var hotelJourneys = telemetry.Kqls.Where(kql => kql.Contains("clubByUser", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, hotelJourneys.Count);
        Assert.Contains(hotelJourneys, kql => kql.Contains("tmobile.saveonuat.com", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(hotelJourneys, kql => kql.Contains("travel.mysynchronytravel.saveonuat.com", StringComparison.OrdinalIgnoreCase));
        Assert.All(hotelJourneys, kql => Assert.DoesNotContain("Club in (tmoClubs)", kql, StringComparison.Ordinal));
        Assert.Contains(store.Requests, r => r.ClubId == "15639" && r.Host == "tmobile.saveonuat.com");
        Assert.Contains(store.Requests, r => r.ClubId == "15039" && r.Host == "travel.mysynchronytravel.saveonuat.com");
        Assert.DoesNotContain(store.Requests, r => r.ClubId == "15707");

        store.Requests.Clear();
        telemetry.Reset();
        await service.SyncDomainAsync("cruises", CancellationToken.None);

        var cruiseJourney = Assert.Single(telemetry.Kqls, kql => kql.Contains("partnerBySession", StringComparison.Ordinal));
        Assert.Contains("uat-cruise-web.ovscruise.com", cruiseJourney, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ChartWriteFailureStaysOnThatClub()
    {
        var store = new RecordingJourneyStore();
        var metrics = new FailingMetricStore("15827");
        var service = Build(store, new StaticBrandCatalog(Brands), "hotels", metricStore: metrics);

        var passes = await service.SyncDomainAsync("hotels", CancellationToken.None);

        Assert.Contains(metrics.ClubIds, id => id == "15639");
        Assert.Contains(metrics.ClubIds, id => id == "15893");
        Assert.Equal(2, passes.Single(p => p.ClubId == "15827").DashboardMetrics!.Count(m => m.SkipReason == "chart write failed"));
        Assert.All(
            passes.Where(p => p.ClubId != "15827").SelectMany(p => p.DashboardMetrics!),
            m => Assert.True(m.Stored));
    }

    [Fact]
    public async Task MissingBrandCatalogFallsBackToTheConfiguredClub()
    {
        var store = new RecordingJourneyStore();
        var service = Build(store, new UnconfiguredBrandCatalog(), "cars");

        var passes = await service.SyncAllAsync(CancellationToken.None);

        Assert.Contains(passes, p => p.SkipReason == "brand.Information is not configured.");
        var stored = Assert.Single(passes, p => p.Stored);
        Assert.Equal("15639", stored.ClubId);
    }

    private static SyncJourneysService Build(
        RecordingJourneyStore store,
        IBrandCatalog brands,
        string domain,
        ScriptedTelemetry? telemetry = null,
        IDashboardMetricStore? metricStore = null,
        string environment = "production")
    {
        telemetry ??= new ScriptedTelemetry(skipWhenKqlContains: null);
        metricStore ??= new UnusedMetricStore();
        var insights = Options.Create(new InsightsOptions
        {
            AppInsightsAppId = "hotel-app",
            Hotels = new HotelInsightsOptions
            {
                TenantId = "60396ff5-e90f-4bc5-aced-a4f462f2636e",
                AppInsightsAppId = "hotel-app",
                Staging = new DomainInsightsSlot { AppInsightsAppId = "hotel-staging-app" },
            },
            Air = new AirInsightsOptions
            {
                TenantId = "b0298d22-1db0-4550-8fa6-a116a5ab4208",
                AppInsightsAppId = "air-app",
            },
            Cruises = new CruiseInsightsOptions
            {
                TenantId = "60396ff5-e90f-4bc5-aced-a4f462f2636e",
                AppInsightsAppId = "cruise-app",
                Staging = new CruiseStagingSlot { AppInsightsAppId = "cruise-staging-app" },
            },
        });
        var cache = new InMemoryTelemetryQueryCache();
        var journeys = new GetJourneysService(
            telemetry,
            cache,
            new KqlQueryTextProvider(),
            new FakeKqlSourceResolver(),
            insights,
            new EmptyDemoFixtures(),
            new ClubHostResolver(telemetry, cache, insights, brands),
            Options.Create(new JourneySyncOptions { Domains = domain, Window = "15m", Environment = environment }));
        var dashboard = new SyncDashboardMetricsService(
            telemetry,
            new KqlQueryTextProvider(),
            new FakeKqlSourceResolver(),
            metricStore,
            new ClubHostResolver(telemetry, cache, insights, brands),
            insights);

        return new SyncJourneysService(
            journeys,
            store,
            dashboard,
            brands,
            Options.Create(new JourneySyncOptions { Domains = domain, Window = "15m", Environment = environment }));
    }

    private sealed class StaticBrandCatalog(IReadOnlyList<BrandClub> rows) : IBrandCatalog
    {
        public bool IsConfigured => true;

        public Task<IReadOnlyList<BrandClub>> GetAllAsync(CancellationToken cancellationToken) =>
            Task.FromResult(rows);
    }

    private sealed class ScriptedTelemetry(string? skipWhenKqlContains, string[]? hotelClubIds = null) : ITelemetrySource
    {
        public int QueryCount { get; private set; }
        public List<string> Kqls { get; } = [];

        public void Reset()
        {
            QueryCount = 0;
            Kqls.Clear();
        }

        public Task<IReadOnlyList<TelemetryJourneyRow>> QueryAsync(
            TelemetryQueryRequest request,
            CancellationToken cancellationToken)
        {
            QueryCount++;
            Kqls.Add(request.Kql);
            if (skipWhenKqlContains is not null
                && request.Kql.Contains(skipWhenKqlContains, StringComparison.Ordinal))
            {
                return Task.FromResult<IReadOnlyList<TelemetryJourneyRow>>([]);
            }

            if (request.Kql.Contains("clubByUser", StringComparison.Ordinal))
            {
                var ids = hotelClubIds ?? ["15639", "15827"];
                return Task.FromResult<IReadOnlyList<TelemetryJourneyRow>>(ids.Select(Row).ToArray());
            }

            if (request.Kql.Contains("partnerBySession", StringComparison.Ordinal))
            {
                return Task.FromResult<IReadOnlyList<TelemetryJourneyRow>>([Row("308"), Row("323")]);
            }

            var row = new TelemetryJourneyRow();
            row.Extra["UserId"] = JsonSerializer.SerializeToElement("member-1");
            return Task.FromResult<IReadOnlyList<TelemetryJourneyRow>>([row]);
        }

        private static TelemetryJourneyRow Row(string clubId)
        {
            var row = new TelemetryJourneyRow();
            row.Extra["UserId"] = JsonSerializer.SerializeToElement("member-" + clubId);
            row.Extra["ClubId"] = JsonSerializer.SerializeToElement(clubId);
            return row;
        }
    }

    private sealed class RecordingJourneyStore : IJourneyStore
    {
        public List<JourneyUpsertRequest> Requests { get; } = [];

        public Task<JourneyUpsertResult> UpsertAsync(JourneyUpsertRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new JourneyUpsertResult(Guid.NewGuid(), request.Rows.Count, request.Rows.Count));
        }

        public Task<StoredJourneys?> GetLatestAsync(string domain, CancellationToken cancellationToken) =>
            Task.FromResult<StoredJourneys?>(null);
    }

    private sealed class FailingMetricStore(string failingClubId) : IDashboardMetricStore
    {
        public List<string?> ClubIds { get; } = [];

        public Task ReplaceChartAsync(
            string domain,
            string chart,
            string window,
            string? environment,
            string? clubId,
            IReadOnlyList<DashboardMetricPoint> points,
            CancellationToken cancellationToken)
        {
            ClubIds.Add(clubId);
            if (clubId == failingClubId)
            {
                throw new InvalidOperationException("chart write failed");
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<DashboardMetricPoint>> GetAsync(
            string? domain,
            string? chart,
            DateTimeOffset? since,
            string? environment,
            string? clubId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DashboardMetricPoint>>([]);
    }

    private sealed class UnusedMetricStore : IDashboardMetricStore
    {
        public Task ReplaceChartAsync(
            string domain,
            string chart,
            string window,
            string? environment,
            string? clubId,
            IReadOnlyList<DashboardMetricPoint> points,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<DashboardMetricPoint>> GetAsync(
            string? domain,
            string? chart,
            DateTimeOffset? since,
            string? environment,
            string? clubId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DashboardMetricPoint>>([]);
    }

    private sealed class EmptyDemoFixtures : IDemoJourneyFixtureProvider
    {
        public bool HasSnapshot => false;

        public Task<JourneysResult> LoadDemoAsync(string win, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("no demo");

        public Task<JourneysResult> LoadSnapshotAsync(string win, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("no snapshot");
    }
}
