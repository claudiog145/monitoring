using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Analytics;
using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring.Kpi;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Core.UseCases;
using JourneyMonitor.Infrastructure.Monitoring;
using JourneyMonitor.Infrastructure.ProfileAnalytics;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Tests.MultiCompany.Analytics;

/// <summary>
/// Contract v6 §4.4 / §4.5 (MC3): analytics metrics are synced per company, per service and per step,
/// every read names one company, and the overview is composed only from that company's data with the
/// incumbent-only widgets marked as not applicable for everybody else.
/// </summary>
public class ProfileAnalyticsTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Analytics_MetricsPerProfileStep()
    {
        var profiles = new InMemoryCompanyProfileStore(
        [
            Banco(),
            Kinectify(),
            Incumbent(),
            Profile("dormant", ("svc", ["a"])) with { Enabled = false },
        ]);
        var telemetry = new FakeTelemetry(failService: "screening");
        var store = new RecordingProfileMetricStore();
        var sync = new SyncProfileDashboardMetricsService(
            profiles,
            telemetry,
            store,
            Options.Create(new ProfileAnalyticsOptions { Window = "6h", BinSize = "5m" }),
            Options.Create(new JourneySyncOptions()),
            new FixedTime(Now));

        var passes = await sync.SyncAllAsync(CancellationToken.None);

        // Every enabled non-incumbent company, every service; the incumbent's charts stay on its per-club sync.
        Assert.Equal(
            ["banco/core", "banco/pagos", "kinectify/screening"],
            telemetry.Calls.Select(c => $"{c.Profile}/{c.Service}").Order(StringComparer.Ordinal).ToList());
        Assert.Equal(TimeSpan.FromMinutes(5), telemetry.Calls[0].Bin);
        Assert.True(telemetry.Calls[0].To - telemetry.Calls[0].From >= TimeSpan.FromHours(6));

        // Both charts per service, keyed by the company, the service and the step - never an unknown step.
        Assert.Equal(
            ["banco/core/exceptions_by_step", "banco/core/step_latency_p95", "banco/pagos/exceptions_by_step", "banco/pagos/step_latency_p95"],
            store.Writes.Select(w => $"{w.Profile}/{w.Service}/{w.Chart}").Order(StringComparer.Ordinal).ToList());
        var pagosLatency = store.Writes.Single(w => w.Service == "pagos" && w.Chart == DashboardCharts.StepLatencyP95);
        Assert.Equal(["ach", "autorizacion"], pagosLatency.Points.Select(p => p.Series).Order(StringComparer.Ordinal).ToList());
        Assert.All(pagosLatency.Points, p => Assert.Equal("pagos", p.Domain));
        Assert.All(store.Writes.SelectMany(w => w.Points), p => Assert.NotEqual("ghost", p.Series));
        Assert.All(store.Writes, w => Assert.Equal("banco", w.Profile));

        // One company failing does not stop another; the failure is reported, not silently empty.
        var failed = passes.Where(p => p.ProfileKey == "kinectify").ToList();
        Assert.Equal(2, failed.Count);
        Assert.All(failed, p => Assert.False(p.Stored));
        Assert.All(failed, p => Assert.Contains("no source", p.SkipReason));
        Assert.Equal(4, passes.Count(p => p.ProfileKey == "banco" && p.Stored));

        // The query is the profile's own classifier, scoped to the service's aliases.
        var kql = AppInsightsProfileDashboardTelemetry.RenderKql(Banco(), "pagos", Now.AddHours(-6), Now, TimeSpan.FromMinutes(5));
        Assert.Contains("let serviceKey = \"pagos\";", kql);
        Assert.Contains("| extend MatchIndex=case(", kql);
        Assert.Contains("{{SOURCE:bank-pagos}}", kql);
        Assert.DoesNotContain("{{SOURCE:bank-core}}", kql);
        Assert.Contains("\"step_latency_p95\"", kql);
        Assert.Contains("\"exceptions_by_step\"", kql);
        Assert.DoesNotContain("{{from}}", kql);
        Assert.DoesNotContain("{{SERVICE_AND_STEP_CLASSIFIER}}", kql);
    }

    [Fact]
    public async Task Analytics_ReadFilteredByProfile()
    {
        var profiles = new InMemoryCompanyProfileStore([Banco(), Kinectify(), Incumbent()]);
        var store = new RecordingProfileMetricStore();
        store.Seed("banco", Point("pagos", DashboardCharts.StepLatencyP95, "ach"));
        store.Seed("banco", Point("core", DashboardCharts.ExceptionsByStep, "transferencia"));
        store.Seed("kinectify", Point("screening", DashboardCharts.StepLatencyP95, "01-ingest"));
        store.Seed(KpiProfiles.Default, Point("hotels", DashboardCharts.StepLatencyP95, "search"));
        var service = new GetDashboardMetricsService(
            new UnusedLegacyStore(),
            store,
            profiles,
            new KpiProfileResolver(activeStore: new ActiveProfile("kinectify"), profileStore: profiles));

        var (status, banco) = await service.GetForProfileAsync("banco", null, null, null, null, null, CancellationToken.None);
        Assert.Equal(200, status);
        Assert.Equal("banco", banco.ProfileKey);
        Assert.Equal(2, banco.RowCount);
        Assert.Equal(["core", "pagos"], banco.Points.Select(p => p.Domain).Order(StringComparer.Ordinal).ToList());
        Assert.All(store.Reads, r => Assert.Equal("banco", r.Profile));
        Assert.Equal(["pagos", "core"], banco.Services!.Select(s => s.Key).ToList());
        Assert.Equal(["ach", "autorizacion"], banco.Services![0].Steps.Select(s => s.Key).ToList());
        Assert.Equal([DashboardCharts.StepLatencyP95, DashboardCharts.ExceptionsByStep], banco.Services![0].Charts);
        Assert.Equal(new DashboardLabels("Canal", "Paso"), banco.Labels);

        // No profileKey: the active company, never a mix.
        store.Reads.Clear();
        var (_, active) = await service.GetForProfileAsync(null, null, null, null, null, null, CancellationToken.None);
        Assert.Equal("kinectify", active.ProfileKey);
        Assert.Equal(["01-ingest"], active.Points.Select(p => p.Series).ToList());
        Assert.All(store.Reads, r => Assert.Equal("kinectify", r.Profile));

        // One service of the profile; the entity filter is reported as not applicable, not silently mixed.
        var (_, pagos) = await service.GetForProfileAsync("banco", "pagos", null, "1h", null, "SUC-1", CancellationToken.None);
        Assert.Equal("pagos", pagos.Domain);
        Assert.Equal(["ach"], pagos.Points.Select(p => p.Series).ToList());
        Assert.NotNull(pagos.Warning);

        Assert.Equal(400, (await service.GetForProfileAsync("*", null, null, null, null, null, CancellationToken.None)).StatusCode);
        Assert.Equal(400, (await service.GetForProfileAsync("nobody", null, null, null, null, null, CancellationToken.None)).StatusCode);
        Assert.Equal(400, (await service.GetForProfileAsync("banco", "screening", null, null, null, null, CancellationToken.None)).StatusCode);

        // The incumbent reads its own rows with its per-vertical chart set.
        var (_, incumbent) = await service.GetForProfileAsync(KpiProfiles.Default, null, null, null, null, null, CancellationToken.None);
        Assert.Equal(["search"], incumbent.Points.Select(p => p.Series).ToList());
        Assert.Empty(incumbent.Services!.Single(s => s.Key == "shared").Charts);
    }

    [Fact]
    public async Task Overview_ComposedOnlyFromProfile()
    {
        var profiles = new InMemoryCompanyProfileStore([Banco(), Kinectify(), Incumbent()]);
        var reader = new FakeOverviewReader(new ProfileOverviewCounts(
            new OverviewCounts(1000, 10, 40, 30, null, null, 1200, 3, 7, 1, null),
            [new OverviewEntity("SUC-1", 2, 9, 0), new OverviewEntity("SUC-2", 1, 1, 0)],
            [new OverviewEntity("SUC-2", 0, 0, 5), new OverviewEntity("SUC-3", 0, 0, 2)]));
        var pillars = new FakePillarReader();
        var triage = new FakeTriageStore(
        [
            Investigation("banco", "transfer latency"),
            Investigation("kinectify", "screening latency"),
        ]);
        var service = new GetOverviewService(
            new UnusedLegacyOverviewReader(),
            reader,
            profiles,
            pillars,
            triage,
            new KpiProfileResolver(activeStore: new ActiveProfile("kinectify"), profileStore: profiles),
            new FixedTime(Now));

        var outcome = await service.GetAsync(new OverviewRequest("pagos", "SUC-1", null, "1h", null, "banco"), CancellationToken.None);

        Assert.Equal(200, outcome.StatusCode);
        var result = outcome.Result;
        Assert.Equal("banco", result.ProfileKey);
        Assert.Equal("sql", result.Source);

        var query = Assert.Single(reader.Queries);
        Assert.Equal("banco", query.ProfileKey);
        Assert.False(query.IsIncumbent);
        Assert.Equal("pagos", query.ServiceKey);
        Assert.Equal("SUC-1", query.EntityKey);
        Assert.Equal(Now.AddHours(-1), query.FilteredSince);
        Assert.Equal(["banco"], pillars.ProfileKeys);
        Assert.Equal(["banco"], triage.ProfileKeys);

        // Sections hold only the company's rows, even if a source hands back a stray one.
        Assert.Equal(2, result.Kpis!.Count);
        Assert.All(result.Kpis!, k => Assert.StartsWith("banco", k.Kpi));
        Assert.True(result.Kpis![0].Anomalous);
        var pillar = Assert.Single(result.Pillars!);
        Assert.Equal("Application Health", pillar.Pillar);
        var investigation = Assert.Single(result.Investigations!);
        Assert.Equal("transfer latency", investigation.Kpi);
        Assert.Equal(new OverviewExceptionSummary(3, 7, 1), result.Exceptions);
        Assert.Equal(["SUC-1", "SUC-2", "SUC-3"], result.TopEntities!.Select(e => e.Key).ToList());
        Assert.Equal(5, result.TopEntities![1].AffectedJourneys);
        Assert.Equal("Sucursal", result.Labels!.Entity);

        // Tiles speak the company's vocabulary: no booking funnel for a bank.
        Assert.DoesNotContain(result.Tiles, t => t.Label == "Booking Conversion");
        Assert.Contains(result.Tiles, t => t.Label == "Operación completion" && t.Value == "30/40");

        // No profileKey: the active company.
        reader.Queries.Clear();
        var active = await service.GetAsync(new OverviewRequest(null, null, null, null, null), CancellationToken.None);
        Assert.Equal("kinectify", active.Result.ProfileKey);
        Assert.Equal("kinectify", Assert.Single(reader.Queries).ProfileKey);

        // Never every company, never someone else's service.
        Assert.Equal(400, (await service.GetAsync(new OverviewRequest(null, null, null, null, null, "*"), CancellationToken.None)).StatusCode);
        Assert.Equal(400, (await service.GetAsync(new OverviewRequest("screening", null, null, null, null, "banco"), CancellationToken.None)).StatusCode);
        Assert.Equal(400, (await service.GetAsync(new OverviewRequest(null, null, null, null, null, "nobody"), CancellationToken.None)).StatusCode);
    }

    [Fact]
    public async Task Overview_IncumbentOnlyWidgetsOmitted()
    {
        var profiles = new InMemoryCompanyProfileStore([Banco(), Incumbent()]);
        var reader = new FakeOverviewReader(new ProfileOverviewCounts(
            new OverviewCounts(100, 0, 10, 10, 10, 4, 500, 0, 0, 0, 2), [], []));
        var service = new GetOverviewService(
            new UnusedLegacyOverviewReader(),
            reader,
            profiles,
            new FakePillarReader(),
            new FakeTriageStore([]),
            new KpiProfileResolver(profileStore: profiles),
            new FixedTime(Now));

        var banco = (await service.GetAsync(new OverviewRequest(null, null, null, null, null, "banco"), CancellationToken.None)).Result;
        foreach (var key in OverviewWidgetKeys.IncumbentOnly)
        {
            var widget = Assert.Single(banco.Widgets!, w => w.Key == key);
            Assert.False(widget.Applicable);
            Assert.False(string.IsNullOrWhiteSpace(widget.Note));
        }
        Assert.All(banco.Widgets!.Where(w => !OverviewWidgetKeys.IncumbentOnly.Contains(w.Key)), w => Assert.True(w.Applicable));
        Assert.False(banco.FabricApplies);
        // The incumbent's vertical list is not another company's: "hotels" is not a bank service.
        Assert.Equal(400, (await service.GetAsync(new OverviewRequest("hotels", null, null, null, null, "banco"), CancellationToken.None)).StatusCode);

        var incumbent = (await service.GetAsync(new OverviewRequest("hotels", "15639", null, null, null, KpiProfiles.Default), CancellationToken.None)).Result;
        Assert.Equal(KpiProfiles.Default, incumbent.ProfileKey);
        Assert.All(incumbent.Widgets!, w => Assert.True(w.Applicable));
        Assert.True(incumbent.FabricApplies);
        Assert.Contains(incumbent.Tiles, t => t.Label == "Booking Conversion");
        Assert.True(reader.Queries[^1].IsIncumbent);
        Assert.Equal("hotels", reader.Queries[^1].ServiceKey);
    }

    // ------------------------------------------------------------------ profiles

    internal static CompanyProfile Banco() =>
        Profile("banco", ("pagos", ["ach", "autorizacion"]), ("core", ["transferencia"]));

    internal static CompanyProfile Kinectify() =>
        Profile("kinectify", ("screening", ["01-ingest"])) with
        {
            Labels = new CompanyLabels("Product", "Workflow", "Stage", "Customer", "Team"),
        };

    internal static CompanyProfile Incumbent() =>
        Profile(KpiProfiles.Default, ("hotels", ["search"]), ("shared", ["login"])) with
        {
            Labels = new CompanyLabels("Vertical", "Journey", "Stage", "Club", "Owning team"),
        };

    internal static CompanyProfile Profile(string key, params (string Service, string[] Steps)[] services) =>
        new(
            key,
            key + " (test)",
            new CompanyLabels("Canal", "Operación", "Paso", "Sucursal", "Equipo"),
            services.Select(s => new ServiceDefinition(
                s.Service,
                s.Service.ToUpperInvariant(),
                [(key == "banco" ? "bank" : key) + "-" + s.Service],
                [],
                [new FlowDefinition(
                    "flow",
                    "Flow",
                    s.Steps.Select((step, i) => new StepDefinition(
                        step, step.ToUpperInvariant(), i + 1, [new StepMatcher(StepMatcherKind.PathContains, "/" + step)])).ToList())])).ToList(),
            [new EntityIdentitySource(1, EntityIdentitySourceKind.CustomDimension, "BranchId", "branch")],
            new CompanyGlossary("canal", "operación", "paso", "sucursal", "equipo"));

    private static DashboardMetricPoint Point(string service, string chart, string step) =>
        new(service, chart, step, Now.AddMinutes(-10), 42m, "6h", "production", Now);

    private static TriageInvestigation Investigation(string profileKey, string kpi) =>
        new(
            Guid.NewGuid(),
            "dedup-" + kpi,
            null,
            new TriageSignal(Now, "Application Health", kpi, "statistical", "pagos", null, null, TriageSignalSources.Daemon, profileKey),
            TriageInvestigationStatus.Completed,
            null,
            TriageAlertLevels.Amber,
            false,
            Now.AddMinutes(-5),
            Now,
            null);

    // ------------------------------------------------------------------ fakes

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class ActiveProfile(string key) : IActiveCompanyProfileStore
    {
        public Task<string?> GetActiveAsync(CancellationToken ct = default) => Task.FromResult<string?>(key);

        public Task<ActiveCompanyProfile?> GetActiveDetailAsync(CancellationToken ct = default) =>
            Task.FromResult<ActiveCompanyProfile?>(new ActiveCompanyProfile(key, Now, "test"));

        public Task SetActiveAsync(string profileKey, string changedBy, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeTelemetry(string failService) : IProfileDashboardTelemetry
    {
        public List<(string Profile, string Service, DateTimeOffset From, DateTimeOffset To, TimeSpan Bin)> Calls { get; } = [];

        public Task<IReadOnlyList<ProfileStepMetric>> QueryServiceAsync(
            CompanyProfile profile,
            ServiceDefinition service,
            DateTimeOffset fromInclusive,
            DateTimeOffset toExclusive,
            TimeSpan binSize,
            string environment,
            CancellationToken cancellationToken)
        {
            Calls.Add((profile.ProfileKey, service.Key, fromInclusive, toExclusive, binSize));
            if (service.Key == failService)
            {
                throw new InvalidOperationException($"no source for {service.Key}");
            }

            var metrics = service.Flows.SelectMany(f => f.Steps)
                .SelectMany(step => new[]
                {
                    new ProfileStepMetric(DashboardCharts.StepLatencyP95, step.Key, Now.AddMinutes(-5), 250m),
                    new ProfileStepMetric(DashboardCharts.ExceptionsByStep, step.Key, Now.AddMinutes(-5), 3m),
                })
                .Append(new ProfileStepMetric(DashboardCharts.StepLatencyP95, "ghost", Now.AddMinutes(-5), 1m))
                .ToList();
            return Task.FromResult<IReadOnlyList<ProfileStepMetric>>(metrics);
        }
    }

    private sealed class RecordingProfileMetricStore : IProfileDashboardMetricStore
    {
        private readonly List<(string Profile, DashboardMetricPoint Point)> _rows = [];

        public List<(string Profile, string Service, string Chart, IReadOnlyList<DashboardMetricPoint> Points)> Writes { get; } = [];

        public List<(string Profile, string? Service)> Reads { get; } = [];

        public void Seed(string profileKey, DashboardMetricPoint point) => _rows.Add((profileKey, point));

        public Task ReplaceProfileChartAsync(
            string profileKey,
            string serviceKey,
            string chart,
            string window,
            string? environment,
            IReadOnlyList<DashboardMetricPoint> points,
            CancellationToken cancellationToken)
        {
            Writes.Add((profileKey, serviceKey, chart, points));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<DashboardMetricPoint>> GetProfilePointsAsync(
            string profileKey,
            string? serviceKey,
            string? chart,
            DateTimeOffset? since,
            string? environment,
            string? clubId,
            CancellationToken cancellationToken)
        {
            Reads.Add((profileKey, serviceKey));
            IReadOnlyList<DashboardMetricPoint> rows = _rows
                .Where(r => r.Profile == profileKey
                    && (serviceKey is null || r.Point.Domain == serviceKey)
                    && (chart is null || r.Point.Chart == chart))
                .Select(r => r.Point)
                .ToList();
            return Task.FromResult(rows);
        }
    }

    private sealed class UnusedLegacyStore : IDashboardMetricStore
    {
        public Task ReplaceChartAsync(string domain, string chart, string window, string? environment, string? clubId, IReadOnlyList<DashboardMetricPoint> points, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the profile path must not use the legacy store");

        public Task<IReadOnlyList<DashboardMetricPoint>> GetAsync(string? domain, string? chart, DateTimeOffset? since, string? environment, string? clubId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the profile path must not use the legacy store");
    }

    private sealed class UnusedLegacyOverviewReader : IOverviewReader
    {
        public Task<OverviewCounts> ReadAsync(OverviewReadQuery query, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the profile path must not use the legacy reader");
    }

    private sealed class FakeOverviewReader(ProfileOverviewCounts counts) : IProfileOverviewReader
    {
        public List<ProfileOverviewQuery> Queries { get; } = [];

        public Task<ProfileOverviewCounts> ReadAsync(ProfileOverviewQuery query, CancellationToken cancellationToken)
        {
            Queries.Add(query);
            return Task.FromResult(counts);
        }
    }

    private sealed class FakePillarReader : IPillarHealthReader
    {
        public List<string?> ProfileKeys { get; } = [];

        public Task<IReadOnlyList<PillarHealth>> GetAsync(string? domain, string? profileKey = null, CancellationToken ct = default)
        {
            ProfileKeys.Add(profileKey);
            var key = profileKey ?? "";
            IReadOnlyList<PillarHealth> pillars =
            [
                new PillarHealth(KpiPillar.Application, "Application Health", "anomalous",
                [
                    Kpi(1, key + " latency", key, consecutive: 2),
                    Kpi(2, key + " errors", key, consecutive: 0),
                    // A stray row of another company must not reach the overview.
                    Kpi(3, "stray latency", "someone-else", consecutive: 5),
                ]),
            ];
            return Task.FromResult(pillars);
        }

        private static KpiHealth Kpi(int id, string name, string profileKey, int consecutive) =>
            new(id, name, "pagos", "", KpiDirection.Increase, true,
                new KpiBucket(id, Now.AddMinutes(-5), 100, 10, 50, 900, 120, 30, null),
                new KpiDetectionState(id, consecutive, Now.AddMinutes(-5), null, Now),
                null)
            {
                ProfileKey = profileKey,
            };
    }

    private sealed class FakeTriageStore(IReadOnlyList<TriageInvestigation> investigations) : ITriageInvestigationStore
    {
        public List<string?> ProfileKeys { get; } = [];

        public Task<IReadOnlyList<TriageInvestigation>> GetRecentAsync(int top, string? profileKey = null, CancellationToken ct = default)
        {
            ProfileKeys.Add(profileKey);
            // Every row on purpose: the composer must still keep only the requested company's.
            return Task.FromResult(investigations);
        }

        public Task CreateAsync(TriageInvestigation investigation, CancellationToken ct = default) => Task.CompletedTask;

        public Task<TriageInvestigation?> GetAsync(Guid investigationId, CancellationToken ct = default) =>
            Task.FromResult<TriageInvestigation?>(null);

        public Task<TriageInvestigation?> FindOpenByDedupKeyAsync(string dedupKey, CancellationToken ct = default) =>
            Task.FromResult<TriageInvestigation?>(null);

        public Task UpdateStatusAsync(Guid investigationId, TriageInvestigationStatus status, string? error, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task CompleteAsync(Guid investigationId, TriageResult result, bool notified, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
