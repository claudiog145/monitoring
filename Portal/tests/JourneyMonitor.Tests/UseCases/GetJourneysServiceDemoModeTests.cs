using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.UseCases;
using JourneyMonitor.Infrastructure.Caching;
using JourneyMonitor.Infrastructure.Demo;
using JourneyMonitor.Infrastructure.Kql;
using JourneyMonitor.Tests.Fakes;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Tests.UseCases;

/// <summary>
/// A telemetry source that always fails, forcing GetJourneysService down the demo-fixture
/// fallback path exactly like a real "no az login" / no App Insights access scenario.
/// </summary>
internal sealed class AlwaysFailingTelemetrySource : ITelemetrySource
{
    public Task<IReadOnlyList<TelemetryJourneyRow>> QueryAsync(TelemetryQueryRequest request, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("No Azure credentials available in this test.");
}

/// <summary>A brand catalog with no database behind it, i.e. a fresh clone without JourneyStore:ConnectionString.</summary>
internal sealed class UnconfiguredBrandCatalog : IBrandCatalog
{
    public bool IsConfigured => false;

    public Task<IReadOnlyList<BrandClub>> GetAllAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<BrandClub>>(Array.Empty<BrandClub>());
}

/// <summary>
/// End-to-end characterization test of the "forced demo mode" path in GetJourneysService -
/// direct port of the loadDemo()-driven branch of handleJourneysApi in server.js. Uses the
/// real anonymized demo-journeys.json fixture shipped in JourneyMonitor.Infrastructure.
/// </summary>
public class GetJourneysServiceDemoModeTests
{
    private static string DemoFixturePath => Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "JourneyMonitor.Infrastructure", "Demo", "Data", "demo-journeys.json");

    private static GetJourneysService BuildService()
    {
        var demoOptions = Options.Create(new DemoJourneyFixtureOptions { DemoPath = DemoFixturePath, SnapshotPath = "no-such-file.json" });
        var demoProvider = new DemoJourneyFixtureProvider(demoOptions);
        var cache = new InMemoryTelemetryQueryCache();
        var kqlProvider = new KqlQueryTextProvider();
        var insightsOptions = Options.Create(new JourneyMonitor.Core.Options.InsightsOptions());
        var clubHostResolver = new ClubHostResolver(new AlwaysFailingTelemetrySource(), cache, insightsOptions, new UnconfiguredBrandCatalog());

        return new GetJourneysService(
            new AlwaysFailingTelemetrySource(),
            cache,
            kqlProvider,
            new FakeKqlSourceResolver(),
            insightsOptions,
            demoProvider,
            clubHostResolver,
            Options.Create(new JourneySyncOptions()));
    }

    [Fact]
    public async Task ForcedDemoModeReturnsTheAnonymizedFixtureForHotelsClub15639()
    {
        var service = BuildService();
        var request = new JourneysRequest(
            Domain: "hotels", Win: null, ClubId: "15639", Environment: null, RowCapRaw: null, Fresh: true, ForcedMode: "demo");

        var outcome = await service.GetAsync(request, CancellationToken.None);

        Assert.Equal(200, outcome.StatusCode);
        Assert.Equal("demo", outcome.Result.Source);
        Assert.Equal("hotels", outcome.Result.Domain);
        Assert.Equal("15639", outcome.Result.ClubId);
        Assert.True(outcome.Result.RowCount > 0);
        Assert.Equal(outcome.Result.RowCount, outcome.Result.Rows.Count);
    }

    [Fact]
    public async Task ForcedDemoModeShiftsTimestampsOntoTheCurrentClock()
    {
        var service = BuildService();
        var request = new JourneysRequest(
            Domain: "hotels", Win: null, ClubId: "15639", Environment: null, RowCapRaw: null, Fresh: true, ForcedMode: "demo");

        var outcome = await service.GetAsync(request, CancellationToken.None);
        var firstRow = outcome.Result.Rows[0];
        var lastSeen = firstRow.GetString("LastSeen");

        Assert.True(DateTimeOffset.TryParse(lastSeen, out var parsed));
        // The fixture's original timeAnchor is months in the past; after shifting it must read
        // as recent (within a day of "now"), exactly like loadDemo()'s shift() closure.
        Assert.True((DateTimeOffset.UtcNow - parsed).Duration() < TimeSpan.FromDays(1));
    }

    [Fact]
    public async Task AirHasNoAnonymizedDemoFixtureYet()
    {
        var service = BuildService();
        var request = new JourneysRequest(
            Domain: "air", Win: null, ClubId: "308", Environment: null, RowCapRaw: null, Fresh: true, ForcedMode: "demo");

        var outcome = await service.GetAsync(request, CancellationToken.None);

        Assert.Equal(502, outcome.StatusCode);
        Assert.Equal("error", outcome.Result.Source);
        Assert.Contains("Air has no anonymized demo fixture yet", outcome.Result.Error);
    }
}
