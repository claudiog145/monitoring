using JourneyMonitor.Core.Domain;
using JourneyMonitor.Infrastructure.Sql;
using Xunit.Abstractions;

namespace JourneyMonitor.Tests.Spec1000.A3;

public class KpiSqlStoresIntegrationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task SqlKpiStores_RoundTrip()
    {
        var connectionString = A3SqlTestDatabase.TryGetConnectionString(output);
        if (connectionString is null)
        {
            return;
        }

        var options = A3SqlTestDatabase.Options(connectionString);
        var definitions = new SqlKpiDefinitionStore(options);
        var buckets = new SqlKpiBucketStore(options);
        var samples = new SqlKpiSampleStore(options);
        var states = new SqlKpiStateStore(options);
        var anomalies = new SqlKpiAnomalyStore(options);
        var watermarks = new SqlIngestionWatermarkStore(options);

        var kpiName = "A3TEST-" + Guid.NewGuid().ToString("N")[..20];
        var id = 0;
        try
        {
            // Definition: insert, read back, update, audit newest first.
            var inserted = await definitions.UpsertAsync(A3Kpi.Definition(kpiName), "a3test-alice");
            id = inserted.KpiDefinitionId;
            Assert.True(id > 0);
            Assert.Equal(A3Kpi.Definition(kpiName) with { KpiDefinitionId = id, UpdatedAt = inserted.UpdatedAt, UpdatedBy = "a3test-alice" }, inserted);
            Assert.Equal(inserted, await definitions.GetAsync(id));
            Assert.Contains(await definitions.GetAllAsync(), d => d.KpiDefinitionId == id);
            Assert.Contains(await definitions.GetEnabledAsync(KpiPillar.Application), d => d.KpiDefinitionId == id);
            Assert.DoesNotContain(await definitions.GetEnabledAsync(KpiPillar.Business), d => d.KpiDefinitionId == id);

            var updated = await definitions.UpsertAsync(inserted with { Enabled = false, CooldownMinutes = 60, Stage = "search" }, "a3test-bob");
            Assert.Equal(id, updated.KpiDefinitionId);
            Assert.False(updated.Enabled);
            Assert.Equal(60, updated.CooldownMinutes);
            Assert.Equal("search", updated.Stage);
            Assert.Equal(TimeSpan.FromHours(1), updated.BucketSize);
            Assert.Equal(DetectionMethodKind.BaselinePercent, updated.DetectionMethod);
            Assert.DoesNotContain(await definitions.GetEnabledAsync(null), d => d.KpiDefinitionId == id);

            var audit = await definitions.GetAuditAsync(id, 200);
            Assert.Equal(2, audit.Count);
            Assert.Equal("a3test-bob", audit[0].ChangedBy);
            Assert.Equal("a3test-alice", audit[1].ChangedBy);
            Assert.Contains("\"CooldownMinutes\":60", audit[0].SnapshotJson);

            // Buckets: duplicate keys inside one batch collapse (last wins), re-upsert replaces, range is [from, to).
            var t0 = new DateTimeOffset(2001, 1, 1, 10, 0, 0, TimeSpan.Zero);
            await buckets.UpsertAsync(
            [
                A3Kpi.Bucket(id, t0.AddHours(2), 5, 50),
                A3Kpi.Bucket(id, t0, 1, 10),
                A3Kpi.Bucket(id, t0.AddHours(1), 3, 30),
                A3Kpi.Bucket(id, t0.ToOffset(TimeSpan.FromHours(-3)), 2, 20),
                A3Kpi.Bucket(id, t0.AddHours(3), 7, 70),
            ]);
            await buckets.UpsertAsync([A3Kpi.Bucket(id, t0.AddHours(1), 33, 330.25) with { Value = 0.125, P50 = null }]);

            var range = await buckets.GetRangeAsync(id, t0, t0.AddHours(3));
            Assert.Equal(
            [
                A3Kpi.Bucket(id, t0, 2, 20),
                A3Kpi.Bucket(id, t0.AddHours(1), 33, 330.25) with { Value = 0.125, P50 = null },
                A3Kpi.Bucket(id, t0.AddHours(2), 5, 50),
            ], range);

            // 1500 rows go out as two procedure calls (see KpiBatchPayloadTests for the 1000 split itself).
            var many = Enumerable.Range(0, 1500).Select(i => A3Kpi.Bucket(id, t0.AddDays(10).AddMinutes(i), i, i)).ToList();
            await buckets.UpsertAsync(many);
            Assert.Equal(1500, (await buckets.GetRangeAsync(id, t0.AddDays(10), t0.AddDays(12))).Count);

            // Samples: ordered by timestamp, top honoured.
            await samples.AddAsync(
            [
                new KpiSample(0, id, t0, t0.AddMinutes(2), 2.5, "op-b"),
                new KpiSample(0, id, t0, t0.AddMinutes(1), 1.5, "op-a"),
                new KpiSample(0, id, t0, t0.AddMinutes(3), 3.5, null),
            ]);
            var read = await samples.GetAsync(id, t0, 2);
            Assert.Equal(["op-a", "op-b"], read.Select(s => s.OperationId));
            Assert.All(read, s => Assert.True(s.SampleId > 0));
            Assert.Equal(new KpiSample(read[0].SampleId, id, t0, t0.AddMinutes(1), 1.5, "op-a"), read[0]);

            // Purge is global by BucketStart, so it only ever runs with a cutoff in 1900: older than any
            // real KPI data in the shared database. Only this KPI's 1900 samples may go; t0's must stay.
            var ancient = new DateTimeOffset(1900, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(Random.Shared.Next(0, 60 * 24 * 300));
            await samples.AddAsync(
            [
                new KpiSample(0, id, ancient, ancient.AddSeconds(1), 1, "old-1"),
                new KpiSample(0, id, ancient, ancient.AddSeconds(2), 2, "old-2"),
            ]);
            Assert.True(await samples.PurgeOlderThanAsync(ancient.AddMinutes(1)) >= 2);
            Assert.Empty(await samples.GetAsync(id, ancient, 10));
            Assert.Equal(3, (await samples.GetAsync(id, t0, 10)).Count);

            // Detection state: missing row is Initial, then round trip.
            Assert.Equal(KpiDetectionState.Initial(id), await states.GetAsync(id));
            var state = new KpiDetectionState(id, 2, t0.AddHours(1), t0.AddHours(4), t0.AddHours(2).AddMinutes(5));
            await states.SaveAsync(state);
            Assert.Equal(state, await states.GetAsync(id));
            var reset = state with { ConsecutiveCount = 0, CooldownUntil = null };
            await states.SaveAsync(reset);
            Assert.Equal(reset, await states.GetAsync(id));

            // Anomaly: generated id, attach investigation, filtered newest-first read.
            var detectedAt = DateTimeOffset.UtcNow.AddYears(50);
            var anomaly = await anomalies.AddAsync(new KpiAnomaly(0, id, detectedAt, t0, 812.5, 400, 103.1, "2 consecutive increases", null));
            Assert.True(anomaly.AnomalyId > 0);
            var investigationId = Guid.NewGuid();
            await anomalies.AttachInvestigationAsync(anomaly.AnomalyId, investigationId);
            var recent = await anomalies.GetRecentAsync(KpiPillar.Application, "hotels", 50);
            var stored = Assert.Single(recent, a => a.AnomalyId == anomaly.AnomalyId);
            Assert.Equal(anomaly with { InvestigationId = investigationId }, stored);
            Assert.DoesNotContain(await anomalies.GetRecentAsync(KpiPillar.Business, null, 50), a => a.AnomalyId == anomaly.AnomalyId);
            Assert.DoesNotContain(await anomalies.GetRecentAsync(null, "cars", 50), a => a.AnomalyId == anomaly.AnomalyId);

            // Watermark: missing is null, never moves backwards.
            Assert.Null(await watermarks.GetAsync(id));
            await watermarks.SetAsync(id, t0.AddHours(2));
            Assert.Equal(t0.AddHours(2), await watermarks.GetAsync(id));
            await watermarks.SetAsync(id, t0.AddHours(1));
            Assert.Equal(t0.AddHours(2), await watermarks.GetAsync(id));
            await watermarks.SetAsync(id, t0.AddHours(3));
            Assert.Equal(t0.AddHours(3), await watermarks.GetAsync(id));

            output.WriteLine($"KPI stores round trip verified for KpiDefinitionId {id}.");
        }
        finally
        {
            if (id > 0)
            {
                await A3SqlTestDatabase.DeleteKpiDataAsync(connectionString, id);
            }
        }
    }

    [Fact]
    public async Task SqlIngestionWatermarkStore_ConcurrentSetKeepsHighest()
    {
        var connectionString = A3SqlTestDatabase.TryGetConnectionString(output);
        if (connectionString is null)
        {
            return;
        }

        // IngestionWatermark has no foreign key; a negative id can never belong to a real KPI.
        var id = -Random.Shared.Next(1_000_000, int.MaxValue);
        var store = new SqlIngestionWatermarkStore(A3SqlTestDatabase.Options(connectionString));
        var t0 = new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);
        try
        {
            for (var round = 0; round < 3; round++)
            {
                var values = Enumerable.Range(0, 6).Select(i => t0.AddHours(round * 10 + i)).OrderBy(_ => Random.Shared.Next()).ToList();
                await Task.WhenAll(values.Select(v => Task.Run(() => store.SetAsync(id, v))));
                Assert.Equal(values.Max(), await store.GetAsync(id));
            }

            // A late, older writer arriving after the newest one must not move it back.
            await Task.WhenAll(store.SetAsync(id, t0), store.SetAsync(id, t0.AddHours(1)));
            Assert.Equal(t0.AddHours(25), await store.GetAsync(id));
            output.WriteLine($"Concurrent watermark writes verified for id {id}.");
        }
        finally
        {
            await A3SqlTestDatabase.DeleteKpiDataAsync(connectionString, id);
        }
    }
}
