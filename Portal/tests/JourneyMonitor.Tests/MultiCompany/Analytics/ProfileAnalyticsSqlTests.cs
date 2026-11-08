using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Dapper;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Infrastructure.Sql;
using JourneyMonitor.Tests.Spec1000.A3;
using Microsoft.Data.SqlClient;
using Xunit.Abstractions;

namespace JourneyMonitor.Tests.MultiCompany.Analytics;

/// <summary>
/// Contract v6 §4.4 / §4.5 against real SQL (runs only when SPEC1000_SQL_CONNECTION is set): two companies
/// that share a service key and an entity key never see each other's dashboard points or journeys.
/// </summary>
public class ProfileAnalyticsSqlTests(ITestOutputHelper output)
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public async Task Sql_DashboardAndOverviewAreIsolatedByProfile()
    {
        var connectionString = A3SqlTestDatabase.TryGetConnectionString(output);
        if (connectionString is null)
        {
            return;
        }

        var name = "MC3Iso_" + Guid.NewGuid().ToString("N")[..16];
        var master = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master", Pooling = false }.ConnectionString;
        await using (var admin = new SqlConnection(master))
        {
            await admin.ExecuteAsync($"CREATE DATABASE [{name}];");
        }
        try
        {
            var scratch = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = name, Pooling = false }.ConnectionString;
            await using (var connection = new SqlConnection(scratch))
            {
                await connection.OpenAsync();
                var sqlRoot = Path.Combine(FindPortalDirectory(), "src", "JourneyMonitor.Infrastructure", "Sql");
                await RunScriptAsync(connection, File.ReadAllText(Path.Combine(sqlRoot, "dashboard", "CreateDashboardMetrics.sql")));
                await RunScriptAsync(connection, File.ReadAllText(Path.Combine(sqlRoot, "monitoring", "CreateMonitoringProfileJourney.sql")));

                foreach (var (profile, journeys, failed) in new[] { ("banco", 4, 3), ("kinectify", 9, 1) })
                {
                    for (var i = 0; i < journeys; i++)
                    {
                        await connection.ExecuteAsync(
                            """
                            INSERT INTO monitoring.ProfileJourney
                                (ProfileKey, JourneyId, ServiceKey, EntityKey, StartedAt, EndedAt, DurationMs, Status, StepCount, Failed)
                            VALUES (@Profile, @Id, N'payments', N'SHARED-1', @At, @At, 100, @Status, 1, @Failed);
                            INSERT INTO monitoring.ProfileJourneyStep
                                (ProfileKey, JourneyId, StepOrder, StepKey, Name, [Timestamp], DurationMs, Success)
                            VALUES (@Profile, @Id, 1, N'charge', N'POST /charge', @At, 100, @Success);
                            """,
                            new
                            {
                                Profile = profile,
                                Id = $"{profile}-{i}",
                                At = Now.AddMinutes(-5),
                                Status = i < failed ? "Failed" : "Completed",
                                Failed = i < failed,
                                Success = i >= failed,
                            });
                    }
                }
            }

            var store = new SqlDashboardMetricStore(A3SqlTestDatabase.Options(scratch));
            IProfileDashboardMetricStore profileStore = store;
            await profileStore.ReplaceProfileChartAsync("banco", "payments", DashboardCharts.StepLatencyP95, "6h", "production",
                [new DashboardMetricPoint("payments", DashboardCharts.StepLatencyP95, "charge", Now.AddMinutes(-10), 111m, "6h", "production", Now)],
                CancellationToken.None);
            await profileStore.ReplaceProfileChartAsync("kinectify", "payments", DashboardCharts.StepLatencyP95, "6h", "production",
                [new DashboardMetricPoint("payments", DashboardCharts.StepLatencyP95, "charge", Now.AddMinutes(-10), 999m, "6h", "production", Now)],
                CancellationToken.None);

            var banco = await profileStore.GetProfilePointsAsync("banco", "payments", null, null, null, null, CancellationToken.None);
            Assert.Equal([111m], banco.Select(p => p.Value).ToList());
            var kinectify = await profileStore.GetProfilePointsAsync("kinectify", null, null, null, null, null, CancellationToken.None);
            Assert.Equal([999m], kinectify.Select(p => p.Value).ToList());
            // The legacy surface is the incumbent's: neither company leaks into it.
            Assert.Empty(await ((IDashboardMetricStore)store).GetAsync("payments", null, null, null, null, CancellationToken.None));

            var reader = new SqlOverviewReader(A3SqlTestDatabase.Options(scratch));
            var overview = await reader.ReadAsync(
                new ProfileOverviewQuery("banco", false, null, null, "production", Now.AddHours(-1), Now.AddHours(-24), null, 5),
                CancellationToken.None);
            Assert.Equal(4, overview.Counts.JourneyTotal);
            Assert.Equal(1, overview.Counts.SuccessfulJourneys);
            Assert.Equal(4, overview.Counts.AvailabilityTotalCalls);
            Assert.Equal(3, overview.Counts.AvailabilityFailedCalls);
            Assert.Null(overview.Counts.SearchedJourneys);
            var entity = Assert.Single(overview.JourneyEntities);
            Assert.Equal(new OverviewEntity("SHARED-1", 0, 0, 3), entity);
        }
        finally
        {
            await using var admin = new SqlConnection(master);
            await admin.ExecuteAsync($"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}];");
        }
    }

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
