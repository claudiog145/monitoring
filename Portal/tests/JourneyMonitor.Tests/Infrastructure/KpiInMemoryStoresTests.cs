using JourneyMonitor.Core.Domain;
using JourneyMonitor.Infrastructure.Monitoring;

namespace JourneyMonitor.Tests.Spec1000.A3;

public class KpiInMemoryStoresTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task InMemoryKpiBucketStore_UpsertIsIdempotent()
    {
        var store = new InMemoryKpiBucketStore();

        await store.UpsertAsync([A3Kpi.Bucket(7, T0, 10, 100)]);
        await store.UpsertAsync([A3Kpi.Bucket(7, T0, 50, 250)]);

        var rows = await store.GetRangeAsync(7, T0, T0.AddHours(1));
        var row = Assert.Single(rows);
        Assert.Equal(A3Kpi.Bucket(7, T0, 50, 250), row);
    }

    [Fact]
    public async Task InMemoryKpiBucketStore_UpsertWithDuplicateKeysInOneBatch_LastOneWins()
    {
        var store = new InMemoryKpiBucketStore();
        // Same instant expressed with a different offset is the same datetimeoffset key in SQL.
        var sameInstant = T0.ToOffset(TimeSpan.FromHours(-3));

        await store.UpsertAsync([A3Kpi.Bucket(7, T0, 10, 100), A3Kpi.Bucket(7, sameInstant, 20, 200)]);

        var row = Assert.Single(await store.GetRangeAsync(7, T0, T0.AddHours(1)));
        Assert.Equal(20, row.TotalCount);
    }

    [Fact]
    public async Task InMemoryKpiBucketStore_GetRangeIsHalfOpenAndOrdered()
    {
        var store = new InMemoryKpiBucketStore();
        await store.UpsertAsync(
        [
            A3Kpi.Bucket(7, T0.AddHours(3), 1, 1),
            A3Kpi.Bucket(7, T0.AddHours(1), 1, 1),
            A3Kpi.Bucket(7, T0, 1, 1),
            A3Kpi.Bucket(7, T0.AddHours(-1), 1, 1),
            A3Kpi.Bucket(7, T0.AddHours(2), 1, 1),
            A3Kpi.Bucket(8, T0.AddHours(1), 1, 1),
        ]);

        var rows = await store.GetRangeAsync(7, T0, T0.AddHours(3));

        Assert.Equal([T0, T0.AddHours(1), T0.AddHours(2)], rows.Select(r => r.BucketStart));
        Assert.All(rows, r => Assert.Equal(7, r.KpiDefinitionId));
    }

    [Fact]
    public async Task InMemoryKpiStateStore_ReturnsDefaultStateWhenMissing()
    {
        var store = new InMemoryKpiStateStore();

        Assert.Equal(KpiDetectionState.Initial(42), await store.GetAsync(42));

        var saved = new KpiDetectionState(42, 2, T0, T0.AddHours(2), T0.AddMinutes(5));
        await store.SaveAsync(saved);
        Assert.Equal(saved, await store.GetAsync(42));
        Assert.Equal(KpiDetectionState.Initial(43), await store.GetAsync(43));
    }

    [Fact]
    public async Task InMemoryKpiDefinitionStore_UpsertWritesAudit()
    {
        var store = new InMemoryKpiDefinitionStore();

        var inserted = await store.UpsertAsync(A3Kpi.Definition("A3TEST-Latency"), "alice");
        Assert.NotEqual(0, inserted.KpiDefinitionId);
        Assert.Equal("alice", inserted.UpdatedBy);

        var updated = await store.UpsertAsync(inserted with { Enabled = false, CooldownMinutes = 60 }, "bob");
        Assert.Equal(inserted.KpiDefinitionId, updated.KpiDefinitionId);
        Assert.Equal(60, (await store.GetAsync(inserted.KpiDefinitionId))!.CooldownMinutes);

        var audit = await store.GetAuditAsync(inserted.KpiDefinitionId, 200);
        Assert.Equal(2, audit.Count);
        Assert.Equal("bob", audit[0].ChangedBy);
        Assert.Equal("alice", audit[1].ChangedBy);
        Assert.True(audit[0].AuditId > audit[1].AuditId);
        Assert.Contains("\"CooldownMinutes\":60", audit[0].SnapshotJson);
        Assert.Contains("\"BucketSizeSec\":3600", audit[0].SnapshotJson);
    }

    [Fact]
    public async Task InMemoryKpiDefinitionStore_FiltersEnabledByPillarAndRejectsUnknownOrDuplicate()
    {
        var store = new InMemoryKpiDefinitionStore();
        var app = await store.UpsertAsync(A3Kpi.Definition("Latency"), "seed");
        await store.UpsertAsync(A3Kpi.Definition("Bookings", KpiPillar.Business), "seed");
        await store.UpsertAsync(A3Kpi.Definition("Exceptions", enabled: false), "seed");

        Assert.Equal(3, (await store.GetAllAsync()).Count);
        var enabledApp = Assert.Single(await store.GetEnabledAsync(KpiPillar.Application));
        Assert.Equal(app.KpiDefinitionId, enabledApp.KpiDefinitionId);
        Assert.Equal(2, (await store.GetEnabledAsync(null)).Count);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpsertAsync(A3Kpi.Definition("Latency"), "x"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpsertAsync(A3Kpi.Definition("New") with { KpiDefinitionId = 999 }, "x"));
    }

    [Fact]
    public async Task InMemoryKpiAnomalyStore_AttachInvestigation()
    {
        var definitions = new InMemoryKpiDefinitionStore();
        var hotels = await definitions.UpsertAsync(A3Kpi.Definition("Latency"), "seed");
        var cars = await definitions.UpsertAsync(A3Kpi.Definition("Latency", domain: "cars"), "seed");
        var store = new InMemoryKpiAnomalyStore(definitions);

        var first = await store.AddAsync(new KpiAnomaly(0, hotels.KpiDefinitionId, T0, T0.AddHours(-1), 800, 400, 100, "d", null));
        var second = await store.AddAsync(new KpiAnomaly(0, cars.KpiDefinitionId, T0.AddMinutes(5), T0.AddHours(-1), 800, 400, 100, "d", null));
        Assert.True(second.AnomalyId > first.AnomalyId);

        var investigationId = Guid.NewGuid();
        await store.AttachInvestigationAsync(first.AnomalyId, investigationId);

        var recent = await store.GetRecentAsync(null, null, 10);
        Assert.Equal([second.AnomalyId, first.AnomalyId], recent.Select(a => a.AnomalyId));
        Assert.Equal(investigationId, recent[1].InvestigationId);
        Assert.Null(recent[0].InvestigationId);

        var hotelsOnly = Assert.Single(await store.GetRecentAsync(KpiPillar.Application, "hotels", 10));
        Assert.Equal(first.AnomalyId, hotelsOnly.AnomalyId);
        Assert.Empty(await store.GetRecentAsync(KpiPillar.Business, null, 10));
    }

    [Fact]
    public async Task InMemoryKpiSampleStore_OrdersByTimestampAndPurges()
    {
        var store = new InMemoryKpiSampleStore();
        await store.AddAsync(
        [
            new KpiSample(0, 7, T0, T0.AddMinutes(2), 2, "b"),
            new KpiSample(0, 7, T0, T0.AddMinutes(1), 1, "a"),
            new KpiSample(0, 7, T0.AddHours(-2), T0.AddHours(-2), 9, null),
        ]);

        var samples = await store.GetAsync(7, T0, 10);
        Assert.Equal(["a", "b"], samples.Select(s => s.OperationId));
        Assert.All(samples, s => Assert.True(s.SampleId > 0));

        Assert.Equal(1, await store.PurgeOlderThanAsync(T0));
        Assert.Empty(await store.GetAsync(7, T0.AddHours(-2), 10));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public async Task InMemoryKpiStores_RejectNonFiniteValuesLikeSql(double bad)
    {
        var buckets = new InMemoryKpiBucketStore();
        var samples = new InMemoryKpiSampleStore();

        await Assert.ThrowsAsync<ArgumentException>(() => buckets.UpsertAsync([A3Kpi.Bucket(7, T0, 1, 1) with { Value = bad }]));
        await Assert.ThrowsAsync<ArgumentException>(() => samples.AddAsync([new KpiSample(0, 7, T0, T0, bad, null)]));

        Assert.Empty(await buckets.GetRangeAsync(7, T0, T0.AddHours(1)));
        Assert.Empty(await samples.GetAsync(7, T0, 10));
    }

    [Fact]
    public async Task InMemoryIngestionWatermarkStore_NeverMovesBackwards()
    {
        var store = new InMemoryIngestionWatermarkStore();
        Assert.Null(await store.GetAsync(7));

        await store.SetAsync(7, T0);
        await store.SetAsync(7, T0.AddHours(-1));
        Assert.Equal(T0, await store.GetAsync(7));

        await store.SetAsync(7, T0.AddHours(1));
        Assert.Equal(T0.AddHours(1), await store.GetAsync(7));
    }
}
