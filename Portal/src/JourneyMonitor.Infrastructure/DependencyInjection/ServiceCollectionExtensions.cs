using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Configuration;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Infrastructure.AppInsights;
using JourneyMonitor.Infrastructure.Caching;
using JourneyMonitor.Infrastructure.Company;
using JourneyMonitor.Infrastructure.Demo;
using JourneyMonitor.Infrastructure.Kql;
using JourneyMonitor.Infrastructure.Ai;
using JourneyMonitor.Infrastructure.Monitoring;
using JourneyMonitor.Infrastructure.Notifications;
using JourneyMonitor.Infrastructure.Search;
using JourneyMonitor.Core.Monitoring;
using JourneyMonitor.Core.Monitoring.Inspector;
using JourneyMonitor.Core.UseCases;
using JourneyMonitor.Infrastructure.Sql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Infrastructure.DependencyInjection;

/// <summary>
/// Wires Core's abstractions to their Infrastructure implementations. Called once from each
/// composition root (JourneyMonitor.Functions Program.cs today; JourneyMonitor.Web only needs
/// this if it hosts the API in-process instead of calling Functions over HTTP).
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers ITelemetrySource (Application Insights REST), IKqlQueryTextProvider (embedded
    /// KQL text), IDemoJourneyFixtureProvider (anonymized fixture), and the process-local
    /// 90-second ITelemetryQueryCache (parity with the original Node Map cache).
    /// 
    /// AppInsightsTelemetrySource uses stored tokens (Option 1) with fallback to DefaultAzureCredential.
    /// </summary>
    public static IServiceCollection AddJourneyMonitorInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<InsightsOptions>(configuration.GetSection(InsightsOptions.SectionName));
        services.Configure<DemoJourneyFixtureOptions>(configuration.GetSection(DemoJourneyFixtureOptions.SectionName));

        // The encrypted token store, registered HERE rather than only in each composition root.
        // AppInsightsTelemetrySource and AzureDevOpsSearchTransport both take IAppInsightsTokenService,
        // so leaving it to the hosts meant this extension could not stand up its own graph - exactly
        // what InspectorDependencyInjectionTests exists to catch, and it was failing on it.
        // TryAdd so a host that registers its own (or a fake, in a test) still wins.
        services.TryAddSingleton<IAppInsightsTokenRepository>(sp => new SqlAppInsightsTokenRepository(
            // Empty, never null: the repository rejects null, and a fresh clone with no database
            // must still resolve the graph. A query then fails at call time, where both callers
            // already treat "no usable token" as "fall through to the credential chain".
            configuration.GetConnectionString("DefaultConnection") ?? string.Empty,
            sp.GetRequiredService<ILogger<SqlAppInsightsTokenRepository>>()));
        services.TryAddScoped<IAppInsightsTokenService, AppInsightsTokenService>();

        // Register AppInsightsTelemetrySource with HttpClient and IAppInsightsTokenService dependencies
        services.AddHttpClient<AppInsightsTelemetrySource>()
            .SetHandlerLifetime(TimeSpan.FromMinutes(5));
        services.AddScoped<ITelemetrySource>(sp => sp.GetRequiredService<AppInsightsTelemetrySource>());
        
        // Concrete, not just the interface: AddJourneyMonitorMonitoring decorates it with
        // DbKqlQueryTextProvider, which needs the embedded provider as its fallback.
        services.AddSingleton<KqlQueryTextProvider>();
        services.AddSingleton<IKqlQueryTextProvider>(sp => sp.GetRequiredService<KqlQueryTextProvider>());
        services.AddSingleton<IDemoJourneyFixtureProvider, DemoJourneyFixtureProvider>();
        services.AddSingleton<ITelemetryQueryCache, InMemoryTelemetryQueryCache>();

        return services;
    }

    /// <summary>
    /// Durable journey store over the per-vertical schemas (hotel/car/cruise/air). Requires the
    /// schemas from Sql/*/Create*Schema.sql to already exist; unlike the query cache there is no
    /// in-memory fallback, so callers must skip registration when no connection string is set.
    /// </summary>
    public static IServiceCollection AddSqlJourneyStore(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<JourneyStoreOptions>(configuration.GetSection(JourneyStoreOptions.SectionName));

        // Prefer App Configuration's own key name so the value is not duplicated; the section
        // above stays as the local override a fresh clone can set without any Azure resource.
        var appConfigConnectionString = configuration[JourneyStoreOptions.AppConfigKey];
        if (!string.IsNullOrWhiteSpace(appConfigConnectionString))
        {
            services.PostConfigure<JourneyStoreOptions>(o => o.ConnectionString = appConfigConnectionString);
        }

        services.Configure<JourneySyncOptions>(configuration.GetSection(JourneySyncOptions.SectionName));
        services.AddSingleton<IJourneyStore, SqlJourneyStore>();
        services.AddSingleton<IJourneySnapshotStore>(sp =>
            (SqlJourneyStore)sp.GetRequiredService<IJourneyStore>());
        services.AddSingleton<IDashboardMetricStore, SqlDashboardMetricStore>();
        services.AddSingleton<IIncidentStore, SqlIncidentStore>();
        services.AddSingleton<IIncidentReader, SqlIncidentReader>();
        services.AddSingleton<IOverviewReader, SqlOverviewReader>();
        services.AddSingleton<IIncidentSchemaGate, SqlIncidentSchemaGate>();
        services.AddSingleton<ILatencySpikeSettingsStore, SqlLatencySpikeSettingsStore>();
        services.AddSingleton<ILlmClientSettingsStore, SqlLlmClientSettingsStore>();
        // SLO burn-rate alerting (see docs/product/wi-238036-threshold-research-and-wi-package-2026-09-29.md).
        services.AddSingleton<IGoldenSignalStore, SqlGoldenSignalStore>();
        services.AddSingleton<ISloDefinitionStore, SqlSloDefinitionStore>();
        services.AddSingleton<IExceptionOccurrenceCounter, SqlExceptionOccurrenceCounter>();
        services.AddSingleton<IAlertChannelSettingsStore, SqlAlertChannelSettingsStore>();
        // Club dropdown source (brand.Information). Safe to register unconditionally: with no
        // connection string it reports IsConfigured=false and GetClubsService uses the demo list.
        services.AddSingleton<IBrandCatalog, SqlBrandCatalog>();
        // Full club / Fusion application map (brand.ClubInventory) for the Club Map tab.
        services.AddSingleton<IClubInventoryStore, SqlClubInventoryStore>();
        return services;
    }

    /// <summary>
    /// Latency Spike Monkey + Doctor Monkey (2026-09-17 MVP; SQL persistence added 2026-09-22).
    /// Findings/DoctorReports/KnowledgeChunks/MonkeyRun timestamps persist to the same
    /// JourneyStore SQL database (schema "monitoring") when JourneyStore:ConnectionString is
    /// configured; falls back to the original process-local InMemory* implementations otherwise
    /// so a fresh clone with no database still runs. The LLM client is GitHub Copilot via a
    /// "gho_" OAuth token (Ai:GitHubToken).
    /// </summary>
    public static IServiceCollection AddJourneyMonitorMonitoring(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<MonitoringOptions>(configuration.GetSection(MonitoringOptions.SectionName));
        services.Configure<AiOptions>(configuration.GetSection(AiOptions.SectionName));
        services.Configure<FoundryOptions>(configuration.GetSection(FoundryOptions.SectionName));
        services.Configure<KnowledgeImportOptions>(configuration.GetSection(KnowledgeImportOptions.SectionName));
        services.Configure<RcaOptions>(configuration.GetSection(RcaOptions.SectionName));
        // One named instance per board: the two scouts are the same pipeline over different data
        // and must be tunable apart (window, cadence, per-cycle cap, minimum rank).
        services.Configure<ScoutOptions>(ScoutBoard.Latency, configuration.GetSection(ScoutOptions.LatencySectionName));
        services.Configure<ScoutOptions>(ScoutBoard.Exception, configuration.GetSection(ScoutOptions.ExceptionSectionName));
        services.Configure<InspectorOptions>(configuration.GetSection(InspectorOptions.SectionName));
        services.Configure<RedAlertOptions>(configuration.GetSection(RedAlertOptions.SectionName));
        services.Configure<AzureDevOpsSearchOptions>(configuration.GetSection(AzureDevOpsSearchOptions.SectionName));

        services.AddSingleton<ILatencySpikeSettingsResolver, LatencySpikeSettingsResolver>();
        services.AddSingleton<ILlmClientSettingsResolver, LlmClientSettingsResolver>();
        // Singleton so its short-lived snapshot is actually shared across runners and requests.
        services.AddSingleton<IAgentPauseResolver, AgentPauseResolver>();
        services.AddSingleton<IAgentBudgetResolver, AgentBudgetResolver>();

        // Same precedence as AddSqlJourneyStore: App Configuration's key first, then the local
        // JourneyStore:ConnectionString section - whichever is set decides SQL vs in-memory here.
        // A blank AppConfig value (env var present but empty) must be treated as absent, same as
        // AddSqlJourneyStore's own IsNullOrWhiteSpace check - "??" alone would stop at the blank
        // value and never fall through to the section, silently choosing InMemory* even though
        // AddSqlJourneyStore itself ends up using the real JourneyStore:ConnectionString.
        var appConfigConnectionString = configuration[JourneyStoreOptions.AppConfigKey];
        var journeyStoreConnectionString = !string.IsNullOrWhiteSpace(appConfigConnectionString)
            ? appConfigConnectionString
            : configuration.GetSection(JourneyStoreOptions.SectionName)["ConnectionString"];

        // A connection string being configured is not proof the monitoring.* schema/procs have
        // actually been applied against that database yet (see MonitoringSchemaGate) - a first
        // deployment against an existing JourneyStore without having run the monitoring/*.sql
        // scripts would otherwise select the SQL repos and fail every timer run/API call with a
        // missing-table error instead of falling back like every other "not configured yet" case.
        // A connectivity/auth failure while checking is NOT caught here - it propagates and fails
        // startup instead of silently choosing InMemory* for the rest of the process's lifetime.
        var useSqlMonitoringRepos = !string.IsNullOrWhiteSpace(journeyStoreConnectionString)
            && MonitoringSchemaGate.IsProvisioned(journeyStoreConnectionString);
        if (!string.IsNullOrWhiteSpace(journeyStoreConnectionString) && !useSqlMonitoringRepos)
        {
            Console.Error.WriteLine(
                "[JourneyMonitor] JourneyStore:ConnectionString is set but the monitoring.* schema " +
                "(Sql/monitoring/*.sql) was not found on that database - falling back to in-memory " +
                "Findings/DoctorReports/KnowledgeChunks/MonkeyRun/LlmCallLog instead of failing every " +
                "call. Run docker/init-sql.sh's script list (or apply Sql/monitoring/*.sql manually) " +
                "against this database, then restart, to enable durable persistence.");
        }

        if (useSqlMonitoringRepos)
        {
            services.AddSingleton<IFindingsRepository, SqlFindingsRepository>();
            services.AddSingleton<IDoctorReportsRepository, SqlDoctorReportsRepository>();
            // The knowledge base is the Doctor's, so it embeds with the Doctor's provider: a chunk
            // written under one provider and queried under another cannot be compared at all.
            services.AddSingleton<IKnowledgeRetriever>(sp => ActivatorUtilities.CreateInstance<SqlKnowledgeRetriever>(
                sp, sp.GetRequiredKeyedService<ILlmClient>(LlmAgents.Doctor)));
            services.AddSingleton<IMonkeyRunTracker, SqlMonkeyRunTracker>();
            services.AddSingleton<ILlmCallLogRepository, SqlLlmCallLogRepository>();
            services.AddSingleton<INotificationLogRepository, SqlNotificationLogRepository>();
            services.AddSingleton<IInvestigationPackageRepository, SqlInvestigationPackageRepository>();
            services.AddSingleton<IScoutRunRepository, SqlScoutRunRepository>();
            services.AddSingleton<IScoutLlmCallLogRepository, SqlScoutLlmCallLogRepository>();
        }
        else
        {
            services.AddSingleton<IFindingsRepository, InMemoryFindingsRepository>();
            services.AddSingleton<IDoctorReportsRepository, InMemoryDoctorReportsRepository>();
            services.AddSingleton<IKnowledgeRetriever>(sp => ActivatorUtilities.CreateInstance<InMemoryKnowledgeRetriever>(
                sp, sp.GetRequiredKeyedService<ILlmClient>(LlmAgents.Doctor)));
            services.AddSingleton<IMonkeyRunTracker, InMemoryMonkeyRunTracker>();
            services.AddSingleton<ILlmCallLogRepository, InMemoryLlmCallLogRepository>();
            services.AddSingleton<INotificationLogRepository, InMemoryNotificationLogRepository>();
            services.AddSingleton<IInvestigationPackageRepository, InMemoryInvestigationPackageRepository>();
            services.AddSingleton<IScoutRunRepository, InMemoryScoutRunRepository>();
            services.AddSingleton<IScoutLlmCallLogRepository, InMemoryScoutLlmCallLogRepository>();
        }

        // The Inspector's tables ship in a later script than the rest of monitoring.*, so an
        // environment provisioned before it must not be handed the SQL repositories - it would
        // fail every minute on a missing table instead of degrading to the documented in-memory
        // fallback. Checked only when the rest of the schema is already there; without a
        // connection string there is nothing to check.
        var useSqlInspectorRepos = useSqlMonitoringRepos
            && MonitoringSchemaGate.IsInspectorProvisioned(journeyStoreConnectionString!);
        if (useSqlMonitoringRepos && !useSqlInspectorRepos)
        {
            Console.Error.WriteLine(
                "[JourneyMonitor] the monitoring.* schema is provisioned but the Inspector tables " +
                "(Sql/monitoring/CreateMonitoringInspector.sql) are not - the Inspector Monkey will use an " +
                "in-memory queue/case store, which is lost on restart. Apply that script and restart to " +
                "enable durable L3 investigations.");
        }

        if (useSqlInspectorRepos)
        {
            services.AddSingleton<IAgentWorkQueue, SqlAgentWorkQueue>();
            services.AddSingleton<IForensicCaseRepository, SqlForensicCaseRepository>();
            services.AddSingleton<IAgentTranscriptRepository, SqlAgentTranscriptRepository>();
        }
        else
        {
            services.AddSingleton<IAgentWorkQueue, InMemoryAgentWorkQueue>();
            services.AddSingleton<IForensicCaseRepository, InMemoryForensicCaseRepository>();
            services.AddSingleton<IAgentTranscriptRepository, InMemoryAgentTranscriptRepository>();
        }

        // Same late-migration reasoning as the Inspector block above, and for the same stakes: the
        // RCA Agent's table (Sql/monitoring/CreateMonitoringRca.sql) ships later than
        // Finding/NotificationLog too. Without this check an environment that never ran that
        // script would still get SqlRcaReportRepository, and every composition would silently fail
        // to persist - both the on-demand route and the auto-composer still hand back a fully
        // composed document, so the only symptom is the RCA never showing up on the RCA Agent card.
        var useSqlRcaReportRepo = useSqlMonitoringRepos
            && MonitoringSchemaGate.IsRcaProvisioned(journeyStoreConnectionString!);
        if (useSqlMonitoringRepos && !useSqlRcaReportRepo)
        {
            Console.Error.WriteLine(
                "[JourneyMonitor] the monitoring.* schema is provisioned but monitoring.RcaReport " +
                "(Sql/monitoring/CreateMonitoringRca.sql) is not - composed RCAs will only hold until " +
                "this process restarts, and the on-demand route will still report success. Apply that " +
                "script and restart for durable RCA documents.");
        }

        services.AddSingleton<IRcaReportRepository>(useSqlRcaReportRepo
            ? sp => ActivatorUtilities.CreateInstance<SqlRcaReportRepository>(sp)
            : _ => new InMemoryRcaReportRepository());

        // Same late-migration reasoning as the Inspector block above. The resolver never throws,
        // so picking the in-memory store here degrades "pause" to process-local rather than
        // breaking any agent - but a pause that silently did not survive a restart would be a
        // lie, hence the warning.
        var useSqlAgentPauseStore = useSqlMonitoringRepos
            && MonitoringSchemaGate.IsAgentPauseProvisioned(journeyStoreConnectionString!);
        if (useSqlMonitoringRepos && !useSqlAgentPauseStore)
        {
            Console.Error.WriteLine(
                "[JourneyMonitor] the monitoring.* schema is provisioned but monitoring.AgentPauseSetting " +
                "(Sql/monitoring/CreateMonitoringAgentPause.sql) is not - pausing an agent will only hold " +
                "until this process restarts. Apply that script and restart for a durable pause.");
        }

        if (useSqlAgentPauseStore)
        {
            services.AddSingleton<IAgentPauseStore, SqlAgentPauseStore>();
            services.AddSingleton<IAgentBudgetStore, SqlAgentBudgetStore>();
        }
        else
        {
            services.AddSingleton<IAgentPauseStore, InMemoryAgentPauseStore>();
            services.AddSingleton<IAgentBudgetStore, InMemoryAgentBudgetStore>();
        }

        // Same reasoning again. The resolver never throws, so the in-memory store degrades the
        // ownership map to process-local rather than breaking an investigation - and an empty map
        // is reported as "no owner recorded", which is the honest state of the real one today.
        var useSqlServiceOwnerStore = useSqlMonitoringRepos
            && MonitoringSchemaGate.IsServiceOwnerProvisioned(journeyStoreConnectionString!);
        if (useSqlMonitoringRepos && !useSqlServiceOwnerStore)
        {
            Console.Error.WriteLine(
                "[JourneyMonitor] the monitoring.* schema is provisioned but monitoring.ServiceOwner " +
                "(Sql/monitoring/CreateMonitoringServiceOwner.sql) is not - service owners will only hold " +
                "until this process restarts, and every Red Alert will report that nobody owns the service.");
        }

        if (useSqlServiceOwnerStore)
        {
            services.AddSingleton<IServiceOwnerStore, SqlServiceOwnerStore>();
        }
        else
        {
            services.AddSingleton<IServiceOwnerStore, InMemoryServiceOwnerStore>();
        }

        services.AddSingleton<IServiceOwnerResolver, ServiceOwnerResolver>();

        // Same late-migration reasoning once more, with a softer failure: both stores start from
        // TelemetrySourceDefaults, so KQL still resolves either way. Without the table the catalog
        // is process-local - an edited resource id is lost on restart, and nobody can tell who
        // changed it - which is exactly what the audit trail exists to prevent.
        var useSqlTelemetrySourceStore = useSqlMonitoringRepos
            && MonitoringSchemaGate.IsTelemetrySourceProvisioned(journeyStoreConnectionString!);
        if (useSqlMonitoringRepos && !useSqlTelemetrySourceStore)
        {
            Console.Error.WriteLine(
                "[JourneyMonitor] the monitoring.* schema is provisioned but monitoring.TelemetrySource " +
                "(Sql/monitoring/CreateMonitoringTelemetrySource.sql) is not - telemetry sources fall back " +
                "to the built-in resource ids, and any source edited in the portal is lost on restart.");
        }

        if (useSqlTelemetrySourceStore)
        {
            services.AddSingleton<ITelemetrySourceStore, SqlTelemetrySourceStore>();
        }
        else
        {
            services.AddSingleton<ITelemetrySourceStore, InMemoryTelemetrySourceStore>();
        }

        services.AddSingleton<IKqlSourceResolver, KqlSourceResolver>();

        // Editable KQL. Same reasoning as above, and the mildest failure of all: without the table
        // every key resolves to the text embedded in the assembly, which is the text that shipped.
        var useSqlKqlQueryStore = useSqlMonitoringRepos
            && MonitoringSchemaGate.IsKqlQueryProvisioned(journeyStoreConnectionString!);
        if (useSqlMonitoringRepos && !useSqlKqlQueryStore)
        {
            Console.Error.WriteLine(
                "[JourneyMonitor] the monitoring.* schema is provisioned but monitoring.KqlQuery " +
                "(Sql/monitoring/CreateMonitoringKqlQuery.sql) is not - queries edited in the portal are " +
                "lost on restart and every agent runs the KQL embedded in the build.");
        }

        if (useSqlKqlQueryStore)
        {
            services.AddSingleton<IKqlQueryStore, SqlKqlQueryStore>();
        }
        else
        {
            services.AddSingleton<IKqlQueryStore, InMemoryKqlQueryStore>();
        }

        // Decorates the embedded provider registered in AddJourneyMonitorInfrastructure, so a
        // stored row wins and anything unset falls back to the built-in file.
        services.AddSingleton<DbKqlQueryTextProvider>();
        services.AddSingleton<IKqlQueryTextProvider>(sp => sp.GetRequiredService<DbKqlQueryTextProvider>());

        // Built on the EMBEDDED provider on purpose: "revert to built-in" and the editor's diff
        // both need the text that shipped, which the decorator would replace with the override.
        services.AddSingleton(sp => new KqlQueryAdminService(
            sp.GetRequiredService<IKqlQueryStore>(),
            sp.GetRequiredService<KqlQueryTextProvider>(),
            sp.GetRequiredService<ITelemetrySourceStore>()));

        // Both transports are registered as typed clients (pooled HttpMessageHandler) but exposed
        // as singletons: AddHttpClient<T>() alone resolves a NEW instance per request, which would
        // defeat GitHubCopilotLlmClient's 45-min session-token cache and FoundryLlmClient's Entra
        // token cache (both instance fields), multiplying auth round-trips for no reason.
        services.AddHttpClient<GitHubCopilotLlmClient>();
        services.AddHttpClient<FoundryLlmClient>()
            // The endpoint host is validated before every call (FoundryEndpointValidator), but a
            // trusted host redirecting the request elsewhere must not carry the Bearer token with
            // it - this is the transport-level half of that same guarantee.
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });

        // One router per agent. The provider is resolved PER CALL inside the router (see
        // RoutingLlmClient), so a Settings-tab switch applies without restarting the host; what the
        // key fixes is only which agent's override that router asks for.
        services.AddKeyedSingleton<ILlmClient>(LlmAgents.Doctor, (sp, _) => BuildLlmRouter(sp, LlmAgents.Doctor));
        services.AddKeyedSingleton<ILlmClient>(LlmAgents.Scout, (sp, _) => BuildLlmRouter(sp, LlmAgents.Scout));
        // The Inspector needs a plain-completion client too, not just the tool-calling one: the RCA
        // Agent writes up its cases and must follow the same provider.
        services.AddKeyedSingleton<ILlmClient>(LlmAgents.Inspector, (sp, _) => BuildLlmRouter(sp, LlmAgents.Inspector));

        // Un-keyed default for callers with no agent identity of their own (diagnostics, tooling).
        services.AddSingleton<ILlmClient>(sp => BuildLlmRouter(sp, LlmAgents.Unspecified));

        // Only the Inspector does tool-calling, so this router is pinned to its override.
        services.AddSingleton<IToolCallingLlmClient>(sp => BuildLlmRouter(sp, LlmAgents.Inspector));

        services.AddSingleton<ILlmModelCatalog>(sp => new RoutingLlmModelCatalog(
            sp.GetRequiredService<GitHubCopilotLlmClient>(),
            sp.GetRequiredService<FoundryLlmClient>()));

        // Corpus import. Embeds with the DOCTOR's provider because the knowledge base is the
        // Doctor's: indexing under one provider and querying under another produces vectors that
        // cannot be compared at all (see KnowledgeChunk.EmbeddingModel).
        services.AddSingleton<IKnowledgeDocumentSource, FileSystemKnowledgeDocumentSource>();
        services.AddScoped(sp => new KnowledgeImportService(
            sp.GetRequiredService<IKnowledgeDocumentSource>(),
            sp.GetRequiredService<IKnowledgeRetriever>(),
            sp.GetRequiredKeyedService<ILlmClient>(LlmAgents.Doctor),
            sp.GetRequiredService<ILogger<KnowledgeImportService>>()));

        // The RCA Agent writes up the Inspector's finished cases, so it follows the INSPECTOR's
        // provider: the two always talk about the same investigation, and splitting them would let
        // the write-up come from a different model than the analysis it describes.
        services.AddScoped<IRcaAgent>(sp => new RcaAgentService(
            sp.GetRequiredKeyedService<ILlmClient>(LlmAgents.Inspector),
            sp.GetRequiredService<IKnowledgeRetriever>(),
            sp.GetRequiredService<IOptions<RcaOptions>>(),
            sp.GetRequiredService<ILogger<RcaAgentService>>()));

        services.AddScoped<RcaAutoComposer>();

        services.AddScoped<IErrorClassifier, HeuristicErrorClassifier>();

        // Both concrete detectors are registered by their own type (not ILatencySpikeDetector)
        // so CompositeLatencySpikeDetector - the one type actually exposed as ILatencySpikeDetector
        // - can resolve each directly and dispatch per domain (Monitoring:SqlDetectorDomains).
        services.AddScoped<LatencySpikeDetectorService>();
        services.AddScoped<SqlLatencySpikeDetectorService>();
        services.AddScoped<ILatencySpikeDetector, CompositeLatencySpikeDetector>();

        // Built explicitly so the Doctor gets ITS provider override rather than the un-keyed
        // default - the rest of the constructor still comes from the container.
        services.AddScoped<IDoctorAgent>(sp => ActivatorUtilities.CreateInstance<DoctorAgentService>(
            sp, sp.GetRequiredKeyedService<ILlmClient>(LlmAgents.Doctor)));

        // SLO burn-rate engine: reuses the golden-signal buckets GoldenSignalCollector writes
        // every cycle (see SloBurnRateMonkeyFunction), independent of the legacy flat-threshold
        // Latency Spike Monkey detectors above - both can run side by side during the rollout.
        services.AddScoped<GoldenSignalCollector>();
        services.AddScoped<IBurnRateDetector, BurnRateDetector>();

        // Scout Monkey (detection phase). The club resolver is a singleton because it caches
        // brand.Information for 10 minutes - a scoped instance would re-read the catalogue for
        // every candidate of every cycle.
        services.AddSingleton<IClubIdentityResolver, ClubIdentityResolver>();

        // Company vocabulary seam. There is no built-in company any more: the adapter is built from
        // a monitoring.CompanyProfile row, so adding a customer is a row and never a code change.
        services.AddCompanyProfileStore(configuration);
        services.AddProfileDrivenCompany(configuration);
        services.AddTelemetryDiscovery();

        // Composing the board from the profile is the last step of the migration and is opt-in
        // until it has been validated against a live resource - see the remark on the composed
        // source for why turning it on blind would degrade identity attribution invisibly.
        if (string.Equals(configuration["Scout:UseComposedBoard"], "true", StringComparison.OrdinalIgnoreCase))
        {
            services.AddScoped<IScoutBoardQueryTextSource, ComposedScoutBoardQueryTextSource>();
        }
        else
        {
            services.AddScoped<IScoutBoardQueryTextSource, EmbeddedScoutBoardQueryTextSource>();
        }

        services.AddScoped<IScoutBoardReader, ScoutBoardService>();
        services.AddScoped<IScoutAssessor>(sp => ActivatorUtilities.CreateInstance<ScoutAssessorService>(
            sp, sp.GetRequiredKeyedService<ILlmClient>(LlmAgents.Scout)));
        services.AddScoped<IScoutEvidenceCollector, ScoutEvidenceCollector>();
        services.AddScoped<ScoutMonkeyService>();

        // Inspector Monkey (L3 analysis phase). Everything is scoped per invocation except the
        // tools, which carry no state: InspectorRecorder holds the per-investigation counters, so
        // a singleton would share one lock across every concurrent run for no reason.
        //
        // The four LogsAgent tools are registered as IAgentTool and filtered by OwnerAgent inside
        // the specialist - a tool belongs to exactly one agent, and handing every tool to every
        // agent is what the specialisation exists to avoid.
        services.AddScoped<IAgentTool, DrillDownEventsTool>();
        services.AddScoped<IAgentTool, ClubBaselineTool>();
        services.AddScoped<IAgentTool, SignatureBreakdownTool>();
        services.AddScoped<IAgentTool, ClientBreakdownTool>();

        // Registration order is the order the blackboard and the card list them.
        services.AddScoped<IInspectorSpecialist, LogsAgentSpecialist>();
        services.AddScoped<IInspectorSpecialist, MemoryAgentSpecialist>();
        // Code and documentation become real agents when a search source is configured, and stay
        // honestly sourceless when it is not. The sourceless path must remain reachable: "found
        // nothing" and "had nowhere to look" are opposite statements.
        if (new AzureDevOpsSearchOptions
            {
                Enabled = configuration.GetValue<bool?>($"{AzureDevOpsSearchOptions.SectionName}:Enabled"),
                Organization = configuration[$"{AzureDevOpsSearchOptions.SectionName}:Organization"],
                Project = configuration[$"{AzureDevOpsSearchOptions.SectionName}:Project"],
            }.IsConfigured)
        {
            services.AddHttpClient<AzureDevOpsSearchTransport>();
            services.AddScoped<IDocumentationSearch, AzureDevOpsDocumentationSearch>();
            services.AddScoped<ICodeSearch, AzureDevOpsCodeSearch>();
            services.AddScoped<IInspectorSpecialist, DocsAgentSpecialist>();
            services.AddScoped<IInspectorSpecialist, RealCodeAgentSpecialist>();
        }
        else
        {
            services.AddScoped<IInspectorSpecialist>(_ => new SourcelessDocsAgentSpecialist(
                "no runbook or document store is wired up, so this agent could not have found documentation even if some exists. " +
                "Nothing it reports is evidence about what is documented."));
            services.AddScoped<IInspectorSpecialist, CodeAgentSpecialist>();
        }
        services.AddScoped<IInspectorSpecialist, ReproAgentSpecialist>();

        services.AddScoped<InspectorRecorder>();
        services.AddScoped<InspectorPlanner>();
        services.AddScoped<IGroundingService, InspectorGroundingService>();
        services.AddScoped<IVerdictService, InspectorVerdictService>();
        services.AddScoped<IRedAlertPolicy, RedAlertPolicy>();
        services.AddScoped<InspectorMonkeyService>();
        services.AddScoped<InspectorMonkeyRunner>();

        // Email: 3 providers registered in priority order (Smtp, then ACS, then SendGrid) -
        // EmailAlertNotifier's IEnumerable<IEmailSender> preserves registration order and tries
        // each enabled+configured one until one succeeds (fallback chain, not an exclusive pick).
        services.AddSingleton<IEmailSender, SmtpEmailSender>();
        services.AddSingleton<IEmailSender, AcsEmailSender>();
        services.AddHttpClient<SendGridEmailSender>();
        services.AddSingleton<IEmailSender>(sp => sp.GetRequiredService<SendGridEmailSender>());

        // Notification channels, fanned out by NotificationService in registration order. Toast is
        // first because it is the only one that cannot fail, so the in-portal trace exists even
        // when every outbound transport is off. TeamsAlertNotifier follows the same
        // AddHttpClient<TConcrete>() + AddSingleton<TInterface>(resolve it) pattern as
        // GitHubCopilotLlmClient above: AddHttpClient<TInterface,TImpl>() alone would register it
        // as transient.
        services.AddSingleton<INotificationChannel, ToastNotificationChannel>();
        services.AddSingleton<INotificationChannel, EmailAlertNotifier>();
        services.AddHttpClient<TeamsAlertNotifier>();
        services.AddSingleton<INotificationChannel>(sp => sp.GetRequiredService<TeamsAlertNotifier>());
        services.AddSingleton<INotificationService, NotificationService>();

        return services;
    }

    /// <summary>
    /// One <see cref="RoutingLlmClient"/> bound to an agent. Both transports are resolved once here
    /// (they are singletons holding their own credential caches); the provider decision itself is
    /// made per call inside the router.
    /// </summary>
    private static RoutingLlmClient BuildLlmRouter(IServiceProvider sp, string agent) => new(
        agent,
        sp.GetRequiredService<ILlmClientSettingsResolver>(),
        sp.GetRequiredService<GitHubCopilotLlmClient>(),
        sp.GetRequiredService<FoundryLlmClient>(),
        sp.GetRequiredService<ILogger<RoutingLlmClient>>());
}
