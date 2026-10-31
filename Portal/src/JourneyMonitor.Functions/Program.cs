using JourneyMonitor.Core.Monitoring;
using JourneyMonitor.Core.UseCases;
using JourneyMonitor.Infrastructure.Sql;
using JourneyMonitor.Infrastructure.DependencyInjection;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

builder.Services.AddApplicationInsightsTelemetryWorkerService();
builder.Services.ConfigureFunctionsApplicationInsights();

// Encrypts the Doctor Monkey's GitHub token at rest (see SqlLlmClientSettingsStore) with a key
// kept outside the SQL database, not the token itself. DataProtection:KeysDirectory (blank
// locally = OS temp dir, survives `func start` restarts on the same machine; docker-compose
// mounts a persistent volume there so it also survives container recreates). A real Azure
// deployment needs a durable, shared key store (Key Vault / Blob Storage - see
// Microsoft.AspNetCore.DataProtection.AzureStorage/AzureKeyVault) before this is safe across
// multiple instances or redeploys - not wired yet, tracked as a known gap alongside the QA
// schema-provisioning gap (see AddJourneyMonitorMonitoring's schema check).
var keysDirectory = builder.Configuration["DataProtection:KeysDirectory"];
builder.Services.AddDataProtection()
    .SetApplicationName("JourneyMonitor")
    .PersistKeysToFileSystem(new DirectoryInfo(
        string.IsNullOrWhiteSpace(keysDirectory)
            ? Path.Combine(Path.GetTempPath(), "JourneyMonitorDataProtectionKeys")
            : keysDirectory));

// AppInsights Token Management (Option 1 - External Tokens)
builder.Services.AddSingleton<IAppInsightsTokenRepository>(sp =>
{
    var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
    var logger = sp.GetRequiredService<ILogger<SqlAppInsightsTokenRepository>>();
    return new SqlAppInsightsTokenRepository(connectionString, logger);
});

builder.Services.AddScoped<IAppInsightsTokenService, AppInsightsTokenService>();
builder.Services.AddScoped<IAppInsightsQueryService, AppInsightsQueryService>();

// Core use cases: no Azure/HTTP/EF dependency, unit-testable in isolation (see AGENTS.md).
builder.Services.AddScoped<GetJourneysService>();
builder.Services.AddScoped<GetClubsService>();
builder.Services.AddScoped<GetClubInventoryService>();
builder.Services.AddScoped<ClubHostResolver>();
builder.Services.AddScoped<SyncJourneysService>();
builder.Services.AddScoped<SyncIncidentsService>();
builder.Services.AddScoped<GetStoredJourneysService>();
builder.Services.AddScoped<GetDbJourneysService>();
builder.Services.AddScoped<GetIncidentsService>();
builder.Services.AddScoped<GetOverviewService>();
builder.Services.AddScoped<SyncDashboardMetricsService>();
builder.Services.AddScoped<GetDashboardMetricsService>();
builder.Services.AddScoped<LatencySpikeSettingsService>();
builder.Services.AddScoped<LlmClientSettingsService>();
builder.Services.AddScoped<SloDefinitionsService>();
builder.Services.AddScoped<AlertChannelSettingsService>();
builder.Services.AddScoped<LatencySpikeMonkeyRunner>();
builder.Services.AddScoped<SloBurnRateMonkeyRunner>();
builder.Services.AddScoped<DoctorMonkeyRunner>();
builder.Services.AddScoped<ScoutMonkeyRunner>();

builder.Services.AddJourneyMonitorInfrastructure(builder.Configuration);
builder.Services.AddJourneyMonitorMonitoring(builder.Configuration);

// Durable journey store (per-vertical hotel/car/cruise/air schemas). Always registered so the
// timer and the stored-reads endpoint can report "not configured" themselves; with no
// JourneyStore:ConnectionString they no-op and the live endpoint is unaffected.
builder.Services.AddSqlJourneyStore(builder.Configuration);

builder.Build().Run();
