using System.Net;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring.Daemons;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Functions.Functions;
using JourneyMonitor.Infrastructure.Monitoring;
using JourneyMonitor.Tests.Functions;
using JourneyMonitor.Tests.Monitoring;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Tests.Spec1000.D1;

public sealed class Spec1000DaemonApiTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 10, 15, 0, TimeSpan.Zero);

    private readonly InMemoryDaemonActivityLog _log = new();

    private DaemonStatusReader Reader() => new(
        _log,
        Options.Create(D1Entries.AllPillars()),
        Options.Create(new TriageOptions { Mode = TriageMode.Shadow }),
        Options.Create(new DaemonMonitoringOptions()),
        new D1FixedTimeProvider(Now));

    private DaemonFunctions Function() => new(Reader(), _log, NullLogger<DaemonFunctions>.Instance);

    private static FakeHttpRequestData Request(string pathAndQuery) => new("http://localhost" + pathAndQuery);

    [Fact]
    public async Task DaemonsApi_ReturnsFiveStatuses()
    {
        await _log.AppendAsync(
        [
            D1Entries.Entry(DaemonKeys.ApplicationHealthMonitor, Now.AddSeconds(-20), DaemonActivityKinds.RunStarted),
            D1Entries.Entry(DaemonKeys.ApplicationHealthMonitor, Now.AddSeconds(-10), DaemonActivityKinds.RunCompleted, message: "1 evaluated, 0 triggered, 0 suppressed, 0 skipped"),
        ]);

        var response = await Function().GetDaemonsAsync(Request("/api/daemons"), CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var statuses = response.ReadJson().AsArray();
        Assert.Equal(DaemonKeys.All.Count, statuses.Count);
        Assert.Equal(DaemonKeys.All, statuses.Select(s => s!["daemonKey"]!.GetValue<string>()));
        var app = statuses[1]!;
        Assert.Equal("Application Health Monitor", app["displayName"]!.GetValue<string>());
        Assert.True(app["enabled"]!.GetValue<bool>());
        Assert.Equal(DaemonHealthStates.Ok, app["health"]!.GetValue<string>());
        Assert.Equal("1 evaluated, 0 triggered, 0 suppressed, 0 skipped", app["lastRunSummary"]!.GetValue<string>());
        Assert.NotNull(app["lastRunAt"]);
        Assert.Equal(DaemonHealthStates.NeverRun, statuses[0]!["health"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("?top=0")]
    [InlineData("?top=1001")]
    [InlineData("?top=abc")]
    [InlineData("?level=x")]
    [InlineData("?level=5")]
    [InlineData("?daemon=x")]
    [InlineData("?afterId=-1")]
    [InlineData("?afterId=abc")]
    public async Task DaemonsActivityApi_InvalidQuery_Returns400(string query)
    {
        var response = await Function().GetDaemonActivityAsync(Request("/api/daemons/activity" + query), CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotEmpty(response.ReadJson()["errors"]!.AsArray());
    }

    [Fact]
    public async Task DaemonsActivityApi_PagesWithAfterId()
    {
        await _log.AppendAsync(Enumerable.Range(1, 10)
            .Select(i => D1Entries.Entry(
                i % 2 == 0 ? DaemonKeys.KpiIngestion : DaemonKeys.TriageMonkey,
                Now.AddSeconds(i),
                i == 9 ? DaemonActivityKinds.Error : DaemonActivityKinds.IngestionKpi,
                i == 9 ? DaemonActivityLevel.Error : DaemonActivityLevel.Info,
                $"m{i}"))
            .ToList());

        var first = (await Function().GetDaemonActivityAsync(Request("/api/daemons/activity?afterId=0&top=4"), CancellationToken.None)).ReadJson();
        Assert.Equal([7L, 8L, 9L, 10L], first["entries"]!.AsArray().Select(e => e!["activityId"]!.GetValue<long>()).ToArray());
        Assert.Equal(10, first["lastId"]!.GetValue<long>());

        var page = await Function().GetDaemonActivityAsync(Request("/api/daemons/activity?afterId=3&top=3"), CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var json = page.ReadJson();
        var ids = json["entries"]!.AsArray().Select(e => e!["activityId"]!.GetValue<long>()).ToArray();
        Assert.Equal([4L, 5L, 6L], ids);
        Assert.Equal(6, json["lastId"]!.GetValue<long>());
        Assert.Equal("m4", json["entries"]![0]!["message"]!.GetValue<string>());

        var none = (await Function().GetDaemonActivityAsync(Request("/api/daemons/activity?afterId=10"), CancellationToken.None)).ReadJson();
        Assert.Empty(none["entries"]!.AsArray());
        Assert.Equal(10, none["lastId"]!.GetValue<long>());

        var filtered = (await Function().GetDaemonActivityAsync(Request("/api/daemons/activity?afterId=0&daemon=TRIAGE-MONKEY&level=warning"), CancellationToken.None)).ReadJson();
        Assert.Equal([9L], filtered["entries"]!.AsArray().Select(e => e!["activityId"]!.GetValue<long>()).ToArray());
    }

    [Fact]
    public async Task GetMonkeys_IncludesDaemonCards()
    {
        await _log.AppendAsync(
        [
            D1Entries.Entry(DaemonKeys.KpiIngestion, Now.AddSeconds(-20), DaemonActivityKinds.RunStarted),
            D1Entries.Entry(DaemonKeys.KpiIngestion, Now.AddSeconds(-10), DaemonActivityKinds.RunCompleted, message: "Up to date — nothing to ingest"),
        ]);

        var function = new GetMonkeysFunction(
            new InMemoryFindingsRepository(),
            new InMemoryDoctorReportsRepository(),
            new InMemoryNotificationLogRepository(),
            new InMemoryInvestigationPackageRepository(),
            new InMemoryScoutRunRepository(),
            new InMemoryForensicCaseRepository(),
            new InMemoryRcaReportRepository(),
            new InMemoryAgentWorkQueue(),
            FakeAgentPauseResolver.NothingPaused(),
            Options.Create(new InspectorOptions()),
            Options.Create(new RcaOptions()),
            new InMemoryMonkeyRunTracker(),
            NullLogger<GetMonkeysFunction>.Instance,
            Reader());

        var response = await function.RunAsync(new FakeHttpRequestData("https://localhost/api/monkeys"), default);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cards = response.ReadJson().AsArray();
        var ids = cards.Select(c => c!["id"]!.GetValue<string>()).ToList();
        Assert.Equal(13, cards.Count);
        foreach (var existing in new[]
                 {
                     MonkeyKey.LatencyScoutMonkey, MonkeyKey.ExceptionScoutMonkey, MonkeyKey.InspectorMonkey, MonkeyKey.RcaMonkey,
                     MonkeyKey.LatencySpikeMonkey, MonkeyKey.SloBurnRateMonkey, MonkeyKey.DoctorMonkey,
                 })
        {
            Assert.Contains(existing, ids);
        }
        Assert.Equal(DaemonKeys.All, ids.Skip(7));

        var ingestion = cards.Single(c => c!["id"]!.GetValue<string>() == DaemonKeys.KpiIngestion)!;
        Assert.Equal("KPI Ingestion", ingestion["name"]!.GetValue<string>());
        Assert.Equal("active", ingestion["status"]!.GetValue<string>());
        Assert.Equal(DaemonHealthStates.Ok, ingestion["health"]!.GetValue<string>());
        Assert.Equal(Now.AddSeconds(-10), DateTimeOffset.Parse(ingestion["lastRun"]!.GetValue<string>()));
        Assert.Equal("Up to date — nothing to ingest", ingestion["lastRunSummary"]!.GetValue<string>());
        Assert.NotNull(ingestion["guardrails"]);
        Assert.NotNull(ingestion["activity"]);
    }
}
