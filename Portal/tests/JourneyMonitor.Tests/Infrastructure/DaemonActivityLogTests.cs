using Dapper;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Infrastructure.Monitoring;
using JourneyMonitor.Infrastructure.Sql;
using JourneyMonitor.Tests.Spec1000.A3;
using Microsoft.Data.SqlClient;
using Xunit.Abstractions;

namespace JourneyMonitor.Tests.Spec1000.D1;

public sealed class DaemonActivityLogTests(ITestOutputHelper output)
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 6, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task InMemoryLog_AfterIdZeroReturnsLatestAscending()
    {
        var log = new InMemoryDaemonActivityLog();
        await log.AppendAsync(Enumerable.Range(1, 300)
            .Select(i => D1Entries.Entry(DaemonKeys.KpiIngestion, T0.AddSeconds(i), DaemonActivityKinds.IngestionKpi, message: $"m{i}"))
            .ToList());

        var page = await log.GetAsync(0, null, null, 200);

        Assert.Equal(Enumerable.Range(101, 200).Select(i => (long)i), page.Entries.Select(e => e.ActivityId));
        Assert.Equal(300, page.LastId);
        Assert.Equal("m101", page.Entries[0].Message);

        var after = await log.GetAsync(250, null, null, 20);
        Assert.Equal(Enumerable.Range(251, 20).Select(i => (long)i), after.Entries.Select(e => e.ActivityId));
        Assert.Equal(270, after.LastId);

        var nothingNew = await log.GetAsync(300, null, null, 20);
        Assert.Empty(nothingNew.Entries);
        Assert.Equal(300, nothingNew.LastId);
    }

    [Fact]
    public async Task InMemoryLog_FiltersByDaemonAndMinLevel()
    {
        var log = new InMemoryDaemonActivityLog();
        await log.AppendAsync(
        [
            D1Entries.Entry(DaemonKeys.KpiIngestion, T0, DaemonActivityKinds.RunStarted),
            D1Entries.Entry(DaemonKeys.ApplicationHealthMonitor, T0, DaemonActivityKinds.RunStarted),
            D1Entries.Entry(DaemonKeys.ApplicationHealthMonitor, T0, DaemonActivityKinds.Anomaly, DaemonActivityLevel.Warning),
            D1Entries.Entry(DaemonKeys.KpiIngestion, T0, DaemonActivityKinds.Error, DaemonActivityLevel.Error),
            D1Entries.Entry(DaemonKeys.ApplicationHealthMonitor, T0, DaemonActivityKinds.Error, DaemonActivityLevel.Error),
            D1Entries.Entry(DaemonKeys.ApplicationHealthMonitor, T0, DaemonActivityKinds.RunCompleted),
        ]);

        var app = await log.GetAsync(0, DaemonKeys.ApplicationHealthMonitor, null, 100);
        Assert.Equal([2L, 3L, 5L, 6L], app.Entries.Select(e => e.ActivityId).ToArray());

        var appWarnings = await log.GetAsync(0, DaemonKeys.ApplicationHealthMonitor, DaemonActivityLevel.Warning, 100);
        Assert.Equal([3L, 5L], appWarnings.Entries.Select(e => e.ActivityId).ToArray());

        var errors = await log.GetAsync(0, null, DaemonActivityLevel.Error, 100);
        Assert.Equal([4L, 5L], errors.Entries.Select(e => e.ActivityId).ToArray());

        var appErrorsAfter = await log.GetAsync(3, DaemonKeys.ApplicationHealthMonitor, DaemonActivityLevel.Error, 100);
        Assert.Equal([5L], appErrorsAfter.Entries.Select(e => e.ActivityId).ToArray());
        Assert.Equal(5, appErrorsAfter.LastId);

        var info = await log.GetAsync(0, null, DaemonActivityLevel.Info, 100);
        Assert.Equal(6, info.Entries.Count);
    }

    [Fact]
    public async Task InMemoryLog_CapsAt10000AndPurgesByAge()
    {
        var log = new InMemoryDaemonActivityLog();
        await log.AppendAsync(Enumerable.Range(1, InMemoryDaemonActivityLog.MaxEntries + 5)
            .Select(i => D1Entries.Entry(DaemonKeys.KpiIngestion, T0.AddSeconds(i), DaemonActivityKinds.IngestionKpi))
            .ToList());

        var oldest = await log.GetAsync(1, null, null, 1);
        Assert.Equal(6, oldest.Entries.Single().ActivityId);

        var purged = await log.PurgeOlderThanAsync(T0.AddSeconds(16));
        Assert.Equal(10, purged);
        Assert.Equal(16, (await log.GetAsync(1, null, null, 1)).Entries.Single().ActivityId);
    }

    [Fact]
    public async Task SqlLog_RoundTripAndPaging()
    {
        var connectionString = A3SqlTestDatabase.TryGetConnectionString(output);
        if (connectionString is null)
        {
            return;
        }

        await EnsureSchemaAsync(connectionString);
        var log = new SqlDaemonActivityLog(A3SqlTestDatabase.Options(connectionString));
        const string key = "D1TEST";
        var runId = Guid.NewGuid();
        try
        {
            await log.AppendAsync(
            [
                new DaemonActivityEntry(0, key, runId, T0, DaemonActivityLevel.Info, DaemonActivityKinds.RunStarted, "Run started", null),
                new DaemonActivityEntry(0, key, runId, T0.AddSeconds(1), DaemonActivityLevel.Warning, DaemonActivityKinds.Anomaly, "Latency/hotels: up", """{"kpiDefinitionId":7}"""),
                new DaemonActivityEntry(0, key, runId, T0.AddSeconds(2), DaemonActivityLevel.Error, DaemonActivityKinds.Error, new string('x', 1500), null),
                new DaemonActivityEntry(0, key, runId, T0.AddSeconds(3), DaemonActivityLevel.Info, DaemonActivityKinds.KpiEvaluated, "evaluated", null),
                new DaemonActivityEntry(0, key, runId, T0.AddSeconds(4), DaemonActivityLevel.Info, DaemonActivityKinds.RunCompleted, "done", null),
            ]);

            var all = await log.GetAsync(0, key, null, 1000);
            Assert.Equal(5, all.Entries.Count);
            Assert.Equal(
                [DaemonActivityKinds.RunStarted, DaemonActivityKinds.Anomaly, DaemonActivityKinds.Error, DaemonActivityKinds.KpiEvaluated, DaemonActivityKinds.RunCompleted],
                all.Entries.Select(e => e.Kind).ToArray());
            Assert.True(all.Entries.Zip(all.Entries.Skip(1)).All(p => p.First.ActivityId < p.Second.ActivityId));
            Assert.Equal(all.Entries[^1].ActivityId, all.LastId);
            Assert.All(all.Entries, e => Assert.Equal(runId, e.RunId));
            Assert.Equal(T0.AddSeconds(1), all.Entries[1].At);
            Assert.Equal(DaemonActivityLevel.Warning, all.Entries[1].Level);
            Assert.Equal("""{"kpiDefinitionId":7}""", all.Entries[1].DataJson);
            Assert.Equal(1000, all.Entries[2].Message.Length);

            var latest = await log.GetAsync(0, key, null, 2);
            Assert.Equal([DaemonActivityKinds.KpiEvaluated, DaemonActivityKinds.RunCompleted], latest.Entries.Select(e => e.Kind).ToArray());

            var firstId = all.Entries[0].ActivityId;
            var after = await log.GetAsync(firstId, key, null, 2);
            Assert.Equal([all.Entries[1].ActivityId, all.Entries[2].ActivityId], after.Entries.Select(e => e.ActivityId).ToArray());
            Assert.Equal(all.Entries[2].ActivityId, after.LastId);

            var warnings = await log.GetAsync(0, key, DaemonActivityLevel.Warning, 100);
            Assert.Equal([DaemonActivityKinds.Anomaly, DaemonActivityKinds.Error], warnings.Entries.Select(e => e.Kind).ToArray());
            var errors = await log.GetAsync(firstId, key, DaemonActivityLevel.Error, 100);
            Assert.Single(errors.Entries);

            var empty = await log.GetAsync(all.LastId, key, null, 10);
            Assert.Empty(empty.Entries);
            Assert.Equal(all.LastId, empty.LastId);

            // The cutoff only reaches the 2001 test row; real activity is never that old.
            await log.AppendAsync([new DaemonActivityEntry(0, key, runId, new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero), DaemonActivityLevel.Info, "old", "old", null)]);
            var deleted = await log.PurgeOlderThanAsync(new DateTimeOffset(2001, 1, 2, 0, 0, 0, TimeSpan.Zero));
            Assert.True(deleted >= 1);
            Assert.Equal(5, (await log.GetAsync(0, key, null, 1000)).Entries.Count);
        }
        finally
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.ExecuteAsync("DELETE FROM monitoring.DaemonActivity WHERE DaemonKey = @Key", new { Key = key });
        }
    }

    /// <summary>Applies CreateMonitoringDaemonActivity.sql (idempotent) so the test does not depend on sql-init having run.</summary>
    private static async Task EnsureSchemaAsync(string connectionString)
    {
        var script = await File.ReadAllTextAsync(FindScript());
        var batches = System.Text.RegularExpressions.Regex.Split(script, @"^\s*GO\s*$", System.Text.RegularExpressions.RegexOptions.Multiline)
            .Where(b => !string.IsNullOrWhiteSpace(b));
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        foreach (var batch in batches)
        {
            await connection.ExecuteAsync(batch);
        }
    }

    private static string FindScript()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "JourneyMonitor.Infrastructure", "Sql", "monitoring", "CreateMonitoringDaemonActivity.sql");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("CreateMonitoringDaemonActivity.sql not found above " + AppContext.BaseDirectory);
    }
}
