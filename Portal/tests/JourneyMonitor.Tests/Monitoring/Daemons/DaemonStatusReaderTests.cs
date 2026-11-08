using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring.Daemons;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Infrastructure.Monitoring;

namespace JourneyMonitor.Tests.Spec1000.D1;

public sealed class DaemonStatusReaderTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 10, 15, 0, TimeSpan.Zero);

    private readonly InMemoryDaemonActivityLog _log = new();
    private HealthMonitoringOptions _health = D1Entries.AllPillars();
    private readonly TriageOptions _triage = new() { Mode = TriageMode.Shadow };
    private readonly DaemonMonitoringOptions _daemon = new() { IngestionSchedule = "0 */2 * * * *" };

    private readonly InMemoryMonkeyRunTracker _tracker = new();

    private DaemonStatusReader Reader() => new(
        _log,
        Microsoft.Extensions.Options.Options.Create(_health),
        Microsoft.Extensions.Options.Options.Create(_triage),
        Microsoft.Extensions.Options.Options.Create(_daemon),
        new D1FixedTimeProvider(Now),
        _tracker);

    private Task Add(string key, params (TimeSpan Ago, string Kind, DaemonActivityLevel Level, string Message)[] entries) =>
        _log.AppendAsync(entries.Select(e => D1Entries.Entry(key, Now - e.Ago, e.Kind, e.Level, e.Message)).ToList());

    private async Task<DaemonStatus> StatusOf(string key) => (await Reader().GetAsync()).Single(s => s.DaemonKey == key);

    [Fact]
    public async Task StatusReader_TokenRefresh_UsesItsOwnSwitchScheduleAndStaleWindow()
    {
        var tokenRefresh = new TokenRefreshOptions { Schedule = "0 */7 * * * *" };
        DaemonStatusReader TokenReader() => new(
            _log,
            Microsoft.Extensions.Options.Options.Create(_health),
            Microsoft.Extensions.Options.Options.Create(_triage),
            Microsoft.Extensions.Options.Options.Create(_daemon),
            new D1FixedTimeProvider(Now),
            _tracker,
            Microsoft.Extensions.Options.Options.Create(tokenRefresh));

        await Add(DaemonKeys.TokenRefresh,
            (TimeSpan.FromMinutes(9), DaemonActivityKinds.RunStarted, DaemonActivityLevel.Info, "Run started"),
            (TimeSpan.FromMinutes(9), DaemonActivityKinds.RunCompleted, DaemonActivityLevel.Info, "No tokens configured for automatic refresh"));

        var status = (await TokenReader().GetAsync()).Single(s => s.DaemonKey == DaemonKeys.TokenRefresh);
        Assert.Equal("Token Refresh", status.DisplayName);
        Assert.Equal("0 */7 * * * *", status.Schedule);
        Assert.True(status.Enabled);
        // 9 minutes is past the generic 5-minute window but within the token-refresh one (every 5 min).
        Assert.Equal(DaemonHealthStates.Ok, status.Health);

        tokenRefresh.Enabled = false;
        status = (await TokenReader().GetAsync()).Single(s => s.DaemonKey == DaemonKeys.TokenRefresh);
        Assert.False(status.Enabled);
        Assert.Equal(DaemonHealthStates.Disabled, status.Health);
    }

    [Fact]
    public async Task StatusReader_ReturnsOneStatusPerDaemonInOrder()
    {
        await Add(DaemonKeys.KpiIngestion,
            (TimeSpan.FromSeconds(40), DaemonActivityKinds.RunStarted, DaemonActivityLevel.Info, "Run started"),
            (TimeSpan.FromSeconds(30), DaemonActivityKinds.RunCompleted, DaemonActivityLevel.Info, "Up to date — nothing to ingest"));

        var statuses = await Reader().GetAsync();

        Assert.Equal(DaemonKeys.All, statuses.Select(s => s.DaemonKey));
        Assert.Equal(DaemonKeys.All.Select(DaemonKeys.DisplayName), statuses.Select(s => s.DisplayName));
        var ingestion = statuses[0];
        Assert.Equal(DaemonHealthStates.Ok, ingestion.Health);
        Assert.Equal("Up to date — nothing to ingest", ingestion.LastRunSummary);
        Assert.Equal(Now.AddSeconds(-30), ingestion.LastRunAt);
        Assert.Equal(DaemonActivityLevel.Info, ingestion.LastRunLevel);
        Assert.Equal("0 */2 * * * *", ingestion.Schedule);
        Assert.Equal(_daemon.MonitorSchedule, statuses[1].Schedule);
        Assert.Equal(_daemon.TriageSchedule, statuses[4].Schedule);
        Assert.Null(ingestion.Mode);
        Assert.Equal("Shadow", statuses[4].Mode);
    }

    [Fact]
    public async Task StatusReader_DisabledWhenHealthMonitoringOff()
    {
        _health = new HealthMonitoringOptions { Enabled = false, Pillars = ["Application", "Business", "Infrastructure"] };

        var statuses = await Reader().GetAsync();

        Assert.All(statuses.Take(4), s =>
        {
            Assert.False(s.Enabled);
            Assert.Equal(DaemonHealthStates.Disabled, s.Health);
        });
        Assert.True(statuses[4].Enabled);
        Assert.Equal(DaemonHealthStates.NeverRun, statuses[4].Health);

        _triage.Mode = TriageMode.Off;
        var off = await Reader().GetAsync();
        Assert.All(off.Where(s => s.DaemonKey != DaemonKeys.TokenRefresh), s => Assert.Equal(DaemonHealthStates.Disabled, s.Health));
        // token-refresh follows TokenRefresh:Enabled only, not the health or triage switches.
        Assert.True(off.Single(s => s.DaemonKey == DaemonKeys.TokenRefresh).Enabled);
        Assert.Equal("Off", off[4].Mode);

        // Only the pillars listed in HealthMonitoring:Pillars are enabled.
        _health = new HealthMonitoringOptions { Enabled = true, Pillars = ["application"] };
        var onlyApp = await Reader().GetAsync();
        Assert.True(onlyApp[0].Enabled);
        Assert.True(onlyApp[1].Enabled);
        Assert.False(onlyApp[2].Enabled);
        Assert.False(onlyApp[3].Enabled);
    }

    [Fact]
    public async Task StatusReader_RunningWhenStartedWithoutCompletion()
    {
        await Add(DaemonKeys.ApplicationHealthMonitor,
            (TimeSpan.FromSeconds(120), DaemonActivityKinds.RunStarted, DaemonActivityLevel.Info, "Run started"),
            (TimeSpan.FromSeconds(114), DaemonActivityKinds.RunCompleted, DaemonActivityLevel.Info, "1 evaluated, 0 triggered, 0 suppressed, 0 skipped"),
            (TimeSpan.FromSeconds(60), DaemonActivityKinds.RunStarted, DaemonActivityLevel.Info, "Run started"));

        var status = await StatusOf(DaemonKeys.ApplicationHealthMonitor);

        Assert.Equal(DaemonHealthStates.Running, status.Health);
        Assert.Equal(Now.AddSeconds(-114), status.LastRunAt);

        // Past RunningWindowMinutes an unfinished run is no longer "running".
        await Add(DaemonKeys.BusinessHealthMonitor,
            (TimeSpan.FromMinutes(4), DaemonActivityKinds.RunStarted, DaemonActivityLevel.Info, "Run started"));
        Assert.Equal(DaemonHealthStates.NeverRun, (await StatusOf(DaemonKeys.BusinessHealthMonitor)).Health);
    }

    [Fact]
    public async Task StatusReader_StaleWhenLastRunTooOld()
    {
        await Add(DaemonKeys.KpiIngestion,
            (TimeSpan.FromSeconds(630), DaemonActivityKinds.RunStarted, DaemonActivityLevel.Info, "Run started"),
            (TimeSpan.FromMinutes(10), DaemonActivityKinds.RunCompleted, DaemonActivityLevel.Info, "1 KPI(s) processed, 1 bucket(s), 0 sample(s) written, 0 skipped"));

        var status = await StatusOf(DaemonKeys.KpiIngestion);

        Assert.Equal(DaemonHealthStates.Stale, status.Health);
        Assert.Equal(Now.AddMinutes(-10), status.LastRunAt);
    }

    [Fact]
    public async Task StatusReader_ErrorWhenLastRunHadErrors()
    {
        await Add(DaemonKeys.KpiIngestion,
            (TimeSpan.FromSeconds(90), DaemonActivityKinds.RunStarted, DaemonActivityLevel.Info, "Run started"),
            (TimeSpan.FromSeconds(84), DaemonActivityKinds.Error, DaemonActivityLevel.Error, "Latency/hotels: fetch failed — timeout"),
            (TimeSpan.FromSeconds(78), DaemonActivityKinds.RunCompleted, DaemonActivityLevel.Warning, "0 KPI(s) processed, 0 bucket(s), 0 sample(s) written, 1 skipped"));

        var status = await StatusOf(DaemonKeys.KpiIngestion);

        Assert.Equal(DaemonHealthStates.Error, status.Health);
        Assert.Equal(DaemonActivityLevel.Warning, status.LastRunLevel);
        Assert.Equal(Now.AddSeconds(-84), status.LastErrorAt);
        Assert.Equal("Latency/hotels: fetch failed — timeout", status.LastErrorMessage);

        // A later clean run clears the error state but keeps the last error visible.
        await Add(DaemonKeys.KpiIngestion,
            (TimeSpan.FromSeconds(30), DaemonActivityKinds.RunStarted, DaemonActivityLevel.Info, "Run started"),
            (TimeSpan.FromSeconds(24), DaemonActivityKinds.RunCompleted, DaemonActivityLevel.Info, "Up to date — nothing to ingest"));
        var recovered = await StatusOf(DaemonKeys.KpiIngestion);
        Assert.Equal(DaemonHealthStates.Ok, recovered.Health);
        Assert.Equal("Latency/hotels: fetch failed — timeout", recovered.LastErrorMessage);
    }

    [Fact]
    public async Task StatusReader_TriageIdleIsOkWhenInspectorRecent()
    {
        await _tracker.RecordRunAsync(MonkeyKey.InspectorMonkey, Now.AddMinutes(-1));

        // Never investigated anything yet: idle, not never-run.
        var neverInvestigated = await StatusOf(DaemonKeys.TriageMonkey);
        Assert.Equal(DaemonHealthStates.Ok, neverInvestigated.Health);
        Assert.Equal(DaemonStatusReader.TriageIdleSummary, neverInvestigated.LastRunSummary);
        Assert.Null(neverInvestigated.LastRunAt);

        // Last investigation an hour ago: still idle, and LastRunAt keeps that investigation's time.
        await Add(DaemonKeys.TriageMonkey,
            (TimeSpan.FromMinutes(61), DaemonActivityKinds.RunStarted, DaemonActivityLevel.Info, "Run started"),
            (TimeSpan.FromMinutes(60), DaemonActivityKinds.RunCompleted, DaemonActivityLevel.Info, "Investigation x Completed"));
        var idle = await StatusOf(DaemonKeys.TriageMonkey);
        Assert.Equal(DaemonHealthStates.Ok, idle.Health);
        Assert.Equal("Idle — no queued investigations", idle.LastRunSummary);
        Assert.Equal(Now.AddMinutes(-60), idle.LastRunAt);

        // The error rule still applies while idle.
        await Add(DaemonKeys.TriageMonkey,
            (TimeSpan.FromMinutes(30), DaemonActivityKinds.RunStarted, DaemonActivityLevel.Info, "Run started"),
            (TimeSpan.FromMinutes(30), DaemonActivityKinds.Error, DaemonActivityLevel.Error, "Investigation x failed — boom"),
            (TimeSpan.FromMinutes(30), DaemonActivityKinds.RunCompleted, DaemonActivityLevel.Warning, "Investigation x Failed"));
        var failed = await StatusOf(DaemonKeys.TriageMonkey);
        Assert.Equal(DaemonHealthStates.Error, failed.Health);
        Assert.Equal(DaemonStatusReader.TriageIdleSummary, failed.LastRunSummary);

        // Other daemons never get the exception.
        Assert.Equal(DaemonHealthStates.NeverRun, (await StatusOf(DaemonKeys.KpiIngestion)).Health);
    }

    [Fact]
    public async Task StatusReader_TriageStaleWhenInspectorAlsoStale()
    {
        await Add(DaemonKeys.TriageMonkey,
            (TimeSpan.FromMinutes(61), DaemonActivityKinds.RunStarted, DaemonActivityLevel.Info, "Run started"),
            (TimeSpan.FromMinutes(60), DaemonActivityKinds.RunCompleted, DaemonActivityLevel.Info, "Investigation x Completed"));

        // No Inspector run recorded at all.
        var noInspector = await StatusOf(DaemonKeys.TriageMonkey);
        Assert.Equal(DaemonHealthStates.Stale, noInspector.Health);
        Assert.Equal("Investigation x Completed", noInspector.LastRunSummary);

        // Inspector ran, but longer ago than StaleAfterMinutes.
        await _tracker.RecordRunAsync(MonkeyKey.InspectorMonkey, Now.AddMinutes(-6));
        var staleInspector = await StatusOf(DaemonKeys.TriageMonkey);
        Assert.Equal(DaemonHealthStates.Stale, staleInspector.Health);
        Assert.Equal(Now.AddMinutes(-60), staleInspector.LastRunAt);
    }

    [Fact]
    public async Task StatusReader_NeverRunWithoutActivity()
    {
        var statuses = await Reader().GetAsync();

        Assert.All(statuses, s =>
        {
            Assert.True(s.Enabled);
            Assert.Equal(DaemonHealthStates.NeverRun, s.Health);
            Assert.Null(s.LastRunAt);
            Assert.Null(s.LastRunSummary);
            Assert.Null(s.LastErrorAt);
        });
    }
}
