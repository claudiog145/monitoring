using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring.Evidence;
using JourneyMonitor.Infrastructure.Fabric;
using JourneyMonitor.Infrastructure.Monitoring;
using JourneyMonitor.Tests.Spec1000.A5;
using Microsoft.Extensions.Logging.Abstractions;

namespace JourneyMonitor.Tests.Monitoring.Evidence;

/// <summary>AG5 (contract v4 §5): business evidence per profile - Fabric for the incumbent, KPI buckets for everyone else.</summary>
public sealed class KpiBusinessEvidenceTests
{
    private const string Acme = "acme";
    private static readonly DateTimeOffset DetectedAt = A5TriageFixtures.DetectedAt;
    private static readonly DateTimeOffset From = DetectedAt.AddHours(-3);
    private static readonly DateTimeOffset To = DetectedAt.AddMinutes(15);

    [Fact]
    public async Task BusinessEvidence_NonIncumbent_UsesKpiBuckets()
    {
        var definitions = new InMemoryKpiDefinitionStore();
        var acmeOrders = await definitions.UpsertAsync(Definition(KpiPillar.Business, "Orders", "orders", Acme), "test");
        var acmeRefunds = await definitions.UpsertAsync(Definition(KpiPillar.Business, "Refunds", "payments", Acme), "test");
        var acmeEntity = await definitions.UpsertAsync(Definition(KpiPillar.Business, "Orders", "orders", Acme) with { Grain = KpiGrain.Entity }, "test");
        var acmeApp = await definitions.UpsertAsync(Definition(KpiPillar.Application, "Latency", "orders", Acme), "test");
        var arriviaOrders = await definitions.UpsertAsync(Definition(KpiPillar.Business, "Orders", "orders", KpiProfiles.Default), "test");

        var buckets = new InMemoryKpiBucketStore();
        await buckets.UpsertAsync(
        [
            Bucket(acmeOrders.KpiDefinitionId, DetectedAt.AddMinutes(-10), 80),
            Bucket(acmeOrders.KpiDefinitionId, DetectedAt.AddMinutes(-5), 40),
            Bucket(acmeOrders.KpiDefinitionId, DetectedAt.AddMinutes(-10) - KpiBusinessEvidenceProvider.ReferenceOffset, 100),
            Bucket(acmeOrders.KpiDefinitionId, DetectedAt.AddMinutes(-5) - KpiBusinessEvidenceProvider.ReferenceOffset, 100),
            Bucket(acmeOrders.KpiDefinitionId, DetectedAt.AddDays(-1), 999),
            Bucket(arriviaOrders.KpiDefinitionId, DetectedAt.AddMinutes(-5), 5000),
            Bucket(acmeApp.KpiDefinitionId, DetectedAt.AddMinutes(-5), 7000),
        ]);

        var anomalies = new InMemoryKpiAnomalyStore(definitions);
        await anomalies.AddAsync(Anomaly(acmeOrders.KpiDefinitionId, DetectedAt, 40, 100));
        await anomalies.AddAsync(Anomaly(acmeOrders.KpiDefinitionId, DetectedAt.AddDays(-2), 10, 100));
        await anomalies.AddAsync(Anomaly(arriviaOrders.KpiDefinitionId, DetectedAt, 5000, 9000));
        await anomalies.AddAsync(Anomaly(acmeEntity.KpiDefinitionId, DetectedAt, 1, 2) with { EntityKey = "club-1" });

        var fabric = new A5HotelKpiSource();
        var kpiProvider = new KpiBusinessEvidenceProvider(definitions, buckets, anomalies, NullLogger<KpiBusinessEvidenceProvider>.Instance);
        var provider = new FabricBusinessEvidenceProvider(fabric, NullLogger<FabricBusinessEvidenceProvider>.Instance, kpiProvider);

        var section = await provider.CollectAsync(Signal(Acme, "orders"), From, To);

        Assert.True(section.Available, section.UnavailableReason);
        Assert.Equal(TriageDimensions.BusinessHealth, section.Dimension);
        Assert.Equal(0, fabric.Calls);
        var data = section.Data!.Value;
        Assert.Equal(KpiBusinessEvidenceProvider.SourceName, data.GetProperty("source").GetString());
        Assert.Equal(Acme, data.GetProperty("profileKey").GetString());
        Assert.Equal("orders", data.GetProperty("domain").GetString());

        var kpi = Assert.Single(data.GetProperty("kpis").EnumerateArray());
        Assert.Equal(acmeOrders.KpiDefinitionId, kpi.GetProperty("kpiDefinitionId").GetInt32());
        Assert.Equal(2, kpi.GetProperty("buckets").GetArrayLength());
        Assert.Equal(40, kpi.GetProperty("latest").GetProperty("value").GetDouble());
        Assert.Equal(DetectedAt.AddMinutes(-5), kpi.GetProperty("latest").GetProperty("bucketStart").GetDateTimeOffset());
        Assert.Equal(100, kpi.GetProperty("reference").GetProperty("value").GetDouble());
        Assert.Equal(-60, kpi.GetProperty("deviation").GetDouble());
        Assert.Equal(-60, kpi.GetProperty("deviationPercent").GetDouble(), 6);

        var anomaly = Assert.Single(data.GetProperty("anomalies").EnumerateArray());
        Assert.Equal(acmeOrders.KpiDefinitionId, anomaly.GetProperty("kpiDefinitionId").GetInt32());
        Assert.Equal(40, anomaly.GetProperty("observedValue").GetDouble());
        Assert.Equal(100, anomaly.GetProperty("referenceValue").GetDouble());

        // Without a domain the profile's every Business domain is read, still only its own.
        var all = await provider.CollectAsync(Signal(Acme, null), From, To);
        Assert.True(all.Available);
        var ids = all.Data!.Value.GetProperty("kpis").EnumerateArray().Select(k => k.GetProperty("kpiDefinitionId").GetInt32()).OrderBy(i => i).ToArray();
        Assert.Equal(new[] { acmeOrders.KpiDefinitionId, acmeRefunds.KpiDefinitionId }.OrderBy(i => i).ToArray(), ids);
        Assert.DoesNotContain("5000", all.Data!.Value.GetRawText());

        // A profile without Business KPIs reports the dimension unavailable instead of borrowing one.
        var none = await provider.CollectAsync(Signal("globex", "orders"), From, To);
        Assert.False(none.Available);
        Assert.Contains("globex", none.UnavailableReason);
        Assert.Equal(0, fabric.Calls);
    }

    [Fact]
    public async Task BusinessEvidence_Incumbent_StillFabric()
    {
        var definitions = new InMemoryKpiDefinitionStore();
        var arriviaOrders = await definitions.UpsertAsync(Definition(KpiPillar.Business, "Orders", "hotels", KpiProfiles.Default), "test");
        var buckets = new RecordingBucketStore();
        await buckets.UpsertAsync([Bucket(arriviaOrders.KpiDefinitionId, DetectedAt.AddMinutes(-5), 10)]);
        var kpiProvider = new KpiBusinessEvidenceProvider(
            definitions, buckets, new InMemoryKpiAnomalyStore(definitions), NullLogger<KpiBusinessEvidenceProvider>.Instance);
        var fabric = new RowsHotelKpiSource();
        var provider = new FabricBusinessEvidenceProvider(fabric, NullLogger<FabricBusinessEvidenceProvider>.Instance, kpiProvider);

        foreach (var profileKey in new string?[] { KpiProfiles.Default, null, "ARRIVIA" })
        {
            var section = await provider.CollectAsync(Signal(profileKey, "hotels"), From, To);
            Assert.True(section.Available, section.UnavailableReason);
            Assert.Equal(HotelBookingKpiResponse.ViewName, section.Data!.Value.GetProperty("source").GetString());
            Assert.Equal(1, section.Data!.Value.GetProperty("rows").GetArrayLength());
        }
        Assert.Equal(3, fabric.Calls);

        // The incumbent's non-hotels domains keep the existing answer: no business source, no KPI fallback.
        var air = await provider.CollectAsync(Signal(KpiProfiles.Default, "air"), From, To);
        Assert.False(air.Available);
        Assert.Equal("no business source for air", air.UnavailableReason);
        Assert.Equal(0, buckets.Reads);

        Assert.False(KpiBusinessEvidenceProvider.AppliesTo(Signal(KpiProfiles.Default, "hotels")));
        Assert.False(KpiBusinessEvidenceProvider.AppliesTo(Signal(null, "hotels")));
        Assert.True(KpiBusinessEvidenceProvider.AppliesTo(Signal(Acme, "hotels")));

        // Without the KPI provider wired, Fabric still declines another profile rather than leak incumbent rows.
        var bare = new FabricBusinessEvidenceProvider(fabric, NullLogger<FabricBusinessEvidenceProvider>.Instance);
        var declined = await bare.CollectAsync(Signal(Acme, "hotels"), From, To);
        Assert.False(declined.Available);
        Assert.StartsWith(FabricBusinessEvidenceProvider.IncumbentOnlyReason, declined.UnavailableReason);
        Assert.Equal(3, fabric.Calls);
    }

    [Fact]
    public async Task Evidence_FilteredByProfileKey()
    {
        var definitions = new InMemoryKpiDefinitionStore();
        var acmeLatency = await definitions.UpsertAsync(Definition(KpiPillar.Application, "Latency", "hotels", Acme), "test");
        var arriviaLatency = await definitions.UpsertAsync(Definition(KpiPillar.Application, "Latency", "hotels", KpiProfiles.Default), "test");
        var buckets = new RecordingBucketStore();
        await buckets.UpsertAsync(
        [
            Bucket(acmeLatency.KpiDefinitionId, DetectedAt.AddMinutes(-5), 300),
            Bucket(arriviaLatency.KpiDefinitionId, DetectedAt.AddMinutes(-5), 4200),
        ]);
        var incidents = new A5IncidentReader();
        var application = new ApplicationEvidenceProvider(definitions, buckets, incidents, NullLogger<ApplicationEvidenceProvider>.Instance);

        var acme = await application.CollectAsync(Signal(Acme, "hotels"), From, To);

        Assert.True(acme.Available);
        var acmeKpi = Assert.Single(acme.Data!.Value.GetProperty("kpis").EnumerateArray());
        Assert.Equal(acmeLatency.KpiDefinitionId, acmeKpi.GetProperty("kpiDefinitionId").GetInt32());
        Assert.Equal(Acme, acme.Data!.Value.GetProperty("profileKey").GetString());
        // Incidents are the incumbent's exception ingestion; another profile never reads them.
        Assert.Equal(0, acme.Data!.Value.GetProperty("incidents").GetArrayLength());
        Assert.Null(incidents.LastQuery);
        Assert.DoesNotContain(buckets.ReadIds, id => id == arriviaLatency.KpiDefinitionId);

        var arrivia = await application.CollectAsync(Signal(KpiProfiles.Default, "hotels"), From, To);
        var arriviaKpi = Assert.Single(arrivia.Data!.Value.GetProperty("kpis").EnumerateArray());
        Assert.Equal(arriviaLatency.KpiDefinitionId, arriviaKpi.GetProperty("kpiDefinitionId").GetInt32());
        Assert.Equal(1, arrivia.Data!.Value.GetProperty("incidents").GetArrayLength());

        var retriever = new A5KnowledgeRetriever(2);
        var knowledge = new KnowledgeEvidenceProvider(retriever);
        await knowledge.CollectAsync(Signal(Acme, "hotels"), From, To);
        Assert.Equal(Acme, retriever.LastProfileKey);
        await knowledge.CollectAsync(Signal(null, "hotels"), From, To);
        Assert.Equal(KpiProfiles.Default, retriever.LastProfileKey);
    }

    private static TriageSignal Signal(string? profileKey, string? domain) =>
        A5TriageFixtures.Signal(domain) with { Pillar = "Business Health", Kpi = "Orders", ProfileKey = profileKey };

    private static KpiDefinition Definition(KpiPillar pillar, string kpi, string domain, string profileKey) => new(
        0, pillar, kpi, domain, "", "kql:orders", KpiDirection.Decrease, KpiAggregation.Count,
        TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5), TimeSpan.FromHours(1), 0, 0,
        DetectionMethodKind.BaselinePercent, """{"percent":30}""", 2, 60, true, A5TriageFixtures.Now, "test")
    {
        ProfileKey = profileKey,
    };

    private static KpiBucket Bucket(int id, DateTimeOffset start, double value) =>
        new(id, start, (long)value, 0, null, null, null, null, value);

    private static KpiAnomaly Anomaly(int id, DateTimeOffset at, double observed, double reference) =>
        new(0, id, at, at.AddMinutes(-5), observed, reference, observed - reference, "baseline drop", null);

    private sealed class RecordingBucketStore : IKpiBucketStore
    {
        private readonly InMemoryKpiBucketStore _inner = new();

        public int Reads { get; private set; }

        public List<int> ReadIds { get; } = [];

        public Task UpsertAsync(IReadOnlyList<KpiBucket> buckets, CancellationToken ct = default) => _inner.UpsertAsync(buckets, ct);

        public Task<IReadOnlyList<KpiBucket>> GetRangeAsync(int kpiDefinitionId, DateTimeOffset fromInclusive, DateTimeOffset toExclusive, CancellationToken ct = default)
        {
            Reads++;
            ReadIds.Add(kpiDefinitionId);
            return _inner.GetRangeAsync(kpiDefinitionId, fromInclusive, toExclusive, ct);
        }
    }

    private sealed class RowsHotelKpiSource : IHotelBookingKpiSource
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<HotelBookingKpiResponse>> ReadAlertStatusAsync(CancellationToken cancellationToken)
        {
            Calls++;
            IReadOnlyList<HotelBookingKpiResponse> rows =
            [
                new HotelBookingKpiResponse
                {
                    Grain = "total",
                    AlertLevel = "red",
                    GrossBookingsStatus = "red",
                    CancelBookingsStatus = "ok",
                    NetBookingsStatus = "red",
                    GrossSalesStatus = "ok",
                    NetSalesStatus = "ok",
                    MarginStatus = "ok",
                    MarginRateStatus = "ok",
                    NegMarginStatus = "ok",
                    DeepNegMarginStatus = "ok",
                },
            ];
            return Task.FromResult(rows);
        }
    }
}
