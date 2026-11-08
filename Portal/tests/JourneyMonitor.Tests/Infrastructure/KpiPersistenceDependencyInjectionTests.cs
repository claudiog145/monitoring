using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Infrastructure.DependencyInjection;
using JourneyMonitor.Infrastructure.Monitoring;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace JourneyMonitor.Tests.Spec1000.A3;

public class KpiPersistenceDependencyInjectionTests
{
    [Fact]
    public void AddKpiPersistence_UsesInMemoryWithoutConnectionString()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();

        services.AddKpiPersistence(configuration);
        using var provider = services.BuildServiceProvider();

        Assert.IsType<InMemoryKpiDefinitionStore>(provider.GetRequiredService<IKpiDefinitionStore>());
        Assert.IsType<InMemoryKpiBucketStore>(provider.GetRequiredService<IKpiBucketStore>());
        Assert.IsType<InMemoryKpiSampleStore>(provider.GetRequiredService<IKpiSampleStore>());
        Assert.IsType<InMemoryKpiStateStore>(provider.GetRequiredService<IKpiStateStore>());
        Assert.IsType<InMemoryKpiAnomalyStore>(provider.GetRequiredService<IKpiAnomalyStore>());
        Assert.IsType<InMemoryIngestionWatermarkStore>(provider.GetRequiredService<IIngestionWatermarkStore>());
        Assert.IsType<InMemoryTriageInvestigationStore>(provider.GetRequiredService<ITriageInvestigationStore>());
        Assert.IsType<InMemoryScoutShadowStore>(provider.GetRequiredService<IScoutShadowStore>());

        // Singletons: state written through one resolution is visible through the next.
        Assert.Same(provider.GetRequiredService<IKpiBucketStore>(), provider.GetRequiredService<IKpiBucketStore>());
        Assert.Same(provider.GetRequiredService<ITriageInvestigationStore>(), provider.GetRequiredService<ITriageInvestigationStore>());
    }
}

/// <summary>
/// SD1 iteration 2: the KPI persistence choice by schema state. Probe-driven cases always run; with
/// SPEC1000_SQL_CONNECTION set, the real gate is also exercised on a scratch database created and dropped
/// by the test (never on the shared platformDB schema).
/// </summary>
public class KpiPersistenceSchemaGateTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private const string FakeConnectionString = "Server=sd1-probe-only;Database=none;User Id=x;Password=y;";

    [Fact]
    public async Task KpiPersistence_V3SchemaWithoutV4_FailsFast()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddKpiPersistence(Configuration(FakeConnectionString), _ => KpiSchemaStatus.V4MigrationMissing));
        Assert.Contains("Sql/monitoring/CreateMonitoringScoutShadow.sql", error.Message);
        Assert.Equal(KpiPersistenceServiceCollectionExtensions.V4MigrationMissingMessage, error.Message);

        await WithScratchDatabaseAsync(async (connectionString, run) =>
        {
            // v3 KPI + Triage schema, no v4 migration.
            await run("CreateMonitoringKpi.sql");
            await run("CreateMonitoringTriage.sql");
            AssertFailsFast(connectionString);

            // v4 applied, then the v3 KPI script runs again: v4 columns with v3 procedures is still "v4 missing".
            await run("CreateMonitoringScoutShadow.sql");
            await run("CreateMonitoringKpi.sql");
            AssertFailsFast(connectionString);
        });
    }

    [Fact]
    public async Task KpiPersistence_NoKpiSchema_UsesInMemory()
    {
        var probed = 0;
        using (var provider = new ServiceCollection()
            .AddKpiPersistence(Configuration(FakeConnectionString), _ => { probed++; return KpiSchemaStatus.Absent; })
            .BuildServiceProvider())
        {
            AssertInMemory(provider);
        }
        Assert.Equal(1, probed);

        // No connection string: the database is never probed.
        using (var provider = new ServiceCollection()
            .AddKpiPersistence(new ConfigurationBuilder().Build(), _ => throw new InvalidOperationException("must not probe"))
            .BuildServiceProvider())
        {
            AssertInMemory(provider);
        }

        await WithScratchDatabaseAsync((connectionString, _) =>
        {
            using var provider = new ServiceCollection().AddKpiPersistence(Configuration(connectionString)).BuildServiceProvider();
            AssertInMemory(provider);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task KpiPersistence_V4Schema_UsesSql()
    {
        using (var provider = new ServiceCollection()
            .AddKpiPersistence(Configuration(FakeConnectionString), _ => KpiSchemaStatus.V4)
            .BuildServiceProvider())
        {
            AssertSql(provider);
        }

        await WithScratchDatabaseAsync(async (connectionString, run) =>
        {
            await run("CreateMonitoringKpi.sql");
            await run("CreateMonitoringTriage.sql");
            await run("CreateMonitoringScoutShadow.sql");
            using var provider = new ServiceCollection().AddKpiPersistence(Configuration(connectionString)).BuildServiceProvider();
            AssertSql(provider);
        });
    }

    private static void AssertFailsFast(string connectionString)
    {
        var error = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddKpiPersistence(Configuration(connectionString)));
        Assert.Contains("Sql/monitoring/CreateMonitoringScoutShadow.sql", error.Message);
    }

    private static void AssertInMemory(ServiceProvider provider)
    {
        Assert.IsType<InMemoryKpiDefinitionStore>(provider.GetRequiredService<IKpiDefinitionStore>());
        Assert.IsType<InMemoryKpiBucketStore>(provider.GetRequiredService<IKpiBucketStore>());
        Assert.IsType<InMemoryKpiStateStore>(provider.GetRequiredService<IKpiStateStore>());
        Assert.IsType<InMemoryKpiAnomalyStore>(provider.GetRequiredService<IKpiAnomalyStore>());
        Assert.IsType<InMemoryTriageInvestigationStore>(provider.GetRequiredService<ITriageInvestigationStore>());
        Assert.IsType<InMemoryScoutShadowStore>(provider.GetRequiredService<IScoutShadowStore>());
    }

    private static void AssertSql(ServiceProvider provider)
    {
        Assert.IsType<JourneyMonitor.Infrastructure.Sql.SqlKpiDefinitionStore>(provider.GetRequiredService<IKpiDefinitionStore>());
        Assert.IsType<JourneyMonitor.Infrastructure.Sql.SqlKpiBucketStore>(provider.GetRequiredService<IKpiBucketStore>());
        Assert.IsType<JourneyMonitor.Infrastructure.Sql.SqlKpiStateStore>(provider.GetRequiredService<IKpiStateStore>());
        Assert.IsType<JourneyMonitor.Infrastructure.Sql.SqlKpiAnomalyStore>(provider.GetRequiredService<IKpiAnomalyStore>());
        Assert.IsType<JourneyMonitor.Infrastructure.Sql.SqlTriageInvestigationStore>(provider.GetRequiredService<ITriageInvestigationStore>());
        Assert.IsType<JourneyMonitor.Infrastructure.Sql.SqlScoutShadowStore>(provider.GetRequiredService<IScoutShadowStore>());
    }

    private static IConfiguration Configuration(string connectionString) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["JourneyStore:ConnectionString"] = connectionString })
            .Build();

    private async Task WithScratchDatabaseAsync(Func<string, Func<string, Task>, Task> body)
    {
        var shared = A3SqlTestDatabase.TryGetConnectionString(output);
        if (shared is null)
        {
            return;
        }

        var name = "SD1Scratch_" + Guid.NewGuid().ToString("N")[..16];
        var master = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(shared) { InitialCatalog = "master", Pooling = false };
        var scratch = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(shared) { InitialCatalog = name, Pooling = false }.ConnectionString;
        await using (var connection = new Microsoft.Data.SqlClient.SqlConnection(master.ConnectionString))
        {
            await Dapper.SqlMapper.ExecuteAsync(connection, $"CREATE DATABASE [{name}];");
        }
        try
        {
            await body(scratch, async script =>
            {
                var text = File.ReadAllText(Path.Combine(PortalDirectory(), "src", "JourneyMonitor.Infrastructure", "Sql", "monitoring", script));
                await using var connection = new Microsoft.Data.SqlClient.SqlConnection(scratch);
                await connection.OpenAsync();
                await Dapper.SqlMapper.ExecuteAsync(connection, "IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'monitoring') EXEC(N'CREATE SCHEMA monitoring');");
                foreach (var batch in System.Text.RegularExpressions.Regex.Split(text, @"^\s*GO\s*$",
                    System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                {
                    if (!string.IsNullOrWhiteSpace(batch))
                    {
                        await Dapper.SqlMapper.ExecuteAsync(connection, batch, commandTimeout: 120);
                    }
                }
            });
        }
        finally
        {
            await using var connection = new Microsoft.Data.SqlClient.SqlConnection(master.ConnectionString);
            await Dapper.SqlMapper.ExecuteAsync(connection, $"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}];");
        }
    }

    private static string PortalDirectory([System.Runtime.CompilerServices.CallerFilePath] string thisFile = "")
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