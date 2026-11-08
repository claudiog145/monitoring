using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Monitoring;
using JourneyMonitor.Core.Monitoring.Daemons;
using JourneyMonitor.Core.Monitoring.Evidence;
using JourneyMonitor.Core.Monitoring.Ingestion;
using JourneyMonitor.Core.Monitoring.Inspector;
using JourneyMonitor.Core.Monitoring.Kpi;
using JourneyMonitor.Core.Monitoring.Privacy;
using JourneyMonitor.Core.Monitoring.TokenRefresh;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Core.UseCases;
using JourneyMonitor.Infrastructure.Ai;
using JourneyMonitor.Infrastructure.AppInsights;
using JourneyMonitor.Infrastructure.Fabric;
using JourneyMonitor.Infrastructure.Monitoring;
using JourneyMonitor.Infrastructure.Sql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace JourneyMonitor.Infrastructure.DependencyInjection;

/// <summary>
/// Specification 1000 wave 1: KPI ingestion, pillar health daemons, health reads and the Triage
/// Monkey. Call after <see cref="ServiceCollectionExtensions.AddJourneyMonitorMonitoring"/>, which
/// owns the agent work queue, LLM transports, pause/budget resolvers and notifications used here.
/// </summary>
public static class Spec1000ServiceCollectionExtensions
{
    public static IServiceCollection AddSpec1000(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<HealthMonitoringOptions>(configuration.GetSection(HealthMonitoringOptions.SectionName));
        services.Configure<TriageOptions>(configuration.GetSection(TriageOptions.SectionName));
        services.Configure<KpiIngestionOptions>(configuration.GetSection(KpiIngestionOptions.SectionName));

        services.TryAddSingleton(TimeProvider.System);

        services.AddKpiPersistence(configuration);
        AddDaemonMonitoring(services, configuration);
        AddTokenRefresh(services, configuration);

        services.AddSingleton<IKpiEvaluator, KpiEvaluator>();
        services.AddScoped<ITriageSignalSubmitter, TriageSignalSubmitter>();
        services.AddScoped<IPillarHealthMonitor, PillarHealthMonitorService>();
        services.AddScoped<IEntityDetectionMonitor, EntityDetectionMonitor>();
        services.AddScoped<IKpiIngestionRunner, KpiIngestionService>();
        services.AddScoped<IPillarHealthReader, GetPillarHealthService>();
        // Contract v3 §1.5: HTTP reads/writes default to the active company profile; daemons ignore it.
        services.TryAddScoped<KpiProfileResolver>();

        // Add (not TryAdd) so more sources can sit next to App Insights. Scoped because it holds
        // ITelemetrySource, a transient typed HttpClient that a singleton would pin forever.
        services.AddScoped<IKpiSource, AppInsightsKpiSource>();
        // kql:{queryKey} (contract v4 §5) reuses the App Insights source's target resolution.
        services.TryAddScoped<AppInsightsKpiSource>();
        services.AddScoped<IKpiSource, CatalogKqlKpiSource>();
        services.TryAddScoped<KqlKpiDryRun>();
        AddKqlQueryUsage(services);
        // AG8: company profile saves refuse an alias another profile owns.
        services.TryAddSingleton<TelemetrySourceOwnershipGuard>();
        services.TryAddScoped<TelemetrySourceOwnerPolicy>();
        // FX1: the read-time half - KPI sources and the Scout board refuse an alias another profile owns
        // (catalog cached 30 s, so a transfer takes effect on the next cycle).
        services.TryAddSingleton<TelemetrySourceOwnershipCheck>();

        // FabricBusinessEvidenceProvider needs IHotelBookingKpiSource, registered by
        // AddFabricHotelBookingKpis; without Fabric settings it returns no rows and the Business
        // evidence is reported as unavailable. It is the single Business provider: the incumbent
        // reads Fabric, every other profile is routed to KpiBusinessEvidenceProvider (v4 §5).
        services.TryAddScoped<KpiBusinessEvidenceProvider>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IEvidenceProvider, FabricBusinessEvidenceProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IEvidenceProvider, ApplicationEvidenceProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IEvidenceProvider, InfrastructureEvidenceProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IEvidenceProvider, CodeReliabilityEvidenceProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IEvidenceProvider, RecentChangesEvidenceProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IEvidenceProvider, KnowledgeEvidenceProvider>());

        // Same per-agent router as the Doctor/Scout/Inspector: the provider is resolved per call
        // for the triage agent key, so a Settings-tab switch applies without a restart.
        var triageAgentKey = ResolveTriageAgentKey(configuration);
        services.AddKeyedSingleton<ILlmClient>(triageAgentKey, (sp, _) => new RoutingLlmClient(
            triageAgentKey,
            sp.GetRequiredService<ILlmClientSettingsResolver>(),
            sp.GetRequiredService<GitHubCopilotLlmClient>(),
            sp.GetRequiredService<FoundryLlmClient>(),
            sp.GetRequiredService<ILogger<RoutingLlmClient>>()));

        // InspectorMonkeyRunner takes TriageMonkeyService as an optional constructor parameter; the
        // container injects it because it is registered here. The redactor (contract v4 §5) and the
        // profile store reach it as optional trailing parameters.
        services.TryAddSingleton<IEvidenceRedactor, EvidenceRedactor>();
        services.AddScoped(sp => ActivatorUtilities.CreateInstance<TriageMonkeyService>(
            sp, sp.GetRequiredKeyedService<ILlmClient>(triageAgentKey)));

        return services;
    }

    /// <summary>
    /// Daemon activity log + status. SQL when a JourneyStore connection string is configured and
    /// monitoring.DaemonActivity exists, otherwise an in-memory log. The three daemon services take the
    /// recorder and the run tracker as optional constructor parameters, so the container injects them.
    /// </summary>
    /// <summary>
    /// Contract v4 §5 (AG3): the KQL admin service learns which KPIs use a custom kpi: query, so an
    /// edit is dry-run and a delete is refused while an enabled KPI uses it. Replaces the registration
    /// of AddJourneyMonitorMonitoring (which runs first); hosts without the KQL admin are left alone.
    /// </summary>
    private static void AddKqlQueryUsage(IServiceCollection services)
    {
        services.TryAddSingleton(sp => new KqlQueryUsage(
            sp.GetRequiredService<IKpiDefinitionStore>(),
            async (definition, kqlText, ct) =>
            {
                // The KPI sources are scoped (they hold a transient HttpClient); the admin service is a singleton.
                await using var scope = sp.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
                return await scope.ServiceProvider.GetRequiredService<KqlKpiDryRun>().ValidateAsync(definition, ct, kqlText);
            }));

        if (services.Any(d => d.ServiceType == typeof(KqlQueryAdminService)))
        {
            services.Replace(ServiceDescriptor.Singleton(sp => new KqlQueryAdminService(
                sp.GetRequiredService<IKqlQueryStore>(),
                sp.GetRequiredService<Kql.KqlQueryTextProvider>(),
                sp.GetRequiredService<ITelemetrySourceStore>(),
                sp.GetRequiredService<KqlQueryUsage>())));
        }
    }

    private static void AddDaemonMonitoring(IServiceCollection services, IConfiguration configuration)
    {
        var ingestionCron = configuration["spec1000_ingestion_cron"];
        var monitorCron = configuration["spec1000_monitor_cron"];
        services.Configure<DaemonMonitoringOptions>(configuration.GetSection(DaemonMonitoringOptions.SectionName));
        services.PostConfigure<DaemonMonitoringOptions>(o =>
        {
            if (!string.IsNullOrWhiteSpace(ingestionCron)) o.IngestionSchedule = ingestionCron.Trim();
            if (!string.IsNullOrWhiteSpace(monitorCron)) o.MonitorSchedule = monitorCron.Trim();
        });

        var appConfigConnectionString = configuration[JourneyStoreOptions.AppConfigKey];
        var connectionString = !string.IsNullOrWhiteSpace(appConfigConnectionString)
            ? appConfigConnectionString
            : configuration.GetSection(JourneyStoreOptions.SectionName)["ConnectionString"];

        // Connectivity/auth failures propagate on purpose (see MonitoringSchemaGate): only a missing table degrades.
        var useSql = !string.IsNullOrWhiteSpace(connectionString)
            && MonitoringSchemaGate.IsDaemonActivitySchemaPresent(connectionString);
        if (useSql)
        {
            services.AddOptions<JourneyStoreOptions>();
            services.PostConfigure<JourneyStoreOptions>(o =>
            {
                if (!o.IsConfigured)
                {
                    o.ConnectionString = connectionString;
                }
            });
            services.AddSingleton<IDaemonActivityLog, SqlDaemonActivityLog>();
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(connectionString))
            {
                Console.Error.WriteLine("[JourneyMonitor] monitoring.DaemonActivity (Sql/monitoring/CreateMonitoringDaemonActivity.sql) " +
                    "was not found - daemon activity uses an in-memory log and is lost on restart.");
            }
            services.AddSingleton<IDaemonActivityLog, InMemoryDaemonActivityLog>();
        }

        services.AddSingleton<IDaemonActivityRecorder, DaemonActivityRecorder>();
        services.AddSingleton<IDaemonStatusReader, DaemonStatusReader>();
    }

    /// <summary>
    /// token-refresh daemon (contracts-company ?2.3). The timer reads %spec1000_token_refresh_cron%; the
    /// same value is shown on the daemon card. Needs IAppInsightsTokenRepository (AddJourneyMonitorInfrastructure)
    /// and the host's DataProtection, the same protector the token store uses.
    /// </summary>
    private static void AddTokenRefresh(IServiceCollection services, IConfiguration configuration)
    {
        var tokenRefreshCron = configuration["spec1000_token_refresh_cron"];
        services.Configure<TokenRefreshOptions>(configuration.GetSection(TokenRefreshOptions.SectionName));
        services.PostConfigure<TokenRefreshOptions>(o =>
        {
            if (!string.IsNullOrWhiteSpace(tokenRefreshCron)) o.Schedule = tokenRefreshCron.Trim();
        });
        services.TryAddSingleton<IAppInsightsTokenAcquirer, AppInsightsTokenAcquirer>();
        services.TryAddScoped<IAppInsightsTokenRefreshService, AppInsightsTokenRefreshService>();
    }

    private static string ResolveTriageAgentKey(IConfiguration configuration)
    {
        var configured = configuration[$"{TriageOptions.SectionName}:{nameof(TriageOptions.AgentKey)}"];
        return string.IsNullOrWhiteSpace(configured) ? new TriageOptions().AgentKey : configured.Trim();
    }
}
