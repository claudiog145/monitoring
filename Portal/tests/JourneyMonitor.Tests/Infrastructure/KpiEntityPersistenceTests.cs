using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Infrastructure.Monitoring;
using JourneyMonitor.Infrastructure.Sql;
using JourneyMonitor.Tests.Spec1000.A3;
using Microsoft.Data.SqlClient;
using Xunit.Abstractions;

namespace JourneyMonitor.Tests.Spec1000.SD1;

/// <summary>
/// SD1 - contract v4 section 1 / section 4 persistence. Every test runs against the in-memory stores and,
/// when SPEC1000_SQL_CONNECTION is set, against the real SQL stores with the same assertions.
/// </summary>
public class KpiEntityPersistenceTests(ITestOutputHelper output)
{
    private static readonly DateTimeOffset Now = KpiTime.FloorToBucket(DateTimeOffset.UtcNow, TimeSpan.FromHours(1));

    // Newer than any real anomaly in a shared database, so TOP 1 reads see this test's rows first.
    private static readonly DateTimeOffset Future = new(2090, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // ------------------------------------------------------------------ required tests

    [Fact]
    public async Task EntityBuckets_PkIncludesEntityKey()
    {
        await AssertEntityBucketsAreDistinctSeriesAsync(new InMemoryKpiBucketStore(), 7);

        // The SQL batch carries the entity columns and only collapses true duplicates of the wider key.
        var payloads = SqlKpiBucketStore.BuildBatchPayloads(
        [
            A3Kpi.Bucket(7, Now, 1, 10),
            A3Kpi.Bucket(7, Now, 2, 20) with { EntityKey = "hotel-1", FailedCount = 3 },
            A3Kpi.Bucket(7, Now, 4, 40) with { EntityKey = "HOTEL-1", FailedCount = 5 },
            A3Kpi.Bucket(7, Now, 6, 60) with { EntityKey = "hotel-2" },
        ]);
        using (var doc = JsonDocument.Parse(Assert.Single(payloads)))
        {
            var rows = doc.RootElement.EnumerateArray().ToList();
            Assert.Equal(3, rows.Count);
            Assert.Equal(["", "HOTEL-1", "hotel-2"], rows.Select(r => r.GetProperty("entityKey").GetString()));
            Assert.Equal(5, rows[1].GetProperty("failedCount").GetInt64());
        }
        Assert.Throws<ArgumentException>(() => SqlKpiBucketStore.BuildBatchPayloads([A3Kpi.Bucket(7, Now, 1, 1) with { EntityKey = new string('x', 257) }]));
        Assert.Throws<ArgumentException>(() => SqlKpiBucketStore.BuildBatchPayloads([A3Kpi.Bucket(7, Now, 1, 1) with { EntityKey = null! }]));

        await WithSqlDefinitionAsync(KpiGrain.Entity, async (connectionString, id) =>
        {
            await AssertEntityBucketsAreDistinctSeriesAsync(new SqlKpiBucketStore(A3SqlTestDatabase.Options(connectionString)), id);

            await using var connection = new SqlConnection(connectionString);
            Assert.Equal("KpiDefinitionId,EntityKey,BucketStart", await PrimaryKeyColumnsAsync(connection, "monitoring.KpiBucket"));
            Assert.Equal("KpiDefinitionId,EntityKey", await PrimaryKeyColumnsAsync(connection, "monitoring.KpiDetectionState"));
        });
    }

    [Fact]
    public async Task DomainBuckets_EmptyEntityKey_BehaveAsV3()
    {
        var definitions = new InMemoryKpiDefinitionStore();
        var inserted = await definitions.UpsertAsync(A3Kpi.Definition("SD1-domain"), "sd1");
        await AssertDomainBehavesAsV3Async(
            definitions, new InMemoryKpiBucketStore(), new InMemoryKpiStateStore(), new InMemoryKpiAnomalyStore(definitions), inserted);

        // Grain is part of the natural key: the same KPI may exist once per grain, never twice per grain.
        var entity = await definitions.UpsertAsync(A3Kpi.Definition("SD1-domain") with { Grain = KpiGrain.Entity, MaxEntitiesPerBucket = 42 }, "sd1");
        Assert.Equal(KpiGrain.Entity, entity.Grain);
        Assert.Equal(42, entity.MaxEntitiesPerBucket);
        Assert.Contains("\"Grain\":\"entity\"", (await definitions.GetAuditAsync(entity.KpiDefinitionId, 1))[0].SnapshotJson);
        await Assert.ThrowsAsync<InvalidOperationException>(() => definitions.UpsertAsync(A3Kpi.Definition("SD1-domain"), "sd1"));

        await WithSqlDefinitionAsync(KpiGrain.Domain, async (connectionString, id) =>
        {
            var options = A3SqlTestDatabase.Options(connectionString);
            var sqlDefinitions = new SqlKpiDefinitionStore(options);
            var stored = await sqlDefinitions.GetAsync(id);
            Assert.NotNull(stored);
            await AssertDomainBehavesAsV3Async(
                sqlDefinitions, new SqlKpiBucketStore(options), new SqlKpiStateStore(options), new SqlKpiAnomalyStore(options), stored);

            // A caller written before contract v4 (no @Grain) keeps the stored grain on update.
            var asEntity = await sqlDefinitions.UpsertAsync(stored with { Grain = KpiGrain.Entity, MaxEntitiesPerBucket = 42 }, "sd1test");
            Assert.Equal((KpiGrain.Entity, 42), (asEntity.Grain, asEntity.MaxEntitiesPerBucket));
            await using var connection = new SqlConnection(connectionString);
            await connection.ExecuteAsync(
                "monitoring.usp_UpsertKpiDefinition",
                new
                {
                    KpiDefinitionId = id, Pillar = "Application", Kpi = stored.Kpi, Domain = "hotels", Stage = "", SourceKey = stored.SourceKey,
                    Direction = "Increase", Aggregation = "P95", EvaluationFrequencySec = 300, BucketSizeSec = 3600, LookbackWindowSec = 86400,
                    SampleSize = 4000, MinObservationsPerBucket = 30, DetectionMethod = "BaselinePercent", MethodParamsJson = stored.MethodParamsJson,
                    ConsecutiveCount = 2, CooldownMinutes = 90, Enabled = false, ChangedBy = "sd1test-v3", ProfileKey = stored.ProfileKey,
                },
                commandType: System.Data.CommandType.StoredProcedure);
            var afterV3 = await sqlDefinitions.GetAsync(id);
            Assert.Equal((KpiGrain.Entity, 42, 90), (afterV3!.Grain, afterV3.MaxEntitiesPerBucket, afterV3.CooldownMinutes));
        });
    }

    [Fact]
    public async Task EntityState_IsPerEntity()
    {
        await AssertStateIsPerEntityAsync(new InMemoryKpiStateStore(), 7);

        await WithSqlDefinitionAsync(KpiGrain.Entity, (connectionString, id) =>
            AssertStateIsPerEntityAsync(new SqlKpiStateStore(A3SqlTestDatabase.Options(connectionString)), id));
    }

    [Fact]
    public async Task EntityBuckets_RetentionPurgesAfter8Days()
    {
        var clock = new SD1FixedTimeProvider(Now);
        await AssertRetentionAsync(new InMemoryKpiBucketStore(clock), 7, Now);

        // The clock decides: the same store keeps a 7-day-old entity bucket until it is more than 8 days old.
        var store = new InMemoryKpiBucketStore(clock);
        await store.UpsertAsync([A3Kpi.Bucket(7, Now.AddDays(-7), 1, 1) with { EntityKey = "e" }]);
        clock.Now = Now.AddDays(2);
        await store.UpsertAsync([A3Kpi.Bucket(7, Now.AddDays(1), 1, 1) with { EntityKey = "e" }]);
        Assert.Equal([Now.AddDays(1)], (await store.GetEntityRangeAsync(7, Now.AddDays(-30), Now.AddDays(30))).Select(b => b.BucketStart));
        Assert.Equal(TimeSpan.FromDays(8), SqlKpiBucketStore.EntityRetention);

        await WithSqlDefinitionAsync(KpiGrain.Entity, (connectionString, id) =>
            AssertRetentionAsync(new SqlKpiBucketStore(A3SqlTestDatabase.Options(connectionString)), id, Now));
    }

    [Fact]
    public async Task ShadowComparison_RoundTrips()
    {
        await AssertShadowRoundTripAsync(new InMemoryScoutShadowStore(new SD1FixedTimeProvider(Now)), "latency");

        var connectionString = A3SqlTestDatabase.TryGetConnectionString(output);
        if (connectionString is null)
        {
            return;
        }
        var board = "sd1" + Guid.NewGuid().ToString("N")[..12];
        try
        {
            await AssertShadowRoundTripAsync(new SqlScoutShadowStore(A3SqlTestDatabase.Options(connectionString)), board);
        }
        finally
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.ExecuteAsync("DELETE FROM monitoring.ScoutShadowComparison WHERE Board = @Board;", new { Board = board });
        }
    }

    [Fact]
    public async Task ShadowComparison_OverlappingWindows_DoNotDoubleCount()
    {
        await AssertOverlappingWindowsAsync(new InMemoryScoutShadowStore(new SD1FixedTimeProvider(Now)), "latency");

        var connectionString = A3SqlTestDatabase.TryGetConnectionString(output);
        if (connectionString is null)
        {
            return;
        }
        var board = "sd1" + Guid.NewGuid().ToString("N")[..12];
        try
        {
            await AssertOverlappingWindowsAsync(new SqlScoutShadowStore(A3SqlTestDatabase.Options(connectionString)), board);
        }
        finally
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.ExecuteAsync("DELETE FROM monitoring.ScoutShadowComparison WHERE Board = @Board;", new { Board = board });
        }
    }

    // ------------------------------------------------------------------ SD5: counting unit and never-downgrade

    [Fact]
    public Task Counts_RulesOnlyHour_CountsOnce() => OnShadowStoresAsync(async (store, board) =>
    {
        var hour = Now.AddHours(-2);
        // One slow club for a whole hour: twelve 5-min rules intervals, nothing from the method.
        await store.AddRangeAsync(Enumerable.Range(0, 12)
            .Select(i => Shadow(board, "club-1", "checkout", hour.AddMinutes(5 * i), 3, ScoutShadowMethodVerdict.NotEvaluated, null, ScoutShadowAgreement.RulesOnly))
            .ToList());

        Assert.Equal(12, (await store.GetRecentAsync(board, 24, "sd5-profile")).Count);
        Assert.Equal(new ScoutShadowCounts(0, 1, 0), await store.GetCountsAsync(board, 24, "sd5-profile"));
    });

    [Fact]
    public Task Counts_MethodOnlyHour_CountsOnce() => OnShadowStoresAsync(async (store, board) =>
    {
        var hour = Now.AddHours(-2);
        await store.AddRangeAsync(
        [
            Shadow(board, "club-2", "", hour, null, ScoutShadowMethodVerdict.Anomalous, 21, ScoutShadowAgreement.MethodOnly),
            // A second definition (another stage) firing on the same club and hour is its own incident.
            Shadow(board, "club-2", "ticket", hour, null, ScoutShadowMethodVerdict.Anomalous, 22, ScoutShadowAgreement.MethodOnly),
        ]);

        Assert.Equal(new ScoutShadowCounts(0, 0, 2), await store.GetCountsAsync(board, 24, "sd5-profile"));
    });

    [Fact]
    public Task Counts_MixedGroup_CountsAsBoth() => OnShadowStoresAsync(async (store, board) =>
    {
        var hour = Now.AddHours(-2);
        await store.AddRangeAsync(
        [
            // Tester's scenario: 12 rules-only intervals of one club ...
            .. Enumerable.Range(0, 12).Select(i =>
                Shadow(board, "club-r", "checkout", hour.AddMinutes(5 * i), 3, ScoutShadowMethodVerdict.Normal, null, ScoutShadowAgreement.RulesOnly)),
            // ... one method-only club ...
            Shadow(board, "club-m", "", hour, null, ScoutShadowMethodVerdict.Anomalous, 31, ScoutShadowAgreement.MethodOnly),
            // ... and one anomaly matched in 2 intervals plus its own hourly row.
            Shadow(board, "club-b", "checkout", hour.AddMinutes(10), 3, ScoutShadowMethodVerdict.Anomalous, 32, ScoutShadowAgreement.Both),
            Shadow(board, "club-b", "checkout", hour.AddMinutes(15), 3, ScoutShadowMethodVerdict.Anomalous, 32, ScoutShadowAgreement.Both),
            Shadow(board, "club-b", "", hour, 3, ScoutShadowMethodVerdict.Anomalous, 32, ScoutShadowAgreement.Both),
            // Rules and method each flagged club-x in the same hour without a matched row: both sides saw it.
            Shadow(board, "club-x", "booking", hour.AddMinutes(30), 3, ScoutShadowMethodVerdict.NotEvaluated, null, ScoutShadowAgreement.RulesOnly),
            Shadow(board, "club-x", "", hour, null, ScoutShadowMethodVerdict.Anomalous, 33, ScoutShadowAgreement.MethodOnly),
            // The next hour of club-r is a separate incident.
            Shadow(board, "club-r", "checkout", hour.AddHours(1), 3, ScoutShadowMethodVerdict.Normal, null, ScoutShadowAgreement.RulesOnly),
        ]);

        Assert.Equal(new ScoutShadowCounts(2, 2, 1), await store.GetCountsAsync(board, 24, "sd5-profile"));
    });

    [Fact]
    public Task ShadowStore_NeverDowngradesBoth() => OnShadowStoresAsync(async (store, board) =>
    {
        var both = Shadow(board, "club-1", "", Now.AddHours(-2), 3, ScoutShadowMethodVerdict.Anomalous, 41, ScoutShadowAgreement.Both);
        await store.AddRangeAsync([both]);
        await store.AddRangeAsync([both with { RunId = Guid.NewGuid(), RuleRank = null, Agreement = ScoutShadowAgreement.MethodOnly }]);
        await store.AddRangeAsync([both with { RunId = Guid.NewGuid(), KpiAnomalyId = null, MethodVerdict = ScoutShadowMethodVerdict.Normal, Agreement = ScoutShadowAgreement.RulesOnly }]);

        var row = Assert.Single(await store.GetRecentAsync(board, 24, "sd5-profile"));
        Assert.Equal((both.RunId, ScoutShadowAgreement.Both, (int?)3, (long?)41), (row.RunId, row.Agreement, row.RuleRank, row.KpiAnomalyId));

        // Upgrades still apply.
        var rulesOnly = Shadow(board, "club-2", "x", Now.AddHours(-2), 3, ScoutShadowMethodVerdict.NotEvaluated, null, ScoutShadowAgreement.RulesOnly);
        await store.AddRangeAsync([rulesOnly]);
        await store.AddRangeAsync([rulesOnly with { Agreement = ScoutShadowAgreement.Both, KpiAnomalyId = 42, MethodVerdict = ScoutShadowMethodVerdict.Anomalous }]);
        Assert.Equal(ScoutShadowAgreement.Both, (await store.GetRecentAsync(board, 24, "sd5-profile")).Single(r => r.EntityKey == "club-2").Agreement);
    });

    private static ScoutShadowComparison Shadow(string board, string entity, string stage, DateTimeOffset bucket, int? rank, string verdict, long? anomalyId, string agreement) =>
        new(Guid.NewGuid(), board, "sd5-profile", entity, stage, bucket, rank, verdict, anomalyId, agreement);

    /// <summary>Runs the same assertions on the in-memory store and, when SPEC1000_SQL_CONNECTION is set, on SQL.</summary>
    private async Task OnShadowStoresAsync(Func<IScoutShadowStore, string, Task> body)
    {
        await body(new InMemoryScoutShadowStore(new SD1FixedTimeProvider(Now)), "latency");

        var connectionString = A3SqlTestDatabase.TryGetConnectionString(output);
        if (connectionString is null)
        {
            return;
        }
        var board = "sd5" + Guid.NewGuid().ToString("N")[..12];
        try
        {
            await body(new SqlScoutShadowStore(A3SqlTestDatabase.Options(connectionString), new SD1FixedTimeProvider(Now)), board);
        }
        finally
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.ExecuteAsync("DELETE FROM monitoring.ScoutShadowComparison WHERE Board = @Board;", new { Board = board });
        }
    }

    /// <summary>AG1 (contract v4 section 5): a KPI on a non-incumbent profile's own service persists through the SQL store.</summary>
    [Fact]
    public async Task KpiDefinition_NonIncumbentServiceDomain_Persists()
    {
        var shadowSql = File.ReadAllText(Path.Combine(FindPortalDirectory(), "src", "JourneyMonitor.Infrastructure", "Sql", "monitoring", "CreateMonitoringScoutShadow.sql"));
        Assert.Contains("ALTER TABLE monitoring.KpiDefinition DROP CONSTRAINT CK_monitoring_KpiDefinition_Domain;", shadowSql);
        Assert.Contains("CHECK (LEN(Domain) >= 1)", shadowSql);

        var definition = A3Kpi.Definition("AG1TEST-" + Guid.NewGuid().ToString("N")[..20], domain: "checkout", enabled: false) with { ProfileKey = "ag1-acme" };

        var memory = await new InMemoryKpiDefinitionStore().UpsertAsync(definition, "ag1test");
        Assert.Equal(("checkout", "ag1-acme"), (memory.Domain, memory.ProfileKey));

        var connectionString = A3SqlTestDatabase.TryGetConnectionString(output);
        if (connectionString is null)
        {
            return;
        }

        await using (var connection = new SqlConnection(connectionString))
        {
            Assert.Equal(0, await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM sys.check_constraints WHERE parent_object_id = OBJECT_ID(N'monitoring.KpiDefinition') AND name = N'CK_monitoring_KpiDefinition_Domain'"));
        }

        var store = new SqlKpiDefinitionStore(A3SqlTestDatabase.Options(connectionString));
        var id = 0;
        try
        {
            var inserted = await store.UpsertAsync(definition, "ag1test");
            id = inserted.KpiDefinitionId;
            var stored = await store.GetAsync(id);
            Assert.NotNull(stored);
            Assert.Equal(("checkout", "ag1-acme"), (stored.Domain, stored.ProfileKey));
            Assert.Contains(await store.GetAllAsync("ag1-acme"), d => d.KpiDefinitionId == id);
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
    public async Task SqlScripts_AreIdempotent()
    {
        var portal = FindPortalDirectory();
        var kpiSql = File.ReadAllText(Path.Combine(portal, "src", "JourneyMonitor.Infrastructure", "Sql", "monitoring", "CreateMonitoringKpi.sql"));
        var shadowSql = File.ReadAllText(Path.Combine(portal, "src", "JourneyMonitor.Infrastructure", "Sql", "monitoring", "CreateMonitoringScoutShadow.sql"));
        var initSql = File.ReadAllText(Path.Combine(portal, "docker", "init-sql.sh"));

        // Registered, and after the (v3, frozen) KPI script whose tables it migrates and whose procedures it upgrades.
        var kpiAt = initSql.IndexOf("monitoring/CreateMonitoringKpi.sql", StringComparison.Ordinal);
        var shadowAt = initSql.IndexOf("monitoring/CreateMonitoringScoutShadow.sql", StringComparison.Ordinal);
        Assert.True(kpiAt >= 0 && shadowAt > kpiAt, "CreateMonitoringScoutShadow.sql must be registered in init-sql.sh after CreateMonitoringKpi.sql.");

        foreach (var script in new[] { kpiSql, shadowSql })
        {
            Assert.Equal(Regex.Matches(script, @"(?im)^\s*CREATE\s+TABLE").Count, Regex.Matches(script, @"(?i)IF\s+OBJECT_ID\s*\([^)]*N'U'\s*\)\s*IS\s+NULL").Count);
            Assert.DoesNotMatch(@"(?im)^\s*CREATE\s+PROCEDURE", script);
            Assert.DoesNotMatch(@"(?im)^\s*DROP\s+TABLE", script);
        }
        // The v4 migration lives in the shadow script: columns are guarded, PK swaps only run while the PK lacks EntityKey.
        foreach (var column in new[] { "KpiBucket', N'EntityKey'", "KpiBucket', N'FailedCount'", "KpiDetectionState', N'EntityKey'", "KpiAnomaly', N'EntityKey'", "KpiDefinition', N'Grain'", "KpiDefinition', N'MaxEntitiesPerBucket'" })
        {
            Assert.Contains($"IF COL_LENGTH(N'monitoring.{column}) IS NULL", shadowSql);
        }
        Assert.Equal(2, Regex.Matches(shadowSql, @"kc\.type = N'PK' AND c\.name = N'EntityKey'").Count);
        Assert.Contains("ON monitoring.ScoutShadowComparison (Board, ProfileKey, EntityKey, Stage, BucketStart)", shadowSql);
        Assert.Contains("DELETE FROM monitoring.ScoutShadowComparison WHERE BucketStart < @Cutoff", shadowSql);

        var connectionString = A3SqlTestDatabase.TryGetConnectionString(output);
        if (connectionString is null)
        {
            return;
        }

        // A scratch database (never the shared schema: re-running the v3 script there would swap the procedures
        // of a live stack and of concurrent tests) gets the v3 schema plus a copy of the real KPI rows of the
        // configured database, then the scripts run twice in init-sql.sh order.
        var source = new SqlConnectionStringBuilder(connectionString).InitialCatalog;
        var name = "SD1Idem_" + Guid.NewGuid().ToString("N")[..16];
        var master = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master", Pooling = false }.ConnectionString;
        await using (var admin = new SqlConnection(master))
        {
            await admin.ExecuteAsync($"CREATE DATABASE [{name}];");
        }
        try
        {
            await using var connection = new SqlConnection(new SqlConnectionStringBuilder(connectionString) { InitialCatalog = name, Pooling = false }.ConnectionString);
            await connection.OpenAsync();
            await connection.ExecuteAsync("EXEC(N'CREATE SCHEMA monitoring');");
            await RunScriptAsync(connection, kpiSql);
            await connection.ExecuteAsync(CopyRealRowsSql(source), commandTimeout: 120);

            var before = await RowKeysAsync(connection);
            for (var run = 0; run < 2; run++)
            {
                await RunScriptAsync(connection, kpiSql);
                await RunScriptAsync(connection, shadowSql);
            }
            Assert.Equal(before.OrderBy(k => k, StringComparer.Ordinal), (await RowKeysAsync(connection)).OrderBy(k => k, StringComparer.Ordinal));
            await AssertV4KeysAsync(connection);
        }
        finally
        {
            await using var admin = new SqlConnection(master);
            await admin.ExecuteAsync($"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}];");
        }
    }

    private static async Task AssertV4KeysAsync(SqlConnection connection)
    {
        Assert.Equal("KpiDefinitionId,EntityKey,BucketStart", await PrimaryKeyColumnsAsync(connection, "monitoring.KpiBucket"));
        Assert.Equal("KpiDefinitionId,EntityKey", await PrimaryKeyColumnsAsync(connection, "monitoring.KpiDetectionState"));
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM sys.indexes WHERE object_id = OBJECT_ID(N'monitoring.KpiAnomaly') AND name = N'IX_monitoring_KpiAnomaly_Entity'"));
        Assert.Equal(6, await connection.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*)
            FROM sys.key_constraints AS kc
            INNER JOIN sys.index_columns AS ic ON ic.object_id = kc.parent_object_id AND ic.index_id = kc.unique_index_id
            WHERE kc.parent_object_id = OBJECT_ID(N'monitoring.KpiDefinition') AND kc.name = N'UQ_monitoring_KpiDefinition_Key'
            """));
    }

    // ------------------------------------------------------------------ shared assertions

    private static async Task AssertEntityBucketsAreDistinctSeriesAsync(IKpiBucketStore store, int id)
    {
        await store.UpsertAsync(
        [
            A3Kpi.Bucket(id, Now, 100, 10),
            A3Kpi.Bucket(id, Now, 2, 20) with { EntityKey = "hotel-1", FailedCount = 1 },
            A3Kpi.Bucket(id, Now, 3, 30) with { EntityKey = "hotel-2", FailedCount = null },
            A3Kpi.Bucket(id, Now.AddHours(-1), 4, 40) with { EntityKey = "hotel-1", FailedCount = 0 },
        ]);
        // Same entity in another case is the same series (the SQL column is case-insensitive): replaces, does not add.
        await store.UpsertAsync([A3Kpi.Bucket(id, Now, 5, 50) with { EntityKey = "HOTEL-1", FailedCount = 2 }]);
        // Surrounding spaces are dropped, so " hotel-2 " is hotel-2 (SQL would collide on it anyway).
        await store.UpsertAsync([A3Kpi.Bucket(id, Now, 3, 30) with { EntityKey = " hotel-2 ", FailedCount = null }]);

        Assert.Equal([A3Kpi.Bucket(id, Now, 100, 10)], await store.GetRangeAsync(id, Now.AddHours(-2), Now.AddHours(1)));
        Assert.Equal([A3Kpi.Bucket(id, Now, 100, 10)], await store.GetRangeAsync(id, Now.AddHours(-2), Now.AddHours(1), "", default));

        var hotel1 = await store.GetRangeAsync(id, Now.AddHours(-2), Now.AddHours(1), "hotel-1", default);
        Assert.Equal([(Now.AddHours(-1), 4L, (long?)0), (Now, 5L, (long?)2)], hotel1.Select(b => (b.BucketStart, b.TotalCount, b.FailedCount)));

        var all = await store.GetEntityRangeAsync(id, Now.AddHours(-2), Now.AddHours(1));
        Assert.Equal(
            [("hotel-1", Now.AddHours(-1)), ("hotel-1", Now), ("hotel-2", Now)],
            all.Select(b => (b.EntityKey.ToLowerInvariant(), b.BucketStart)));
        Assert.DoesNotContain(all, b => b.EntityKey == "");
        Assert.Null(all.Single(b => b.EntityKey == "hotel-2").FailedCount);
        Assert.Empty(await store.GetEntityRangeAsync(id, Now.AddHours(1), Now.AddHours(2)));
    }

    private static async Task AssertOverlappingWindowsAsync(IScoutShadowStore store, string board)
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var x = new ScoutShadowComparison(first, board, "sd1-profile", "hotel-1", "search", Now.AddMinutes(-10), 1, ScoutShadowMethodVerdict.NotEvaluated, null, ScoutShadowAgreement.RulesOnly);
        var y = new ScoutShadowComparison(first, board, "sd1-profile", "hotel-2", "search", Now.AddMinutes(-20), 2, ScoutShadowMethodVerdict.Anomalous, 7, ScoutShadowAgreement.Both);
        await store.AddRangeAsync([x, y]);

        // The next cycle's window overlaps: X is seen again (now matched by the method, key with stray spaces),
        // Y again unchanged, Z is new. Same (Board, ProfileKey, EntityKey, Stage, BucketStart) -> updated, not added.
        var xAgain = x with { RunId = second, EntityKey = " hotel-1 ", MethodVerdict = ScoutShadowMethodVerdict.Anomalous, KpiAnomalyId = 8, Agreement = ScoutShadowAgreement.Both };
        var z = new ScoutShadowComparison(second, board, "sd1-profile", "hotel-3", "", Now.AddMinutes(-5), null, ScoutShadowMethodVerdict.Anomalous, 9, ScoutShadowAgreement.MethodOnly);
        await store.AddRangeAsync([xAgain, y with { RunId = second }, z]);

        Assert.Equal(new ScoutShadowCounts(Both: 2, RulesOnly: 0, MethodOnly: 1), await store.GetCountsAsync(board, 24, "sd1-profile"));
        var recent = await store.GetRecentAsync(board, 24, null);
        Assert.Equal(3, recent.Count);
        var updated = recent.Single(r => r.EntityKey == "hotel-1");
        Assert.Equal((second, 8L, ScoutShadowAgreement.Both), (updated.RunId, updated.KpiAnomalyId!.Value, updated.Agreement));

        // Shadow rows are kept 8 days, like entity buckets.
        await store.AddRangeAsync([z with { EntityKey = "hotel-old", BucketStart = Now.AddDays(-9) }]);
        Assert.Equal(3, (await store.GetRecentAsync(board, 24 * 30, null)).Count);
    }

    private static async Task AssertDomainBehavesAsV3Async(
        IKpiDefinitionStore definitions, IKpiBucketStore buckets, IKpiStateStore states, IKpiAnomalyStore anomalies, KpiDefinition definition)
    {
        var id = definition.KpiDefinitionId;
        Assert.Equal(KpiGrain.Domain, definition.Grain);
        Assert.Equal(500, definition.MaxEntitiesPerBucket);
        Assert.Equal(definition, await definitions.GetAsync(id));

        // Buckets: the v3 write/read pair is untouched by entity rows of the same instants.
        var domain = A3Kpi.Bucket(id, Now, 10, 100);
        await buckets.UpsertAsync([domain]);
        await buckets.UpsertAsync([domain with { EntityKey = "hotel-1", TotalCount = 99 }]);
        var read = Assert.Single(await buckets.GetRangeAsync(id, Now, Now.AddHours(1)));
        Assert.Equal(domain, read);
        Assert.Equal("", read.EntityKey);
        Assert.Null(read.FailedCount);

        // State: the v3 read is the domain row even when an entity row exists.
        await states.SaveAsync(new KpiDetectionState(id, 1, Now, null, Now));
        await states.SaveAsync(new KpiDetectionState(id, 7, Now, Now.AddHours(2), Now) { EntityKey = "hotel-1" });
        Assert.Equal(new KpiDetectionState(id, 1, Now, null, Now), await states.GetAsync(id));

        // Anomalies: the v3 read returns only domain rows, and the series filter is applied before TOP.
        var domainAnomaly = await anomalies.AddAsync(new KpiAnomaly(0, id, Future, Now, 2, 1, 1, "domain", null));
        for (var i = 1; i <= 3; i++)
        {
            await anomalies.AddAsync(new KpiAnomaly(0, id, Future.AddMinutes(i), Now, 2, 1, 1, $"entity {i}", null) { EntityKey = "hotel-1" });
        }
        var recent = Assert.Single(await anomalies.GetRecentAsync(null, null, 1, definition.ProfileKey));
        Assert.Equal(domainAnomaly, recent);
        Assert.Equal(domainAnomaly, Assert.Single(await anomalies.GetRecentAsync(null, null, 1, definition.ProfileKey, "", default)));

        var entityRecent = await anomalies.GetRecentAsync(null, null, 2, definition.ProfileKey, "hotel-1", default);
        Assert.Equal(["entity 3", "entity 2"], entityRecent.Select(a => a.Detection));
        Assert.All(entityRecent, a => Assert.Equal("hotel-1", a.EntityKey));
    }

    private static async Task AssertStateIsPerEntityAsync(IKpiStateStore store, int id)
    {
        await store.SaveAsync(new KpiDetectionState(id, 1, Now, null, Now));
        await store.SaveAsync(new KpiDetectionState(id, 2, Now, Now.AddHours(1), Now) { EntityKey = "hotel-b" });
        await store.SaveAsync(new KpiDetectionState(id, 3, Now, null, Now) { EntityKey = "hotel-a" });
        // Saving an entity again replaces only that entity's row.
        await store.SaveAsync(new KpiDetectionState(id, 4, Now.AddHours(1), null, Now.AddHours(1)) { EntityKey = "hotel-a" });

        Assert.Equal(1, (await store.GetAsync(id)).ConsecutiveCount);
        Assert.Equal(1, (await store.GetAsync(id, "", default)).ConsecutiveCount);
        Assert.Equal(
            new KpiDetectionState(id, 4, Now.AddHours(1), null, Now.AddHours(1)) { EntityKey = "hotel-a" },
            await store.GetAsync(id, "hotel-a", default));
        Assert.Equal(KpiDetectionState.Initial(id, "hotel-c"), await store.GetAsync(id, "hotel-c", default));

        var entities = await store.GetEntityStatesAsync(id);
        Assert.Equal([("hotel-a", 4), ("hotel-b", 2)], entities.Select(s => (s.EntityKey, s.ConsecutiveCount)));
        Assert.Equal(Now.AddHours(1), entities[1].CooldownUntil);
        Assert.Empty(await store.GetEntityStatesAsync(id + 100_000));
    }

    private static async Task AssertRetentionAsync(IKpiBucketStore store, int id, DateTimeOffset now)
    {
        await store.UpsertAsync([A3Kpi.Bucket(id, now.AddDays(-30), 1, 1)]);
        await store.UpsertAsync(
        [
            A3Kpi.Bucket(id, now.AddDays(-9), 1, 1) with { EntityKey = "e" },
            A3Kpi.Bucket(id, now.AddDays(-8).AddHours(-1), 1, 1) with { EntityKey = "e" },
            A3Kpi.Bucket(id, now.AddDays(-7), 1, 1) with { EntityKey = "e" },
            A3Kpi.Bucket(id, now, 1, 1) with { EntityKey = "f" },
        ]);

        var entity = await store.GetEntityRangeAsync(id, now.AddDays(-40), now.AddDays(1));
        Assert.Equal([("e", now.AddDays(-7)), ("f", now)], entity.Select(b => (b.EntityKey, b.BucketStart)));
        // Domain buckets are never purged by entity retention.
        Assert.Equal([now.AddDays(-30)], (await store.GetRangeAsync(id, now.AddDays(-40), now.AddDays(1))).Select(b => b.BucketStart));
    }

    private static async Task AssertShadowRoundTripAsync(IScoutShadowStore store, string board)
    {
        var run = Guid.NewGuid();
        var otherRun = Guid.NewGuid();
        ScoutShadowComparison Row(Guid runId, string entity, string stage, DateTimeOffset bucket, int? rank, string verdict, long? anomalyId, string agreement, string profile = "sd1-profile") =>
            new(runId, board, profile, entity, stage, bucket, rank, verdict, anomalyId, agreement);

        var both = Row(run, "hotel-1", "search", Now.AddMinutes(-10), 1, ScoutShadowMethodVerdict.Anomalous, 42, ScoutShadowAgreement.Both);
        var rulesOnly = Row(run, "hotel-2", "", Now.AddMinutes(-20), 2, ScoutShadowMethodVerdict.NotEvaluated, null, ScoutShadowAgreement.RulesOnly);
        var methodOnly = Row(run, "hotel-3", "book", Now.AddMinutes(-5), null, ScoutShadowMethodVerdict.Anomalous, 43, ScoutShadowAgreement.MethodOnly);
        var otherProfile = Row(otherRun, "hotel-1", "search", Now.AddMinutes(-15), 3, ScoutShadowMethodVerdict.Normal, null, ScoutShadowAgreement.RulesOnly, "sd1-other");
        var outsideWindow = Row(otherRun, "hotel-9", "search", Now.AddHours(-30), 1, ScoutShadowMethodVerdict.Normal, null, ScoutShadowAgreement.RulesOnly);

        await store.AddRangeAsync([both, rulesOnly, methodOnly, otherProfile, outsideWindow]);
        // A retried cycle does not duplicate rows.
        await store.AddRangeAsync([both, rulesOnly]);
        await store.AddRangeAsync([]);

        var recent = await store.GetRecentAsync(board.ToUpperInvariant(), 24, "sd1-profile");
        Assert.Equal([methodOnly, both, rulesOnly], recent);
        Assert.Equal(new ScoutShadowCounts(1, 1, 1), await store.GetCountsAsync(board, 24, "sd1-profile"));

        Assert.Equal([methodOnly, both, otherProfile, rulesOnly], await store.GetRecentAsync(board, 24, null));
        Assert.Equal(new ScoutShadowCounts(1, 2, 1), await store.GetCountsAsync(board, 24, null));
        Assert.Equal(new ScoutShadowCounts(1, 3, 1), await store.GetCountsAsync(board, 48, null));
        Assert.Empty(await store.GetRecentAsync(board + "x", 24, null));
        Assert.Equal(new ScoutShadowCounts(0, 0, 0), await store.GetCountsAsync(board + "x", 24, null));

        await Assert.ThrowsAsync<ArgumentException>(() => store.AddRangeAsync([both with { Agreement = "maybe" }]));
        await Assert.ThrowsAsync<ArgumentException>(() => store.AddRangeAsync([both with { MethodVerdict = "unknown" }]));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.GetRecentAsync(board, 0, null));
    }

    // ------------------------------------------------------------------ SQL helpers

    private async Task WithSqlDefinitionAsync(KpiGrain grain, Func<string, int, Task> body)
    {
        var connectionString = A3SqlTestDatabase.TryGetConnectionString(output);
        if (connectionString is null)
        {
            return;
        }

        var definitions = new SqlKpiDefinitionStore(A3SqlTestDatabase.Options(connectionString));
        var name = "SD1TEST-" + Guid.NewGuid().ToString("N")[..20];
        var id = 0;
        try
        {
            var inserted = await definitions.UpsertAsync(A3Kpi.Definition(name, enabled: false) with { Grain = grain, MaxEntitiesPerBucket = grain == KpiGrain.Entity ? 42 : 500 }, "sd1test");
            id = inserted.KpiDefinitionId;
            Assert.Equal(grain, inserted.Grain);
            Assert.Equal(grain == KpiGrain.Entity ? 42 : 500, inserted.MaxEntitiesPerBucket);
            await body(connectionString, id);
        }
        finally
        {
            if (id > 0)
            {
                await A3SqlTestDatabase.DeleteKpiDataAsync(connectionString, id);
            }
        }
    }

    private static async Task<string> PrimaryKeyColumnsAsync(SqlConnection connection, string table) =>
        await connection.ExecuteScalarAsync<string>(
            """
            SELECT STRING_AGG(c.name, ',') WITHIN GROUP (ORDER BY ic.key_ordinal)
            FROM sys.key_constraints AS kc
            INNER JOIN sys.index_columns AS ic ON ic.object_id = kc.parent_object_id AND ic.index_id = kc.unique_index_id
            INNER JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE kc.parent_object_id = OBJECT_ID(@Table) AND kc.type = N'PK'
            """,
            new { Table = table }) ?? "";

    private static async Task<HashSet<string>> RowKeysAsync(SqlConnection connection)
    {
        var keys = await connection.QueryAsync<string>(
            """
            SELECT CONCAT(N'D|', KpiDefinitionId, N'|', Kpi, N'|', Domain, N'|', Stage, N'|', ProfileKey) FROM monitoring.KpiDefinition
            UNION ALL SELECT CONCAT(N'A|', AuditId) FROM monitoring.KpiDefinitionAudit
            UNION ALL SELECT CONCAT(N'B|', KpiDefinitionId, N'|', CONVERT(nvarchar(40), BucketStart, 127), N'|', TotalCount, N'|', P95) FROM monitoring.KpiBucket
            UNION ALL SELECT CONCAT(N'S|', KpiDefinitionId, N'|', ConsecutiveCount, N'|', CONVERT(nvarchar(40), CooldownUntil, 127)) FROM monitoring.KpiDetectionState
            UNION ALL SELECT CONCAT(N'N|', AnomalyId, N'|', KpiDefinitionId, N'|', Detection) FROM monitoring.KpiAnomaly
            """);
        return [.. keys];
    }

    /// <summary>Copies the domain rows (the only kind v3 tables can hold) of the source database's KPI tables.</summary>
    private static string CopyRealRowsSql(string source) =>
        $"""
        SET IDENTITY_INSERT monitoring.KpiDefinition ON;
        INSERT INTO monitoring.KpiDefinition
            (KpiDefinitionId, Pillar, Kpi, Domain, Stage, SourceKey, Direction, Aggregation, EvaluationFrequencySec, BucketSizeSec,
             LookbackWindowSec, SampleSize, MinObservationsPerBucket, DetectionMethod, MethodParamsJson, ConsecutiveCount, CooldownMinutes,
             Enabled, UpdatedAt, UpdatedBy, ProfileKey)
        SELECT KpiDefinitionId, Pillar, Kpi, Domain, Stage, SourceKey, Direction, Aggregation, EvaluationFrequencySec, BucketSizeSec,
               LookbackWindowSec, SampleSize, MinObservationsPerBucket, DetectionMethod, MethodParamsJson, ConsecutiveCount, CooldownMinutes,
               Enabled, UpdatedAt, UpdatedBy, ProfileKey
        FROM [{source}].monitoring.KpiDefinition
        WHERE Grain = N'domain';
        SET IDENTITY_INSERT monitoring.KpiDefinition OFF;

        SET IDENTITY_INSERT monitoring.KpiDefinitionAudit ON;
        INSERT INTO monitoring.KpiDefinitionAudit (AuditId, KpiDefinitionId, SnapshotJson, ChangedAt, ChangedBy, ProfileKey)
        SELECT AuditId, KpiDefinitionId, SnapshotJson, ChangedAt, ChangedBy, ProfileKey FROM [{source}].monitoring.KpiDefinitionAudit;
        SET IDENTITY_INSERT monitoring.KpiDefinitionAudit OFF;

        INSERT INTO monitoring.KpiBucket (KpiDefinitionId, BucketStart, TotalCount, SampledCount, P50, P95, Mean, StdDev, Value, IngestedAt)
        SELECT KpiDefinitionId, BucketStart, TotalCount, SampledCount, P50, P95, Mean, StdDev, Value, IngestedAt
        FROM [{source}].monitoring.KpiBucket WHERE EntityKey = N'';

        INSERT INTO monitoring.KpiDetectionState (KpiDefinitionId, ConsecutiveCount, LastBucketEvaluated, CooldownUntil, LastEvaluatedAt, UpdatedAt)
        SELECT KpiDefinitionId, ConsecutiveCount, LastBucketEvaluated, CooldownUntil, LastEvaluatedAt, UpdatedAt
        FROM [{source}].monitoring.KpiDetectionState WHERE EntityKey = N'';

        SET IDENTITY_INSERT monitoring.KpiAnomaly ON;
        INSERT INTO monitoring.KpiAnomaly
            (AnomalyId, KpiDefinitionId, DetectedAt, BucketStart, ObservedValue, ReferenceValue, Deviation, Detection, InvestigationId)
        SELECT AnomalyId, KpiDefinitionId, DetectedAt, BucketStart, ObservedValue, ReferenceValue, Deviation, Detection, InvestigationId
        FROM [{source}].monitoring.KpiAnomaly WHERE EntityKey = N'';
        SET IDENTITY_INSERT monitoring.KpiAnomaly OFF;
        """;
    private static async Task RunScriptAsync(SqlConnection connection, string script)
    {
        foreach (var batch in Regex.Split(script, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(batch))
            {
                await connection.ExecuteAsync(batch, commandTimeout: 120);
            }
        }
    }

    private static string FindPortalDirectory([CallerFilePath] string thisFile = "")
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(thisFile) })
        {
            for (var dir = start is null ? null : new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "docker", "init-sql.sh"))) return dir.FullName;
                if (File.Exists(Path.Combine(dir.FullName, "Portal", "docker", "init-sql.sh"))) return Path.Combine(dir.FullName, "Portal");
            }
        }
        throw new InvalidOperationException("Could not find the Portal directory (docker/init-sql.sh).");
    }
}

internal sealed class SD1FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}
