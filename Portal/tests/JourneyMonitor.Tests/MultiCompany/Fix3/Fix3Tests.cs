using System.Text.Json;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Filtering;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Core.UseCases;
using JourneyMonitor.Infrastructure.Caching;
using JourneyMonitor.Infrastructure.Kql;
using JourneyMonitor.Tests.Fakes;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Tests.MultiCompany.Fix3;

/// <summary>
/// FXM3 (review finding M4): with the demo stack's fake App Insights the incumbent's legacy journey
/// sync (hotel/car/cruise tables) queried with the production KQL, which filters on T-Mobile hosts,
/// catalog club ids and customDimensions.UserId that the demo packs never emit, so it stored nothing.
/// The sync now reads the demo-shaped journey queries when Insights:QueryEndpoint points at the fake.
/// </summary>
public class Fix3Tests
{
    private const string FakeEndpoint = "http://fake-appinsights:8080";

    private static readonly BrandClub[] Brands =
    [
        new("T-Mobile", "15639", "308", "stays.tmobiletravel.com"),
        new("WeSalute", "15827", "323", "book.wesalute.com"),
        new("AARP Go", "15893", "333", "members.aarparrivia.com"),
    ];

    [Theory]
    [InlineData("hotels")]
    [InlineData("cars")]
    [InlineData("cruises")]
    public async Task LegacyJourneySync_RunsAgainstDemoTelemetry(string domain)
    {
        // Club ids the demo pack stamps on its telemetry (VacationClubId); none is in the brand catalog.
        string[] telemetryClubs = ["9001", "9002"];
        var telemetry = new DemoShapedTelemetry(telemetryClubs);
        var store = new RecordingJourneyStore();
        var service = Build(store, telemetry, domain, FakeEndpoint);

        var passes = await service.SyncDomainAsync(domain, CancellationToken.None);

        var kql = Assert.Single(telemetry.JourneyKqls);
        Assert.Contains("let win = 15m;", kql, StringComparison.Ordinal);
        Assert.Contains("let rowCap = 200;", kql, StringComparison.Ordinal);
        Assert.Contains("ClubId = tostring(customDimensions.VacationClubId)", kql, StringComparison.Ordinal);
        Assert.Contains("session_Id", kql, StringComparison.Ordinal);
        Assert.DoesNotContain("tmoHost", kql, StringComparison.Ordinal);
        Assert.DoesNotContain("tostring(customDimensions.UserId)", kql, StringComparison.Ordinal);

        // Every telemetry club is stored, so the Overview and the Club filter (which lists the same
        // ids) read rows; the catalog clubs the telemetry never carries are not reported as empty.
        Assert.Equal(telemetryClubs, passes.Select(p => p.ClubId));
        Assert.All(passes, pass => Assert.True(pass.Stored));
        Assert.Equal(telemetryClubs, store.Requests.Select(r => r.ClubId));
        Assert.All(store.Requests, request =>
        {
            Assert.Equal(domain, request.Domain);
            Assert.Equal("live", request.Source);
            Assert.Null(request.Host);
            Assert.NotEmpty(request.Rows);
        });
        Assert.DoesNotContain(store.Requests.SelectMany(r => r.Rows), row => row.Extra.ContainsKey("ClubId"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://api.applicationinsights.io")]
    [InlineData("https://api.applicationinsights.io/")]
    public async Task LegacyJourneySync_KeepsTheProductionKqlForTheRealApi(string? endpoint)
    {
        var telemetry = new DemoShapedTelemetry(["15639", "15827"]);
        var service = Build(new RecordingJourneyStore(), telemetry, "hotels", endpoint);

        await service.SyncDomainAsync("hotels", CancellationToken.None);

        var kql = Assert.Single(telemetry.Kqls, k => k.Contains("clubByUser", StringComparison.Ordinal));
        Assert.Contains("url has_any (tmoHosts)", kql, StringComparison.Ordinal);
        Assert.DoesNotContain("session_Id", kql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("  ", false)]
    [InlineData("https://api.applicationinsights.io", false)]
    [InlineData("HTTPS://API.APPLICATIONINSIGHTS.IO/", false)]
    [InlineData(FakeEndpoint, true)]
    [InlineData("http://localhost:8086/", true)]
    public void DemoTelemetry_IsDetectedFromTheQueryEndpoint(string? endpoint, bool expected)
    {
        Assert.Equal(expected, GetJourneysService.UsesDemoTelemetry(new InsightsOptions { QueryEndpoint = endpoint }));
    }

    [Theory]
    [InlineData("hotels", "UserId, Outcome, LastStep, Steps, FailReason,", "CallsNoCorrelation")]
    [InlineData("cars", "CorrelationId, UserId, Outcome, LastStep, Steps, FailReason,", "BookedReservations")]
    [InlineData("cruises", "UserId, CorrelationId, SessionCount, SessionId, Outcome,", "FrontendEnd")]
    public void DemoJourneyKql_KeepsTheLegacyRowContract(string domain, string projectHead, string column)
    {
        var raw = new KqlQueryTextProvider().GetByKey(GetJourneysService.DemoJourneyKey(domain));

        Assert.Contains("| project " + projectHead, raw, StringComparison.Ordinal);
        Assert.Contains(column, raw, StringComparison.Ordinal);
        Assert.Contains("FirstSeen, LastSeen", raw, StringComparison.Ordinal);
        Assert.Contains("Journey, ClubId", raw, StringComparison.Ordinal);
        Assert.Contains("_Rank <= rowCap", raw, StringComparison.Ordinal);
        // Azure Logs treats a blank line as a query separator.
        Assert.DoesNotContain("\n\n", raw.Replace("\r\n", "\n").TrimEnd(), StringComparison.Ordinal);
    }

    [Fact]
    public void DemoJourneyKql_InjectionFillsWindowAndCap_AndRejectsUnsafeInput()
    {
        var raw = new KqlQueryTextProvider().GetByKey(GetJourneysService.DemoJourneyKey("hotels"));

        var kql = GetJourneysService.InjectDemoVerticalJourneyKql(raw, "1h", 50);

        Assert.Contains("let win = 1h;", kql, StringComparison.Ordinal);
        Assert.Contains("let rowCap = 50;", kql, StringComparison.Ordinal);
        Assert.Contains("let rowCap = 200;", GetJourneysService.InjectDemoVerticalJourneyKql(raw, "15m", 0), StringComparison.Ordinal);
        Assert.Throws<ClubFilterException>(() => GetJourneysService.InjectDemoVerticalJourneyKql(raw, "1h; drop", 50));
        Assert.Throws<ClubFilterException>(() => GetJourneysService.InjectDemoVerticalJourneyKql("print 1", "1h", 50));
    }

    private static SyncJourneysService Build(RecordingJourneyStore store, ITelemetrySource telemetry, string domain, string? endpoint)
    {
        var insights = Options.Create(new InsightsOptions
        {
            QueryEndpoint = endpoint,
            AppInsightsAppId = "demo-tourism-sor-frontend",
            Hotels = new HotelInsightsOptions { AppInsightsAppId = "demo-tourism-sor-frontend" },
            Cruises = new CruiseInsightsOptions { AppInsightsAppId = "demo-tourism-cruise-core" },
        });
        var brands = new StaticBrandCatalog(Brands);
        var cache = new InMemoryTelemetryQueryCache();
        var syncOptions = Options.Create(new JourneySyncOptions { Domains = domain, Window = "15m", Environment = "production" });
        var journeys = new GetJourneysService(
            telemetry,
            cache,
            new KqlQueryTextProvider(),
            new FakeKqlSourceResolver(),
            insights,
            new NoDemoFixtures(),
            new ClubHostResolver(telemetry, cache, insights, brands),
            syncOptions);
        var dashboard = new SyncDashboardMetricsService(
            telemetry,
            new KqlQueryTextProvider(),
            new FakeKqlSourceResolver(),
            new NullMetricStore(),
            new ClubHostResolver(telemetry, cache, insights, brands),
            insights);
        return new SyncJourneysService(journeys, store, dashboard, brands, syncOptions);
    }

    /// <summary>
    /// Answers like the fake App Insights over the demo pack: only the demo-shaped journey query
    /// (it carries rowCap) returns journeys; the production text finds nothing.
    /// </summary>
    private sealed class DemoShapedTelemetry(string[] clubIds) : ITelemetrySource
    {
        public List<string> Kqls { get; } = [];

        public IEnumerable<string> JourneyKqls => Kqls.Where(k => k.Contains("let rowCap", StringComparison.Ordinal));

        public Task<IReadOnlyList<TelemetryJourneyRow>> QueryAsync(TelemetryQueryRequest request, CancellationToken cancellationToken)
        {
            Kqls.Add(request.Kql);
            if (!request.Kql.Contains("let rowCap", StringComparison.Ordinal))
            {
                return Task.FromResult<IReadOnlyList<TelemetryJourneyRow>>([]);
            }

            return Task.FromResult<IReadOnlyList<TelemetryJourneyRow>>(clubIds.Select(Row).ToArray());
        }

        private static TelemetryJourneyRow Row(string clubId)
        {
            var row = new TelemetryJourneyRow();
            row.Extra["UserId"] = JsonSerializer.SerializeToElement("u" + clubId);
            row.Extra["CorrelationId"] = JsonSerializer.SerializeToElement("61e6382d04b4a718-" + clubId);
            row.Extra["Outcome"] = JsonSerializer.SerializeToElement("Dropped at step 2");
            row.Extra["LastSeen"] = JsonSerializer.SerializeToElement(DateTimeOffset.UtcNow);
            row.Extra["ClubId"] = JsonSerializer.SerializeToElement(clubId);
            return row;
        }
    }

    private sealed class StaticBrandCatalog(IReadOnlyList<BrandClub> rows) : IBrandCatalog
    {
        public bool IsConfigured => true;

        public Task<IReadOnlyList<BrandClub>> GetAllAsync(CancellationToken cancellationToken) => Task.FromResult(rows);
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

    private sealed class NullMetricStore : IDashboardMetricStore
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
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<DashboardMetricPoint>>([]);
    }

    private sealed class NoDemoFixtures : IDemoJourneyFixtureProvider
    {
        public bool HasSnapshot => false;

        public Task<JourneysResult> LoadDemoAsync(string win, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("no demo");

        public Task<JourneysResult> LoadSnapshotAsync(string win, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("no snapshot");
    }
}
