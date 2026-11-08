using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Infrastructure.Monitoring;
using JourneyMonitor.Infrastructure.Sql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Infrastructure.DependencyInjection;

/// <summary>What the JourneyStore database holds of the Specification 1000 KPI/Triage schema.</summary>
public enum KpiSchemaStatus
{
    /// <summary>monitoring.KpiDefinition or monitoring.TriageInvestigation is missing: the KPI schema was never applied.</summary>
    Absent,

    /// <summary>
    /// The KPI schema exists but the contract v4 migration (Sql/monitoring/CreateMonitoringScoutShadow.sql)
    /// does not - or only partly: v4 columns with v3 procedures, e.g. after the v3 KPI script ran again.
    /// </summary>
    V4MigrationMissing,

    /// <summary>KPI schema, entity-grain columns, v4 procedures and monitoring.ScoutShadowComparison are all present.</summary>
    V4,
}

/// <summary>
/// Specification 1000 persistence: KPI definitions/buckets/samples/state/anomalies/watermarks, triage
/// investigations and Scout shadow comparisons. SQL when a JourneyStore connection string is configured
/// and the KPI + Triage schema with the contract v4 migration is present on it. Process-local singletons
/// only when there is no connection string or the KPI schema is entirely absent (same fallback rule as
/// AddJourneyMonitorMonitoring). A KPI schema without the v4 migration fails startup: falling back would
/// silently move existing durable KPI and triage data to empty in-memory stores.
/// </summary>
public static class KpiPersistenceServiceCollectionExtensions
{
    public const string V4MigrationMissingMessage =
        "[JourneyMonitor] The Specification 1000 KPI schema (monitoring.KpiDefinition / monitoring.TriageInvestigation) " +
        "is present on the JourneyStore database, but the contract v4 migration is missing or incomplete (entity-grain " +
        "columns, v4 KPI procedures, monitoring.ScoutShadowComparison). Apply Sql/monitoring/CreateMonitoringScoutShadow.sql " +
        "(after CreateMonitoringKpi.sql) and restart. Refusing to fall back to in-memory stores, which would hide the " +
        "existing KPI and triage data.";

    public static IServiceCollection AddKpiPersistence(this IServiceCollection services, IConfiguration configuration) =>
        services.AddKpiPersistence(configuration, MonitoringSchemaGate.GetKpiSchemaStatus);

    /// <summary>
    /// Same as <see cref="AddKpiPersistence(IServiceCollection, IConfiguration)"/> with the schema probe
    /// supplied by the caller (tests). The probe is only called when a connection string is configured.
    /// </summary>
    public static IServiceCollection AddKpiPersistence(this IServiceCollection services, IConfiguration configuration, Func<string, KpiSchemaStatus> schemaProbe)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(schemaProbe);

        // Same precedence as AddJourneyMonitorMonitoring: App Configuration's key first (a blank value
        // counts as absent), then the local JourneyStore:ConnectionString section.
        var appConfigConnectionString = configuration[JourneyStoreOptions.AppConfigKey];
        var connectionString = !string.IsNullOrWhiteSpace(appConfigConnectionString)
            ? appConfigConnectionString
            : configuration.GetSection(JourneyStoreOptions.SectionName)["ConnectionString"];

        // Connectivity/auth failures propagate on purpose (see MonitoringSchemaGate): only a missing
        // schema degrades to in-memory.
        var status = string.IsNullOrWhiteSpace(connectionString) ? KpiSchemaStatus.Absent : schemaProbe(connectionString);

        if (status == KpiSchemaStatus.V4MigrationMissing)
        {
            throw new InvalidOperationException(V4MigrationMissingMessage);
        }

        if (status == KpiSchemaStatus.V4)
        {
            // The Sql* stores read IOptions<JourneyStoreOptions>; make sure it carries the connection
            // string chosen here even when AddSqlJourneyStore was not called.
            services.AddOptions<JourneyStoreOptions>();
            services.PostConfigure<JourneyStoreOptions>(o =>
            {
                if (!o.IsConfigured)
                {
                    o.ConnectionString = connectionString;
                }
            });

            services.AddSingleton<IKpiDefinitionStore, SqlKpiDefinitionStore>();
            services.AddSingleton<IKpiBucketStore, SqlKpiBucketStore>();
            services.AddSingleton<IKpiSampleStore, SqlKpiSampleStore>();
            services.AddSingleton<IKpiStateStore, SqlKpiStateStore>();
            services.AddSingleton<IKpiAnomalyStore, SqlKpiAnomalyStore>();
            services.AddSingleton<IIngestionWatermarkStore, SqlIngestionWatermarkStore>();
            services.AddSingleton<ITriageInvestigationStore, SqlTriageInvestigationStore>();
            services.AddSingleton<IScoutShadowStore>(sp => new SqlScoutShadowStore(
                sp.GetRequiredService<IOptions<JourneyStoreOptions>>(), sp.GetService<TimeProvider>()));
            return services;
        }

        Console.Error.WriteLine(string.IsNullOrWhiteSpace(connectionString)
            ? "[JourneyMonitor] JourneyStore:ConnectionString is not set - KPI definitions, buckets, anomalies, " +
              "triage investigations and Scout shadow comparisons use in-memory stores and are lost on restart."
            : "[JourneyMonitor] JourneyStore:ConnectionString is set but monitoring.KpiDefinition / " +
              "monitoring.TriageInvestigation (Sql/monitoring/CreateMonitoringKpi.sql, CreateMonitoringTriage.sql, " +
              "then CreateMonitoringScoutShadow.sql) were not found - KPI, triage and Scout shadow persistence falls " +
              "back to in-memory stores. Apply those scripts and restart to enable durable persistence.");

        services.AddSingleton<InMemoryKpiDefinitionStore>(sp => new InMemoryKpiDefinitionStore(sp.GetService<TimeProvider>()));
        services.AddSingleton<IKpiDefinitionStore>(sp => sp.GetRequiredService<InMemoryKpiDefinitionStore>());
        services.AddSingleton<IKpiBucketStore>(sp => new InMemoryKpiBucketStore(sp.GetService<TimeProvider>()));
        services.AddSingleton<IKpiSampleStore, InMemoryKpiSampleStore>();
        services.AddSingleton<IKpiStateStore, InMemoryKpiStateStore>();
        services.AddSingleton<IKpiAnomalyStore>(sp => new InMemoryKpiAnomalyStore(sp.GetRequiredService<IKpiDefinitionStore>()));
        services.AddSingleton<IIngestionWatermarkStore, InMemoryIngestionWatermarkStore>();
        services.AddSingleton<ITriageInvestigationStore>(sp => new InMemoryTriageInvestigationStore(sp.GetService<TimeProvider>()));
        services.AddSingleton<IScoutShadowStore>(sp => new InMemoryScoutShadowStore(sp.GetService<TimeProvider>()));
        return services;
    }
}
