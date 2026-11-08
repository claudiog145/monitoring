using System.Text.Json;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring.Daemons;
using JourneyMonitor.Core.Monitoring.Inspector;
using JourneyMonitor.Core.Monitoring.Kpi;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Core.UseCases;
using JourneyMonitor.Infrastructure.Monitoring;
using JourneyMonitor.Tests.Monitoring.Kpi.Fakes;
using JourneyMonitor.Tests.Spec1000.A2;
using JourneyMonitor.Tests.Spec1000.A5;
using JourneyMonitor.Tests.Spec1000.D1;
using Microsoft.Extensions.Logging.Abstractions;

namespace JourneyMonitor.Tests.Monitoring.Kpi;

/// <summary>Contract v3 §1: ProfileKey on KPI definitions, repositories and triage.</summary>
public sealed class KpiProfileTests
{
    internal const string Acme = "acme";

    internal static KpiDefinition Latency(int id = 0, string? profileKey = null, string domain = "hotels", bool enabled = true)
    {
        var definition = A2Kpi.Definition(id, domain: domain, enabled: enabled);
        return profileKey is null ? definition : definition with { ProfileKey = profileKey };
    }

    [Fact]
    public void KpiDefinition_DefaultProfileKey_IsIncumbent()
    {
        var definition = Latency();

        Assert.Equal(KpiProfiles.Default, definition.ProfileKey);
        Assert.Equal("arrivia", KpiProfiles.Default);
        Assert.Empty(definition.Validate());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void KpiDefinition_BlankProfileKey_FailsValidation(string profileKey)
    {
        var errors = (Latency() with { ProfileKey = profileKey }).Validate();

        Assert.Contains("ProfileKey is required.", errors);
    }

    [Fact]
    public async Task KpiRepository_ListByProfile_FiltersAndNullReturnsAll()
    {
        var store = new InMemoryKpiDefinitionStore();
        var incumbent = await store.UpsertAsync(Latency(), "test");
        var acme = await store.UpsertAsync(Latency(domain: "cars", profileKey: Acme), "test");
        await store.UpsertAsync(Latency(domain: "air", profileKey: Acme, enabled: false), "test");

        var onlyAcme = await store.GetAllAsync(Acme);
        var onlyIncumbent = await store.GetAllAsync(KpiProfiles.Default);
        var all = await store.GetAllAsync(profileKey: null);
        var legacyAll = await store.GetAllAsync(CancellationToken.None);
        var enabledAcme = await store.GetEnabledAsync(KpiPillar.Application, Acme);
        var enabledAll = await store.GetEnabledAsync(null, CancellationToken.None);

        Assert.Equal(2, onlyAcme.Count);
        Assert.All(onlyAcme, d => Assert.Equal(Acme, d.ProfileKey));
        Assert.Equal(incumbent.KpiDefinitionId, Assert.Single(onlyIncumbent).KpiDefinitionId);
        Assert.Equal(3, all.Count);
        Assert.Equal(3, legacyAll.Count);
        Assert.Equal(acme.KpiDefinitionId, Assert.Single(enabledAcme).KpiDefinitionId);
        Assert.Equal(2, enabledAll.Count);

        // The in-memory anomaly store resolves the profile through the definition.
        var anomalies = new InMemoryKpiAnomalyStore(store);
        await anomalies.AddAsync(new KpiAnomaly(0, incumbent.KpiDefinitionId, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 1, 1, 0, "x", null));
        await anomalies.AddAsync(new KpiAnomaly(0, acme.KpiDefinitionId, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 1, 1, 0, "y", null));
        Assert.Equal(acme.KpiDefinitionId, Assert.Single(await anomalies.GetRecentAsync(null, null, 10, Acme)).KpiDefinitionId);
        Assert.Equal(2, (await anomalies.GetRecentAsync(null, null, 10)).Count);
    }

    [Fact]
    public async Task KpiRepository_SameKeyDifferentProfiles_AreDistinct()
    {
        var store = new InMemoryKpiDefinitionStore();

        var incumbent = await store.UpsertAsync(Latency(), "test");
        var acme = await store.UpsertAsync(Latency(profileKey: Acme), "test");

        Assert.NotEqual(incumbent.KpiDefinitionId, acme.KpiDefinitionId);
        Assert.Equal(KpiProfiles.Default, (await store.GetAsync(incumbent.KpiDefinitionId))!.ProfileKey);
        Assert.Equal(Acme, (await store.GetAsync(acme.KpiDefinitionId))!.ProfileKey);
        Assert.Contains("\"ProfileKey\":\"acme\"", Assert.Single(await store.GetAuditAsync(acme.KpiDefinitionId, 10)).SnapshotJson);

        // Within one profile the natural key stays unique.
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpsertAsync(Latency(profileKey: Acme), "test"));
    }

    [Fact]
    public async Task Triage_ForensicCaseUsesSignalProfileKey()
    {
        var store = new A5InMemoryTriageStore();
        var acmeInvestigation = A5TriageFixtures.Queued(signal: A5TriageFixtures.Signal() with { ProfileKey = Acme });
        var legacyInvestigation = A5TriageFixtures.Queued(signal: A5TriageFixtures.Signal("cars") with { AnomalyId = 43 });
        await store.CreateAsync(acmeInvestigation);
        await store.CreateAsync(legacyInvestigation);
        var provider = new KpiProfileRecordingEvidenceProvider();
        IEvidenceProvider[] providers = [provider];
        var activity = new D1ActivityHarness(new A5FixedTimeProvider(A5TriageFixtures.Now));
        var service = new TriageMonkeyService(
            store, providers, A5FakeLlmClient.Returning(A5TriageFixtures.AmberJson), new A5PauseResolver(), new A5BudgetResolver(),
            new A5RecordingNotificationService(),
            Microsoft.Extensions.Options.Options.Create(new TriageOptions { Mode = TriageMode.Shadow }),
            new A5FixedTimeProvider(A5TriageFixtures.Now), NullLogger<TriageMonkeyService>.Instance, activity.Recorder);

        await service.InvestigateAsync(acmeInvestigation.InvestigationId, CancellationToken.None);
        await service.InvestigateAsync(legacyInvestigation.InvestigationId, CancellationToken.None);

        // The evidence is collected for the signal's profile; a signal without one belongs to the incumbent.
        Assert.Equal([Acme, KpiProfiles.Default], provider.ProfileKeys);
        Assert.Equal(Acme, TriageMonkeyService.ProfileKeyOf(acmeInvestigation.Signal));
        Assert.Equal(KpiProfiles.Default, TriageMonkeyService.ProfileKeyOf(legacyInvestigation.Signal));

        var started = (await activity.EntriesAsync(DaemonKeys.TriageMonkey))
            .Where(e => e.Kind == DaemonActivityKinds.InvestigationStarted)
            .Select(e => JsonDocument.Parse(e.DataJson!).RootElement.GetProperty("profileKey").GetString())
            .ToList();
        Assert.Equal([Acme, KpiProfiles.Default], started);

        // The in-memory store filters investigations by the same resolved profile.
        var inMemory = new InMemoryTriageInvestigationStore();
        await inMemory.CreateAsync(acmeInvestigation);
        await inMemory.CreateAsync(legacyInvestigation);
        Assert.Equal(acmeInvestigation.InvestigationId, Assert.Single(await inMemory.GetRecentAsync(10, Acme)).InvestigationId);
        Assert.Equal(legacyInvestigation.InvestigationId, Assert.Single(await inMemory.GetRecentAsync(10, KpiProfiles.Default)).InvestigationId);
        Assert.Equal(2, (await inMemory.GetRecentAsync(10)).Count);
    }

    [Fact]
    public void TriageSignal_OldJsonWithoutProfileKey_Deserializes()
    {
        const string oldJson = """
            {"detectedAt":"2026-11-05T11:40:00+00:00","pillar":"Application Health","kpi":"Latency","detection":"x","domain":"hotels","stage":null,"anomalyId":7,"source":"daemon"}
            """;

        var signal = JsonSerializer.Deserialize<TriageSignal>(oldJson, TriageJson.Options);

        Assert.NotNull(signal);
        Assert.Null(signal.ProfileKey);
        Assert.Equal(7, signal.AnomalyId);
        Assert.Equal("anomaly:7", TriageDedupKey.For(signal));

        var roundTrip = JsonSerializer.Serialize(signal with { ProfileKey = Acme }, TriageJson.Options);
        Assert.Contains("\"profileKey\":\"acme\"", roundTrip);
        Assert.Equal(Acme, JsonSerializer.Deserialize<TriageSignal>(roundTrip, TriageJson.Options)!.ProfileKey);
    }
}

/// <summary>PillarHealthMonitorService holds a static run lock: serial with the A1 monitor tests.</summary>
[Collection(A1MonitorRunLockCollection.Name)]
public sealed class KpiProfileMonitorTests
{
    private static readonly DateTimeOffset Day = new(2026, 11, 4, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = Day.AddHours(17).AddMinutes(10);

    [Fact]
    public async Task Monitor_SignalCarriesDefinitionProfileKey()
    {
        var definitions = new A1InMemoryDefinitionStore();
        var buckets = new A1InMemoryBucketStore();
        var states = new A1InMemoryStateStore();
        var anomalies = new A1InMemoryAnomalyStore();
        var watermarks = new A1InMemoryWatermarkStore();
        var triage = new A1RecordingTriageSubmitter();

        var definition = KpiEvaluatorTests.Definition(DetectionMethodKind.StaticThreshold, KpiDirection.Increase, "{\"threshold\":3000}") with
        {
            KpiDefinitionId = 1,
            Pillar = KpiPillar.Application,
            ProfileKey = KpiProfileTests.Acme,
        };
        definitions.Definitions.Add(definition);
        states.States[1] = new KpiDetectionState(1, 1, Day.AddHours(15), null, Now.AddHours(-1));
        watermarks.Watermarks[1] = Now;
        buckets.Buckets.Add(new KpiBucket(1, Day.AddHours(16), 100, 0, null, 3500, null, null, null));

        var service = new PillarHealthMonitorService(
            definitions, buckets, states, anomalies, watermarks, new KpiEvaluator(), triage,
            Microsoft.Extensions.Options.Options.Create(new HealthMonitoringOptions { Enabled = true, Pillars = ["application"] }),
            new A1FakeTimeProvider(Now), NullLogger<PillarHealthMonitorService>.Instance);

        var result = await service.RunAsync(KpiPillar.Application);

        Assert.Equal(1, result.Triggered);
        var signal = Assert.Single(triage.Signals);
        Assert.Equal(KpiProfileTests.Acme, signal.ProfileKey);
        Assert.Equal(TriageSignalSources.Daemon, signal.Source);
    }
}

/// <summary>KpiIngestionService holds a static run lock: serial with the A2 ingestion tests.</summary>
[Collection(A2IngestionRunLockCollection.Name)]
public sealed class KpiProfileIngestionTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 14, 37, 12, TimeSpan.Zero);

    private readonly A2BucketStore _buckets = new();
    private readonly A2SampleStore _samples = new();
    private readonly A2WatermarkStore _watermarks = new();
    private readonly D1ActivityHarness _activity = new(new D1FixedTimeProvider(Now));

    private KpiIngestionService Service(KpiDefinition[] definitions, params IKpiSource[] sources) => new(
        new A2DefinitionStore(definitions),
        sources,
        _buckets,
        _samples,
        _watermarks,
        Microsoft.Extensions.Options.Options.Create(new HealthMonitoringOptions { Enabled = true }),
        new A2FixedTimeProvider(Now),
        NullLogger<KpiIngestionService>.Instance,
        null,
        _activity.Recorder,
        _activity.Tracker,
        _activity.Log);

    [Fact]
    public async Task Ingestion_ProcessesAllProfiles()
    {
        var source = new A2KpiSource { Respond = (d, from, to, per) => A2KpiSource.Hourly(d, from, to, per, 2) };
        var incumbent = KpiProfileTests.Latency(1);
        var acme = KpiProfileTests.Latency(2, KpiProfileTests.Acme, domain: "cars");

        var result = await Service([incumbent, acme], source).RunAsync();

        Assert.Equal(2, result.KpisProcessed);
        Assert.Equal([1, 2], source.Calls.Select(c => c.KpiDefinitionId).Distinct().OrderBy(id => id).ToArray());
        Assert.Contains(_buckets.All, b => b.KpiDefinitionId == 1);
        Assert.Contains(_buckets.All, b => b.KpiDefinitionId == 2);
        Assert.True(_watermarks.Values.ContainsKey(1));
        Assert.True(_watermarks.Values.ContainsKey(2));

        var kpiEntries = (await _activity.EntriesAsync(DaemonKeys.KpiIngestion)).Where(e => e.Kind == DaemonActivityKinds.IngestionKpi).ToList();
        var profiles = kpiEntries.Select(e => JsonDocument.Parse(e.DataJson!).RootElement.GetProperty("profileKey").GetString()).ToList();
        Assert.Equal([KpiProfiles.Default, KpiProfileTests.Acme], profiles);
    }

    [Fact]
    public async Task Ingestion_NoSourceForProfile_WarnsAndSkips()
    {
        var source = new A2KpiSource
        {
            Respond = (d, from, to, per) => d.Domain == "cruises"
                ? throw new KpiSourceUnavailableException("App Insights is not configured for 'cruises'.")
                : A2KpiSource.Hourly(d, from, to, per, 2),
        };
        var incumbent = KpiProfileTests.Latency(1);
        var acmeUnconfigured = KpiProfileTests.Latency(2, KpiProfileTests.Acme, domain: "cruises");
        var acmeUnknownSource = KpiProfileTests.Latency(3, KpiProfileTests.Acme, domain: "air") with { SourceKey = "other:latency" };

        var result = await Service([incumbent, acmeUnconfigured, acmeUnknownSource], source).RunAsync();

        Assert.Equal(1, result.KpisProcessed);
        Assert.Equal(2, result.Skipped);
        Assert.DoesNotContain(_buckets.All, b => b.KpiDefinitionId is 2 or 3);
        Assert.False(_watermarks.Values.ContainsKey(2));
        Assert.False(_watermarks.Values.ContainsKey(3));

        var warnings = (await _activity.EntriesAsync(DaemonKeys.KpiIngestion))
            .Where(e => e.Kind == DaemonActivityKinds.IngestionKpi && e.Level == DaemonActivityLevel.Warning)
            .Select(e => e.Message)
            .ToList();
        Assert.Equal(
            [
                "Latency/cruises [acme]: no telemetry source configured for this profile",
                "Latency/air [acme]: no telemetry source configured for this profile",
            ],
            warnings);
    }
}

internal sealed class KpiProfileRecordingEvidenceProvider : IEvidenceProvider
{
    public List<string?> ProfileKeys { get; } = [];

    public string Dimension => TriageDimensions.ApplicationHealth;

    public Task<EvidenceSection> CollectAsync(TriageSignal signal, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken ct = default)
    {
        ProfileKeys.Add(signal.ProfileKey);
        return Task.FromResult(new EvidenceSection(Dimension, false, null, "test"));
    }
}
