using System.Net;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring.Kpi;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Core.UseCases;
using JourneyMonitor.Functions.Functions;
using JourneyMonitor.Infrastructure.Monitoring;
using JourneyMonitor.Tests.Company;
using JourneyMonitor.Tests.Spec1000.A4;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Tests.Functions;

/// <summary>Contract v3 §1.5: the KPI and triage HTTP API default to the active company profile.</summary>
public sealed class KpiProfileApiTests
{
    private const string Acme = "acme";
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    private const string TriageBody = """
        {"detectedAt":"2026-11-05T11:40:00Z","pillar":"Application Health","kpi":"Latency","detection":"manual check","domain":"hotels"}
        """;

    private readonly A4FakeKpiDefinitionStore _definitions = new();
    private readonly A4FakeKpiBucketStore _buckets = new();
    private readonly A4FakeKpiStateStore _states = new();
    private readonly A4FakeKpiAnomalyStore _anomalies = new();
    private readonly A4FakeInvestigationStore _investigations = new();
    private readonly A4FakeWorkQueue _queue = new();
    private readonly FakeActiveStore _active = new() { Active = Acme };

    private KpiProfileResolver Resolver() => new(
        Options.Create(new ProfileDrivenCompanyOptions { DefaultProfileKey = KpiProfiles.Default }),
        _active,
        new InMemoryCompanyProfileStore(
        [
            CompanyKqlTemplaterTests.Profile() with { ProfileKey = KpiProfiles.Default },
            CompanyKqlTemplaterTests.Profile() with { ProfileKey = Acme },
        ]));

    private static IOptions<MonitoringOptions> Monitoring() =>
        Options.Create(new MonitoringOptions { InternalApiSecret = A4Http.InternalSecret, SettingsWriteSecret = A4Http.WriteSecret });

    private KpiDefinitionAdminFunctions Admin() => new(
        _definitions, Monitoring(), new A4FakeTimeProvider(Now), NullLogger<KpiDefinitionAdminFunctions>.Instance, Resolver());

    private HealthReadFunctions Health()
    {
        var time = new A4FakeTimeProvider(Now);
        var reader = new GetPillarHealthService(_definitions, _buckets, _states, _anomalies, time);
        return new HealthReadFunctions(reader, _anomalies, _buckets, _definitions, time, NullLogger<HealthReadFunctions>.Instance, Resolver());
    }

    private TriageInvestigationsFunction Triage() => new(
        new TriageSignalSubmitter(_investigations, _queue, new A4FakeTimeProvider(Now), NullLogger<TriageSignalSubmitter>.Instance),
        _investigations,
        Monitoring(),
        NullLogger<TriageInvestigationsFunction>.Instance,
        Resolver());

    private void SeedTwoProfiles()
    {
        _definitions.Definitions.Add(A4Http.Definition(1, KpiPillar.Application));
        _definitions.Definitions.Add(A4Http.Definition(2, KpiPillar.Application, domain: "cars") with { ProfileKey = Acme });
        _definitions.Definitions.Add(A4Http.Definition(3, KpiPillar.Business, domain: "cars") with { ProfileKey = Acme, Kpi = "Bookings" });
    }

    private static string KpiBody(string? profileKey, int id = 0, string kpi = "Exceptions", string domain = "hotels") => $$"""
        {"kpiDefinitionId":{{id}},"pillar":"Application","kpi":"{{kpi}}","domain":"{{domain}}","stage":"","sourceKey":"appinsights:exceptions",
         "direction":"increase","aggregation":"count","evaluationFrequency":"00:05:00","bucketSize":"01:00:00","lookbackWindow":"1.00:00:00",
         "sampleSize":0,"minObservationsPerBucket":0,"detectionMethod":"robustZScore",
         "methodParamsJson":"{\"windowBuckets\":24,\"zThreshold\":3}","consecutiveCount":2,"cooldownMinutes":120,
         "enabled":true,"changedBy":"ana@example.com"{{(profileKey is null ? "" : $",\"profileKey\":\"{profileKey}\"")}}}
        """;

    [Fact]
    public async Task SettingsApi_DefaultsToActiveProfile()
    {
        SeedTwoProfiles();

        var active = (await Admin().GetAllAsync(A4Http.Request("http://localhost/api/settings/kpis"), CancellationToken.None)).ReadJson().AsArray();
        Assert.Equal([2, 3], active.Select(d => d!["kpiDefinitionId"]!.GetValue<int>()).ToArray());
        Assert.All(active, d => Assert.Equal(Acme, d!["profileKey"]!.GetValue<string>()));

        var named = (await Admin().GetAllAsync(A4Http.Request("http://localhost/api/settings/kpis?profileKey=arrivia"), CancellationToken.None)).ReadJson().AsArray();
        Assert.Equal(KpiProfiles.Default, Assert.Single(named)!["profileKey"]!.GetValue<string>());

        var all = (await Admin().GetAllAsync(A4Http.Request("http://localhost/api/settings/kpis?profileKey=*"), CancellationToken.None)).ReadJson().AsArray();
        Assert.Equal(3, all.Count);

        // Nothing active: Company:DefaultProfileKey decides.
        _active.Active = null;
        var fallback = (await Admin().GetAllAsync(A4Http.Request("http://localhost/api/settings/kpis"), CancellationToken.None)).ReadJson().AsArray();
        Assert.Equal(1, Assert.Single(fallback)!["kpiDefinitionId"]!.GetValue<int>());

        // A POST without profileKey lands in the active profile.
        _active.Active = Acme;
        var created = await Admin().UpsertAsync(
            A4Http.Request("http://localhost/api/settings/kpis", KpiBody(null), writeSecret: A4Http.WriteSecret), CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        Assert.Equal(Acme, created.ReadJson()["profileKey"]!.GetValue<string>());
        Assert.Equal(Acme, _definitions.Upserts.Single().Definition.ProfileKey);
    }

    [Fact]
    public async Task SettingsApi_UnknownProfileKey_Returns400()
    {
        SeedTwoProfiles();

        var post = await Admin().UpsertAsync(
            A4Http.Request("http://localhost/api/settings/kpis", KpiBody("ice"), writeSecret: A4Http.WriteSecret), CancellationToken.None);
        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
        Assert.Contains("Unknown profileKey 'ice'.", post.ReadJson()["errors"]!.AsArray().Select(e => e!.GetValue<string>()));
        Assert.Empty(_definitions.Upserts);

        var get = await Admin().GetAllAsync(A4Http.Request("http://localhost/api/settings/kpis?profileKey=ice"), CancellationToken.None);
        Assert.Equal(HttpStatusCode.BadRequest, get.StatusCode);

        // The same natural key in another, existing profile is not a clash.
        var otherProfile = await Admin().UpsertAsync(
            A4Http.Request("http://localhost/api/settings/kpis", KpiBody(KpiProfiles.Default), writeSecret: A4Http.WriteSecret), CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, otherProfile.StatusCode);
        var sameProfile = await Admin().UpsertAsync(
            A4Http.Request("http://localhost/api/settings/kpis", KpiBody(KpiProfiles.Default), writeSecret: A4Http.WriteSecret), CancellationToken.None);
        Assert.Equal(HttpStatusCode.BadRequest, sameProfile.StatusCode);
    }

    [Fact]
    public async Task SettingsApi_UpdateWithoutProfileKey_KeepsStoredProfile()
    {
        SeedTwoProfiles();

        // Update by id without profileKey while 'acme' is active: KPI 1 stays in 'arrivia'.
        var update = await Admin().UpsertAsync(
            A4Http.Request("http://localhost/api/settings/kpis", KpiBody(null, id: 1, kpi: "Latency"), writeSecret: A4Http.WriteSecret), CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        Assert.Equal(KpiProfiles.Default, update.ReadJson()["profileKey"]!.GetValue<string>());
        Assert.Equal(KpiProfiles.Default, _definitions.Upserts.Single().Definition.ProfileKey);

        // A create (id 0) without profileKey whose natural key already exists resolves to that row's profile,
        // so the clash is reported against 'arrivia' instead of silently creating a copy in 'acme'.
        var duplicate = await Admin().UpsertAsync(
            A4Http.Request("http://localhost/api/settings/kpis", KpiBody(null, kpi: "Latency"), writeSecret: A4Http.WriteSecret), CancellationToken.None);
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
        Assert.Single(_definitions.Upserts);

        // An explicit, different profileKey is a deliberate move and is audited by the store.
        var store = new InMemoryKpiDefinitionStore();
        var stored = await store.UpsertAsync(A4Http.Definition(0, KpiPillar.Application), "seed");
        var admin = new KpiDefinitionAdminFunctions(store, Monitoring(), new A4FakeTimeProvider(Now), NullLogger<KpiDefinitionAdminFunctions>.Instance, Resolver());

        var kept = await admin.UpsertAsync(
            A4Http.Request("http://localhost/api/settings/kpis", KpiBody(null, id: stored.KpiDefinitionId, kpi: "Latency"), writeSecret: A4Http.WriteSecret), CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, kept.StatusCode);
        Assert.Equal(KpiProfiles.Default, (await store.GetAsync(stored.KpiDefinitionId))!.ProfileKey);

        var moved = await admin.UpsertAsync(
            A4Http.Request("http://localhost/api/settings/kpis", KpiBody(Acme, id: stored.KpiDefinitionId, kpi: "Latency"), writeSecret: A4Http.WriteSecret), CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        Assert.Equal(Acme, (await store.GetAsync(stored.KpiDefinitionId))!.ProfileKey);
        var audit = await store.GetAuditAsync(stored.KpiDefinitionId, 10);
        Assert.Equal(3, audit.Count);
        Assert.Contains("\"ProfileKey\":\"acme\"", audit[0].SnapshotJson);
        Assert.Contains("\"ProfileKey\":\"arrivia\"", audit[1].SnapshotJson);
    }

    [Fact]
    public async Task SettingsApi_NewKpiWithoutProfileKey_UsesActiveProfile()
    {
        SeedTwoProfiles();

        var created = await Admin().UpsertAsync(
            A4Http.Request("http://localhost/api/settings/kpis", KpiBody(null, kpi: "Exceptions", domain: "air"), writeSecret: A4Http.WriteSecret), CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        Assert.Equal(Acme, created.ReadJson()["profileKey"]!.GetValue<string>());
        var upsert = _definitions.Upserts.Single();
        Assert.Equal(0, upsert.Definition.KpiDefinitionId);
        Assert.Equal(Acme, upsert.Definition.ProfileKey);

        // Nothing active: the configured default decides.
        _active.Active = null;
        var fallback = await Admin().UpsertAsync(
            A4Http.Request("http://localhost/api/settings/kpis", KpiBody(null, kpi: "Exceptions", domain: "cruises"), writeSecret: A4Http.WriteSecret), CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, fallback.StatusCode);
        Assert.Equal(KpiProfiles.Default, fallback.ReadJson()["profileKey"]!.GetValue<string>());
    }

    [Fact]
    public async Task PillarsApi_FiltersByActiveProfile()
    {
        SeedTwoProfiles();

        var response = await Health().GetPillarHealthAsync(A4Http.Request("http://localhost/api/health/pillars"), CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var pillars = response.ReadJson().AsArray();
        Assert.All(pillars, p => Assert.Equal(Acme, p!["profileKey"]!.GetValue<string>()));
        var kpiIds = pillars.SelectMany(p => p!["kpis"]!.AsArray()).Select(k => k!["kpiDefinitionId"]!.GetValue<int>()).OrderBy(id => id).ToArray();
        Assert.Equal([2, 3], kpiIds);
        Assert.All(pillars.SelectMany(p => p!["kpis"]!.AsArray()), k => Assert.Equal(Acme, k!["profileKey"]!.GetValue<string>()));

        var incumbent = (await Health().GetPillarHealthAsync(A4Http.Request("http://localhost/api/health/pillars?domain=hotels&profileKey=arrivia"), CancellationToken.None)).ReadJson().AsArray();
        Assert.Equal([1], incumbent.SelectMany(p => p!["kpis"]!.AsArray()).Select(k => k!["kpiDefinitionId"]!.GetValue<int>()).ToArray());

        var all = (await Health().GetPillarHealthAsync(A4Http.Request("http://localhost/api/health/pillars?profileKey=*"), CancellationToken.None)).ReadJson().AsArray();
        Assert.Equal(3, all.SelectMany(p => p!["kpis"]!.AsArray()).Count());

        var unknown = await Health().GetPillarHealthAsync(A4Http.Request("http://localhost/api/health/pillars?profileKey=ice"), CancellationToken.None);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
    }

    [Fact]
    public async Task TriageApi_ManualRunDefaultsToActiveProfile()
    {
        var submitted = await Triage().SubmitAsync(
            A4Http.Request("http://localhost/api/triage/investigations", TriageBody, internalSecret: A4Http.InternalSecret), CancellationToken.None);
        Assert.Equal(HttpStatusCode.Accepted, submitted.StatusCode);
        Assert.Equal(Acme, Assert.Single(_investigations.Items.Values).Signal.ProfileKey);

        var explicitBody = TriageBody.Replace("\"domain\":\"hotels\"", "\"domain\":\"cars\",\"profileKey\":\"arrivia\"", StringComparison.Ordinal);
        var explicitRun = await Triage().SubmitAsync(
            A4Http.Request("http://localhost/api/triage/investigations", explicitBody, internalSecret: A4Http.InternalSecret), CancellationToken.None);
        Assert.Equal(HttpStatusCode.Accepted, explicitRun.StatusCode);

        var unknownBody = TriageBody.Replace("\"domain\":\"hotels\"", "\"domain\":\"air\",\"profileKey\":\"ice\"", StringComparison.Ordinal);
        var unknown = await Triage().SubmitAsync(
            A4Http.Request("http://localhost/api/triage/investigations", unknownBody, internalSecret: A4Http.InternalSecret), CancellationToken.None);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Equal(2, _investigations.Items.Count);

        var list = (await Triage().GetRecentAsync(A4Http.Request("http://localhost/api/triage/investigations"), CancellationToken.None)).ReadJson().AsArray();
        var item = Assert.Single(list);
        Assert.Equal(Acme, item!["profileKey"]!.GetValue<string>());
        Assert.Equal(Acme, item["signal"]!["profileKey"]!.GetValue<string>());

        var all = (await Triage().GetRecentAsync(A4Http.Request("http://localhost/api/triage/investigations?profileKey=*"), CancellationToken.None)).ReadJson().AsArray();
        Assert.Equal(2, all.Count);
    }

    private sealed class FakeActiveStore : IActiveCompanyProfileStore
    {
        public string? Active { get; set; }

        public Task<string?> GetActiveAsync(CancellationToken ct = default) => Task.FromResult(Active);

        public Task<ActiveCompanyProfile?> GetActiveDetailAsync(CancellationToken ct = default) => throw new NotSupportedException();

        public Task SetActiveAsync(string profileKey, string changedBy, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
