using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring.Evidence;
using JourneyMonitor.Core.Monitoring.Inspector;
using JourneyMonitor.Core.Monitoring.Kpi;
using JourneyMonitor.Core.UseCases;
using JourneyMonitor.Functions.Functions;
using JourneyMonitor.Infrastructure.AppInsights;
using JourneyMonitor.Infrastructure.DependencyInjection;
using JourneyMonitor.Infrastructure.Fabric;
using JourneyMonitor.Infrastructure.Monitoring;
using JourneyMonitor.Tests.Spec1000.A5;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace JourneyMonitor.Tests.Functions;

/// <summary>
/// Builds the container the way JourneyMonitor.Functions/Program.cs does (no connection string,
/// so every store is in-memory) and checks the whole Specification 1000 graph resolves with scope
/// validation. A missing registration compiles and only fails on the first timer tick otherwise.
/// </summary>
/// <remarks>Serial with the other Inspector runner tests: InspectorMonkeyRunner holds a static run lock and skips an overlapping run.</remarks>
[Collection(A5InspectorRunnerCollection.Name)]
public sealed class Spec1000DependencyInjectionTests
{
    [Fact]
    public void AddSpec1000_ResolvesEverySpec1000Service()
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var sp = scope.ServiceProvider;

        Assert.IsType<KpiEvaluator>(sp.GetRequiredService<IKpiEvaluator>());
        Assert.IsType<PillarHealthMonitorService>(sp.GetRequiredService<IPillarHealthMonitor>());
        Assert.IsType<EntityDetectionMonitor>(sp.GetRequiredService<IEntityDetectionMonitor>());
        Assert.IsType<KpiIngestionService>(sp.GetRequiredService<IKpiIngestionRunner>());
        Assert.IsType<GetPillarHealthService>(sp.GetRequiredService<IPillarHealthReader>());
        Assert.IsType<TriageSignalSubmitter>(sp.GetRequiredService<ITriageSignalSubmitter>());
        Assert.NotNull(sp.GetRequiredService<TriageMonkeyService>());
        Assert.NotNull(sp.GetRequiredService<TimeProvider>());

        var providers = sp.GetServices<IEvidenceProvider>().ToList();
        Assert.Equal(6, providers.Count);
        Assert.Equal(
            TriageDimensions.All.OrderBy(d => d, StringComparer.Ordinal),
            providers.Select(p => p.Dimension).OrderBy(d => d, StringComparer.Ordinal));
        Assert.Contains(providers, p => p is FabricBusinessEvidenceProvider);
        Assert.Contains(providers, p => p is ApplicationEvidenceProvider);
        Assert.Contains(providers, p => p is KnowledgeEvidenceProvider);

        var sources = sp.GetServices<IKpiSource>().ToList();
        Assert.NotEmpty(sources);
        Assert.Contains(sources, s => s is AppInsightsKpiSource);

        // The Functions the worker activates per invocation, including the pre-existing Run Now.
        Assert.NotNull(ActivatorUtilities.CreateInstance<RunMonkeyNowFunction>(sp));
        Assert.NotNull(ActivatorUtilities.CreateInstance<KpiIngestionFunction>(sp));
        Assert.NotNull(ActivatorUtilities.CreateInstance<ApplicationHealthMonitorFunction>(sp));
        Assert.NotNull(ActivatorUtilities.CreateInstance<BusinessHealthMonitorFunction>(sp));
        Assert.NotNull(ActivatorUtilities.CreateInstance<InfrastructureHealthMonitorFunction>(sp));
        Assert.NotNull(ActivatorUtilities.CreateInstance<HealthReadFunctions>(sp));
        Assert.NotNull(ActivatorUtilities.CreateInstance<KpiDefinitionAdminFunctions>(sp));
        Assert.NotNull(ActivatorUtilities.CreateInstance<TriageInvestigationsFunction>(sp));
        // AG8: every company profile save runs the alias ownership check.
        Assert.NotNull(sp.GetRequiredService<TelemetrySourceOwnershipGuard>());
        Assert.NotNull(sp.GetRequiredService<TelemetrySourceOwnerPolicy>());
        Assert.NotNull(ActivatorUtilities.CreateInstance<CompanyProfileAdminFunctions>(sp));
        Assert.NotNull(ActivatorUtilities.CreateInstance<TelemetrySourceAdminFunctions>(sp));
    }

    [Fact]
    public void AddSpec1000_WithoutSql_UsesInMemoryStores()
    {
        using var provider = BuildProvider();

        Assert.IsType<InMemoryKpiDefinitionStore>(provider.GetRequiredService<IKpiDefinitionStore>());
        Assert.IsType<InMemoryKpiBucketStore>(provider.GetRequiredService<IKpiBucketStore>());
        Assert.IsType<InMemoryKpiSampleStore>(provider.GetRequiredService<IKpiSampleStore>());
        Assert.IsType<InMemoryKpiStateStore>(provider.GetRequiredService<IKpiStateStore>());
        Assert.IsType<InMemoryKpiAnomalyStore>(provider.GetRequiredService<IKpiAnomalyStore>());
        Assert.IsType<InMemoryIngestionWatermarkStore>(provider.GetRequiredService<IIngestionWatermarkStore>());
        Assert.IsType<InMemoryTriageInvestigationStore>(provider.GetRequiredService<ITriageInvestigationStore>());
        Assert.IsType<InMemoryAgentWorkQueue>(provider.GetRequiredService<IAgentWorkQueue>());
    }

    [Fact]
    public async Task AddSpec1000_InspectorRunner_ProcessesQueuedTriageSignal()
    {
        // Mode=Off completes the investigation without calling any LLM, so this only proves the
        // runner received TriageMonkeyService: without it the triage item is never leased.
        using var provider = BuildProvider(new Dictionary<string, string?> { ["Triage:Mode"] = "Off" });

        Guid investigationId;
        using (var scope = provider.CreateScope())
        {
            var submission = await scope.ServiceProvider.GetRequiredService<ITriageSignalSubmitter>().SubmitAsync(
                new TriageSignal(DateTimeOffset.UtcNow, "Application Health", "Latency", "manual check", "air", null, null, TriageSignalSources.Manual));
            investigationId = submission.InvestigationId;
        }

        MonkeyRunSummary summary;
        using (var scope = provider.CreateScope())
        {
            summary = await scope.ServiceProvider.GetRequiredService<InspectorMonkeyRunner>().RunAsync();
        }

        Assert.True(summary.NewFindings == 1 && summary.Diagnosed == 1,
            $"The Inspector run should have leased and triaged exactly one signal (got NewFindings={summary.NewFindings}, Diagnosed={summary.Diagnosed}); " +
            "0/0 means the run was skipped (run lock held or paused) or TriageMonkeyService was not injected.");

        var investigation = await provider.GetRequiredService<ITriageInvestigationStore>().GetAsync(investigationId);
        Assert.NotNull(investigation);
        Assert.Equal(TriageInvestigationStatus.Completed, investigation.Status);
        Assert.False(investigation.Notified);
    }

    /// <summary>
    /// AG8: the KQL admin only refuses to delete an in-use kpi: query when AddSpec1000 replaced the
    /// registration AddJourneyMonitorMonitoring made - which depends on the order Program.cs calls
    /// them in. Built with exactly that order, so swapping the calls fails here, not in production.
    /// </summary>
    [Fact]
    public async Task KqlAdmin_InUseRevert_Returns409_WithProductionDiOrder()
    {
        const string queryKey = "kpi:ag8.di-order-v1";
        const string kql = """
            let fromTs = {{from}};
            let toTs = {{to}};
            app('{{SOURCE:cruise-core}}').requests
            | where timestamp >= fromTs and timestamp < toTs
            | summarize TotalCount = sum(itemCount) by BucketStart = bin(timestamp, {{bucket}})
            | project ['kind'] = "bucket", BucketStart, TotalCount, Value = todouble(TotalCount)
            """;

        using var provider = BuildProvider();
        var admin = provider.GetRequiredService<KqlQueryAdminService>();

        Assert.Equal(200, (await admin.SaveAsync(queryKey, kql, "AG8 DI order", null, true, "ag8@example.com")).StatusCode);
        var kpi = await provider.GetRequiredService<IKpiDefinitionStore>().UpsertAsync(
            JourneyMonitor.Tests.Spec1000.A4.A4Http.Definition(0, KpiPillar.Business, enabled: true)
                with { Kpi = "AG8 DI order", SourceKey = "kql:" + queryKey },
            "ag8@example.com");

        var (status, payload) = await admin.RevertAsync(queryKey, "ag8@example.com");

        Assert.Equal(409, status);
        Assert.Equal($"Query is used by KPI(s) {kpi.KpiDefinitionId}; disable them first.",
            (string)payload.GetType().GetProperty("error")!.GetValue(payload)!);
        Assert.NotNull(await provider.GetRequiredService<IKqlQueryStore>().GetAsync(queryKey));
    }

    /// <summary>AG8: the profile admin the host activates really carries the ownership check (an unregistered optional dependency would silently skip it).</summary>
    [Fact]
    public async Task AddSpec1000_CompanyProfileSave_RefusesForeignAlias()
    {
        using var provider = BuildProvider(new Dictionary<string, string?> { ["Monitoring:SettingsWriteSecret"] = "ag8-secret" });
        using var scope = provider.CreateScope();
        var admin = ActivatorUtilities.CreateInstance<CompanyProfileAdminFunctions>(scope.ServiceProvider);
        var profile = new JourneyMonitor.Core.Company.CompanyProfile(
            "contoso", "Contoso",
            new JourneyMonitor.Core.Company.CompanyLabels("Service", "Flow", "Step", "Entity", "Owner"),
            [new JourneyMonitor.Core.Company.ServiceDefinition("checkout", "Checkout", ["club"], [], [])],
            [new JourneyMonitor.Core.Company.EntityIdentitySource(1, JourneyMonitor.Core.Company.EntityIdentitySourceKind.CustomDimension, "TenantId", "tenant")],
            new JourneyMonitor.Core.Company.CompanyGlossary("service", "flow", "step", "entity", "owner", "Sells widgets."));

        var response = await admin.UpsertAsync(
            new JourneyMonitor.Tests.Onboarding.OnboardingHttpRequest("PUT", "https://localhost/api/settings/company-profiles/contoso",
                    System.Text.Json.JsonSerializer.Serialize(profile, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)))
                .WithHeader("X-Settings-Write-Secret", "ag8-secret"),
            "contoso",
            default);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Alias 'club' belongs to profile 'arrivia'.", response.ReadJson()["error"]!.GetValue<string>());
    }

    private static ServiceProvider BuildProvider(IDictionary<string, string?>? settings = null)
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(settings ?? new Dictionary<string, string?>()).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(configuration);
        // Program.cs configures a file-system key ring; the settings stores only need something to resolve.
        services.AddSingleton<IDataProtectionProvider>(new PassThroughDataProtectionProvider());

        services.AddScoped<LatencySpikeMonkeyRunner>();
        services.AddScoped<SloBurnRateMonkeyRunner>();
        services.AddScoped<DoctorMonkeyRunner>();
        services.AddScoped<ScoutMonkeyRunner>();

        services.AddJourneyMonitorInfrastructure(configuration);
        services.AddFabricHotelBookingKpis();
        services.AddJourneyMonitorMonitoring(configuration);
        services.AddSpec1000(configuration);
        services.AddSqlJourneyStore(configuration);

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    private sealed class PassThroughDataProtectionProvider : IDataProtectionProvider, IDataProtector
    {
        public IDataProtector CreateProtector(string purpose) => this;

        public byte[] Protect(byte[] plaintext) => plaintext;

        public byte[] Unprotect(byte[] protectedData) => protectedData;
    }
}
