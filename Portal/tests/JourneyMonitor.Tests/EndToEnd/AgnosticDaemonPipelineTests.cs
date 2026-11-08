using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Configuration;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring.Evidence;
using JourneyMonitor.Core.Monitoring.Ingestion;
using JourneyMonitor.Core.Monitoring.Inspector;
using JourneyMonitor.Core.Monitoring.Kpi;
using JourneyMonitor.Core.Monitoring.Privacy;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Core.UseCases;
using JourneyMonitor.Functions.Functions;
using JourneyMonitor.Infrastructure.AppInsights;
using JourneyMonitor.Infrastructure.Fabric;
using JourneyMonitor.Infrastructure.Monitoring;
using JourneyMonitor.Tests.Functions;
using JourneyMonitor.Tests.Monitoring.Kpi.Fakes;
using JourneyMonitor.Tests.Spec1000.A2;
using JourneyMonitor.Tests.Spec1000.A4;
using JourneyMonitor.Tests.Spec1000.A5;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Tests.EndToEnd;

/// <summary>
/// AGE2E (contract v4 §5, isolation guarantee): a second company, <c>acme</c>, run through the
/// daemons end to end in process next to the incumbent - KPI ingestion from a scripted telemetry
/// backend, the Application and Business pillar monitors, entity and partner detection, the Triage
/// signal and the Triage investigation (business evidence from acme's own KPIs, redacted prompt) -
/// and then every store and HTTP read checked in both directions for data of the other company.
///
/// Real services and real in-memory stores; only the telemetry backend, the LLM and the incumbent's
/// Fabric reader are fake. The acme profile uses no incumbent vocabulary, and its telemetry sources
/// are its own catalog rows (acme-checkout, acme-screening), never the incumbent's.
/// </summary>
[Collection(A1MonitorRunLockCollection.Name)]
public sealed class AgnosticDaemonPipelineTests
{
    private const string Acme = "acme";
    private const string Arrivia = KpiProfiles.Default;
    private const string AlertsQueryKey = "kpi:acme-alerts";
    private const string CustomerRef = "CR-55120";
    private const string CustomerEmail = "pat.lee@example.test";

    /// <summary>
    /// Entries (non-comment, non-blank lines) of coupling-allowlist.txt at tag <c>spec1000-contracts-v4</c>,
    /// the start of the AG phase (<c>git show spec1000-contracts-v4:Portal/tests/JourneyMonitor.Tests/Company/coupling-allowlist.txt</c>).
    /// The AG phase may only shrink this inventory.
    /// </summary>
    private const int AllowListEntriesAtPhaseStart = 87;

    /// <summary>Physical lines of the same file at the same tag (header comment included).</summary>
    private const int AllowListLinesAtPhaseStart = 146;

    private static readonly DateTimeOffset Now = new(2026, 11, 4, 17, 10, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset LastBucket = Now.AddMinutes(-5);

    [Fact]
    public async Task Acme_KpiIngestedDetectedAndTriaged()
    {
        var world = new World();
        await world.SeedAsync();

        await world.RunDaemonsAsync();

        // Ingestion: every acme definition got buckets, from acme's own apps only.
        foreach (var definition in world.AcmeDefinitions)
        {
            var buckets = await world.Buckets.GetRangeAsync(definition.KpiDefinitionId, Now.AddHours(-2), Now, definition.Grain == KpiGrain.Entity ? null : "");
            var all = definition.Grain == KpiGrain.Entity
                ? await world.Buckets.GetEntityRangeAsync(definition.KpiDefinitionId, Now.AddHours(-2), Now)
                : buckets;
            Assert.True(all.Count > 0, $"{definition.Kpi}/{definition.Domain} [{definition.Grain}] has no buckets.");
            Assert.Equal(Now, await world.Watermarks.GetAsync(definition.KpiDefinitionId));
        }
        Assert.All(world.Telemetry.Requests.Where(r => r.Kql.Contains("acme", StringComparison.Ordinal)),
            r => Assert.StartsWith("acme-", r.AppInsightsAppId, StringComparison.Ordinal));
        Assert.Contains(world.Telemetry.Requests, r => r.Kql.Contains(World.QueryMarker, StringComparison.Ordinal) && r.AppInsightsAppId == "acme-checkout-app");
        Assert.Contains(world.Telemetry.Requests, r => r.AppInsightsAppId == "acme-screening-app");
        Assert.DoesNotContain(world.Telemetry.Requests, r => r.AppInsightsAppId == "acme-checkout-app" && r.Kql.Contains("web-front", StringComparison.Ordinal));

        // Detection: the injected spike on the last closed bucket is an anomaly of each acme series.
        var acmeAnomalies = await world.AnomaliesAsync(Acme);
        var domainAnomaly = Assert.Single(acmeAnomalies, a => a.KpiDefinitionId == world.AcmeDomainLatency.KpiDefinitionId);
        Assert.Equal(LastBucket, domainAnomaly.BucketStart);
        Assert.Equal("", domainAnomaly.EntityKey);
        Assert.NotNull(domainAnomaly.InvestigationId);
        var entityAnomaly = Assert.Single(acmeAnomalies, a => a.KpiDefinitionId == world.AcmeEntityLatency.KpiDefinitionId);
        Assert.Equal("store:s-2", entityAnomaly.EntityKey);
        Assert.Null(entityAnomaly.InvestigationId);
        var partnerAnomaly = Assert.Single(acmeAnomalies, a => a.KpiDefinitionId == world.AcmePartner.KpiDefinitionId);
        Assert.Equal("watchlists", partnerAnomaly.EntityKey);
        Assert.NotNull(partnerAnomaly.InvestigationId);
        Assert.DoesNotContain(acmeAnomalies, a => a.KpiDefinitionId == world.AcmeAlerts.KpiDefinitionId);

        // Triage: one investigation per Triage-worthy acme anomaly, all under ProfileKey acme.
        var acmeInvestigations = await world.Investigations.GetRecentAsync(50, Acme);
        Assert.Equal(2, acmeInvestigations.Count);
        Assert.All(acmeInvestigations, i => Assert.Equal(Acme, i.Signal.ProfileKey));
        var latencyInvestigation = Assert.Single(acmeInvestigations, i => i.Signal.AnomalyId == domainAnomaly.AnomalyId);
        Assert.Equal("Application Health", latencyInvestigation.Signal.Pillar);
        Assert.Equal("checkout", latencyInvestigation.Signal.Domain);
        Assert.Equal(TriageSignalSources.Daemon, latencyInvestigation.Signal.Source);
        var partnerInvestigation = Assert.Single(acmeInvestigations, i => i.Signal.AnomalyId == partnerAnomaly.AnomalyId);
        Assert.Equal("partner:watchlists", partnerInvestigation.Signal.Stage);
        Assert.Equal("screening", partnerInvestigation.Signal.Domain);
        var queued = await world.Queue.GetQueuedAsync(AgentWorkPayloadType.TriageSignal, 50);
        Assert.All(acmeInvestigations, i => Assert.Contains(queued, q => q.PayloadId == i.InvestigationId.ToString()));

        var result = await world.Triage().InvestigateAsync(latencyInvestigation.InvestigationId, CancellationToken.None);

        Assert.NotNull(result);
        var completed = await world.Investigations.GetAsync(latencyInvestigation.InvestigationId);
        Assert.Equal(TriageInvestigationStatus.Completed, completed!.Status);

        // Business evidence (AG5) comes from acme's kql: Business KPI, not from the incumbent's Fabric view.
        var business = Assert.Single(world.Sections, s => s.Dimension == TriageDimensions.BusinessHealth);
        Assert.True(business.Available, business.UnavailableReason);
        var data = business.Data!.Value;
        Assert.Equal(KpiBusinessEvidenceProvider.SourceName, data.GetProperty("source").GetString());
        Assert.Equal(Acme, data.GetProperty("profileKey").GetString());
        var kpi = Assert.Single(data.GetProperty("kpis").EnumerateArray());
        Assert.Equal(world.AcmeAlerts.KpiDefinitionId, kpi.GetProperty("kpiDefinitionId").GetInt32());
        Assert.True(kpi.GetProperty("buckets").GetArrayLength() > 0);
        Assert.Equal(0, world.Fabric.Calls);

        var application = Assert.Single(world.Sections, s => s.Dimension == TriageDimensions.ApplicationHealth);
        Assert.True(application.Available, application.UnavailableReason);
        Assert.Null(world.Incidents.LastQuery);

        // The prompt (AG6): acme is closed by default, so the raw telemetry's PII never reaches the model.
        var prompt = Assert.Single(world.Llm.Prompts);
        Assert.Contains("checkout", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(CustomerEmail, prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(CustomerRef, prompt, StringComparison.Ordinal);
        Assert.Contains("[REDACTED:email]", prompt, StringComparison.Ordinal);
        Assert.Contains("[REDACTED:pii]", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(World.ArriviaSpikeText, prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("hotels", prompt, StringComparison.OrdinalIgnoreCase);

        // The persisted correlation follows the same rule (AG6B).
        var persisted = JsonSerializer.Serialize(completed.Result);
        Assert.DoesNotContain(CustomerEmail, persisted, StringComparison.Ordinal);
        Assert.DoesNotContain(CustomerRef, persisted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Acme_AndArrivia_AreIsolated()
    {
        var world = new World();
        await world.SeedAsync();

        await world.RunDaemonsAsync();

        var acmeIds = world.AcmeDefinitions.Select(d => d.KpiDefinitionId).ToHashSet();
        var arriviaIds = new HashSet<int> { world.ArriviaLatency.KpiDefinitionId };

        // The daemons processed both profiles in the same runs.
        Assert.NotEmpty(await world.Buckets.GetRangeAsync(world.ArriviaLatency.KpiDefinitionId, Now.AddHours(-2), Now));
        Assert.Contains(world.Telemetry.Requests, r => r.AppInsightsAppId == "hotel-app");

        // Stores, both directions.
        var acmeDefinitions = await world.Definitions.GetAllAsync(Acme);
        var arriviaDefinitions = await world.Definitions.GetAllAsync(Arrivia);
        Assert.Equal(acmeIds.Order(), acmeDefinitions.Select(d => d.KpiDefinitionId).Order());
        Assert.Equal(arriviaIds.Order(), arriviaDefinitions.Select(d => d.KpiDefinitionId).Order());

        var acmeAnomalies = await world.AnomaliesAsync(Acme);
        var arriviaAnomalies = await world.AnomaliesAsync(Arrivia);
        Assert.NotEmpty(acmeAnomalies);
        Assert.NotEmpty(arriviaAnomalies);
        Assert.All(acmeAnomalies, a => Assert.Contains(a.KpiDefinitionId, acmeIds));
        Assert.All(arriviaAnomalies, a => Assert.Contains(a.KpiDefinitionId, arriviaIds));

        var acmeInvestigations = await world.Investigations.GetRecentAsync(50, Acme);
        var arriviaInvestigations = await world.Investigations.GetRecentAsync(50, Arrivia);
        Assert.NotEmpty(acmeInvestigations);
        var arriviaInvestigation = Assert.Single(arriviaInvestigations);
        Assert.Equal("hotels", arriviaInvestigation.Signal.Domain);
        Assert.Empty(acmeInvestigations.Select(i => i.InvestigationId).Intersect(arriviaInvestigations.Select(i => i.InvestigationId)));

        foreach (var id in acmeIds)
        {
            var rows = await world.Buckets.GetEntityRangeAsync(id, Now.AddHours(-2), Now);
            Assert.All(rows, b => Assert.Equal(id, b.KpiDefinitionId));
            Assert.DoesNotContain(rows, b => b.P95 == World.ArriviaSpikeMs);
        }

        // HTTP reads, both directions.
        var acmeSettings = await world.ReadAsync(f => f.Admin.GetAllAsync(Get("settings/kpis?profileKey=acme"), CancellationToken.None));
        var arriviaSettings = await world.ReadAsync(f => f.Admin.GetAllAsync(Get("settings/kpis?profileKey=arrivia"), CancellationToken.None));
        Assert.Equal(acmeIds.Order(), Ids(acmeSettings).Order());
        Assert.Equal(arriviaIds.Order(), Ids(arriviaSettings).Order());
        Assert.All(acmeSettings.AsArray(), d => Assert.Equal(Acme, d!["profileKey"]!.GetValue<string>()));
        Assert.All(arriviaSettings.AsArray(), d => Assert.Equal(Arrivia, d!["profileKey"]!.GetValue<string>()));

        var acmeAnomaliesApi = await world.ReadAsync(f => f.Health.GetAnomaliesAsync(Get("health/anomalies?profileKey=acme"), CancellationToken.None));
        var arriviaAnomaliesApi = await world.ReadAsync(f => f.Health.GetAnomaliesAsync(Get("health/anomalies?profileKey=arrivia"), CancellationToken.None));
        Assert.NotEmpty(acmeAnomaliesApi.AsArray());
        Assert.NotEmpty(arriviaAnomaliesApi.AsArray());
        Assert.All(acmeAnomaliesApi.AsArray(), a => Assert.Contains(a!["kpiDefinitionId"]!.GetValue<int>(), acmeIds));
        Assert.All(arriviaAnomaliesApi.AsArray(), a => Assert.Contains(a!["kpiDefinitionId"]!.GetValue<int>(), arriviaIds));

        var acmePillars = await world.ReadAsync(f => f.Health.GetPillarHealthAsync(Get("health/pillars?domain=checkout&profileKey=acme"), CancellationToken.None));
        var acmePillarKpis = acmePillars.AsArray().SelectMany(p => p!["kpis"]!.AsArray()).Select(k => k!["kpiDefinitionId"]!.GetValue<int>()).ToList();
        Assert.NotEmpty(acmePillarKpis);
        Assert.All(acmePillarKpis, id => Assert.Contains(id, acmeIds));
        Assert.All(acmePillars.AsArray(), p => Assert.Equal(Acme, p!["profileKey"]!.GetValue<string>()));
        var application = Assert.Single(acmePillars.AsArray(), p => p!["pillar"]!.ToString() is "Application" or "application" or "1");
        Assert.Equal(GetPillarHealthService.StatusAnomalous, application!["status"]!.GetValue<string>());

        // The incumbent's view of its own domain is untouched by acme's spike, and the reverse.
        var arriviaPillars = await world.ReadAsync(f => f.Health.GetPillarHealthAsync(Get("health/pillars?domain=hotels&profileKey=arrivia"), CancellationToken.None));
        var arriviaPillarKpis = arriviaPillars.AsArray().SelectMany(p => p!["kpis"]!.AsArray()).Select(k => k!["kpiDefinitionId"]!.GetValue<int>()).ToList();
        Assert.Equal([world.ArriviaLatency.KpiDefinitionId], arriviaPillarKpis);
        var acmeOnIncumbentDomain = await world.ReadAsync(f => f.Health.GetPillarHealthAsync(Get("health/pillars?domain=hotels&profileKey=acme"), CancellationToken.None));
        Assert.Empty(acmeOnIncumbentDomain.AsArray().SelectMany(p => p!["kpis"]!.AsArray()));

        var acmeTriage = await world.ReadAsync(f => f.Triage.GetRecentAsync(Get("triage/investigations?profileKey=acme"), CancellationToken.None));
        var arriviaTriage = await world.ReadAsync(f => f.Triage.GetRecentAsync(Get("triage/investigations?profileKey=arrivia"), CancellationToken.None));
        Assert.Equal(acmeInvestigations.Count, acmeTriage.AsArray().Count);
        Assert.Single(arriviaTriage.AsArray());
        Assert.DoesNotContain("hotels", acmeTriage.ToJsonString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("checkout", arriviaTriage.ToJsonString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("screening", arriviaTriage.ToJsonString(), StringComparison.OrdinalIgnoreCase);

        // The default read (no profileKey) follows the active profile, which stays the incumbent.
        var defaultSettings = await world.ReadAsync(f => f.Admin.GetAllAsync(Get("settings/kpis"), CancellationToken.None));
        Assert.Equal(arriviaIds.Order(), Ids(defaultSettings).Order());

        // A Triage run for acme reads only acme's KPIs; one for the incumbent never sees acme's.
        var acmeLatency = Assert.Single(acmeInvestigations, i => i.Signal.AnomalyId is not null && i.Signal.Stage is null);
        await world.Triage().InvestigateAsync(acmeLatency.InvestigationId, CancellationToken.None);
        await world.Triage().InvestigateAsync(arriviaInvestigation.InvestigationId, CancellationToken.None);
        Assert.Equal(2, world.Llm.Prompts.Count);
        Assert.DoesNotContain(World.ArriviaSpikeText, world.Llm.Prompts[0], StringComparison.Ordinal);
        Assert.DoesNotContain("hotels", world.Llm.Prompts[0], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("checkout", world.Llm.Prompts[1], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(World.AcmeSpikeText, world.Llm.Prompts[1], StringComparison.Ordinal);
        Assert.DoesNotContain(world.BucketReads, read => read.Investigation == 0 && arriviaIds.Contains(read.KpiDefinitionId));
        Assert.DoesNotContain(world.BucketReads, read => read.Investigation == 1 && acmeIds.Contains(read.KpiDefinitionId));
    }

    [Fact]
    public void CouplingAllowlist_DoesNotGrow()
    {
        var path = AllowListPath();
        var lines = File.ReadAllLines(path);
        var entries = lines.Count(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith('#'));

        Assert.True(entries <= AllowListEntriesAtPhaseStart,
            $"coupling-allowlist.txt has {entries} entries; the AG phase started with {AllowListEntriesAtPhaseStart} and may only shrink it.");
        Assert.True(lines.Length <= AllowListLinesAtPhaseStart,
            $"coupling-allowlist.txt has {lines.Length} lines; the AG phase started with {AllowListLinesAtPhaseStart}.");
        Assert.True(entries > 0, "The allow-list was read empty: the guardrail would pass for the wrong reason.");
    }

    // =========================================================================================

    private static FakeHttpRequestData Get(string pathAndQuery) => A4Http.Request("http://localhost/api/" + pathAndQuery);

    private static IEnumerable<int> Ids(JsonNode array) => array.AsArray().Select(d => d!["kpiDefinitionId"]!.GetValue<int>());

    private static string AllowListPath([CallerFilePath] string thisFile = "")
    {
        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(thisFile))
        {
            candidates.Add(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "Company", "coupling-allowlist.txt")));
        }
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            candidates.Add(Path.Combine(dir.FullName, "Portal", "tests", "JourneyMonitor.Tests", "Company", "coupling-allowlist.txt"));
            candidates.Add(Path.Combine(dir.FullName, "tests", "JourneyMonitor.Tests", "Company", "coupling-allowlist.txt"));
        }
        return candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("coupling-allowlist.txt not found. Looked in: " + string.Join(", ", candidates.Take(4)));
    }

    /// <summary>The whole composition for one test: profiles, catalog, stores, daemons, Triage and the HTTP reads.</summary>
    private sealed class World
    {
        public const string QueryMarker = "acme-alerts business count";
        public const double ArriviaSpikeMs = 7777;
        public const string ArriviaSpikeText = "7777";
        public const string AcmeSpikeText = "4800";

        private readonly A1FakeTimeProvider _time = new(Now);
        private readonly InMemoryCompanyProfileStore _profiles = new([AcmeProfile(), KpiSourceDoubles.Incumbent(Arrivia)]);
        private readonly InMemoryKqlQueryStore _queries = new();
        private readonly HealthMonitoringOptions _monitoring = new() { Enabled = true, Pillars = ["Application", "Business"] };
        private int _investigationIndex = -1;

        public World()
        {
            Anomalies = new InMemoryKpiAnomalyStore(Definitions);
            Buckets = new RecordingBuckets(new InMemoryKpiBucketStore(_time), this);
        }

        public InMemoryKpiDefinitionStore Definitions { get; } = new();
        public RecordingBuckets Buckets { get; }
        public InMemoryKpiSampleStore Samples { get; } = new();
        public InMemoryKpiStateStore States { get; } = new();
        public InMemoryKpiAnomalyStore Anomalies { get; }
        public InMemoryIngestionWatermarkStore Watermarks { get; } = new();
        public InMemoryTriageInvestigationStore Investigations { get; } = new(new A1FakeTimeProvider(Now));
        public InMemoryAgentWorkQueue Queue { get; } = new();
        public RoutingTelemetry Telemetry { get; } = new();
        public A5FakeLlmClient Llm { get; } = A5FakeLlmClient.Returning(A5TriageFixtures.AmberJson);
        public A5HotelKpiSource Fabric { get; } = new();
        public A5IncidentReader Incidents { get; } = new();
        public List<EvidenceSection> Sections { get; } = [];
        public CollectingLogger<KpiIngestionService> IngestionLog { get; } = new();
        public List<(int Investigation, int KpiDefinitionId)> BucketReads { get; } = [];
        public int CurrentInvestigation => _investigationIndex;

        public KpiDefinition AcmeDomainLatency { get; private set; } = null!;
        public KpiDefinition AcmeEntityLatency { get; private set; } = null!;
        public KpiDefinition AcmePartner { get; private set; } = null!;
        public KpiDefinition AcmeAlerts { get; private set; } = null!;
        public KpiDefinition ArriviaLatency { get; private set; } = null!;
        public IReadOnlyList<KpiDefinition> AcmeDefinitions => [AcmeDomainLatency, AcmeEntityLatency, AcmePartner, AcmeAlerts];

        /// <summary>
        /// A screening company. No incumbent word anywhere: its services, partner, entity and
        /// telemetry aliases are its own, and Privacy is left out on purpose (closed by default).
        /// </summary>
        public static CompanyProfile AcmeProfile() => new(
            Acme,
            "Acme Screening",
            new CompanyLabels("Service", "Flow", "Step", "Store", "Team"),
            [
                new ServiceDefinition("checkout", "Checkout", ["acme-checkout"], [],
                [
                    new FlowDefinition("purchase", "Purchase",
                    [
                        new StepDefinition("cart", "Cart", 1, [new StepMatcher(StepMatcherKind.PathContains, "/cart")]),
                        new StepDefinition("pay", "Pay", 2, [new StepMatcher(StepMatcherKind.PathContains, "/pay")]),
                    ]),
                ]),
                new ServiceDefinition("screening", "Screening", ["acme-screening"], [],
                [
                    new FlowDefinition("review", "Review",
                    [
                        new StepDefinition("lookup", "Lookup", 1, [new StepMatcher(StepMatcherKind.PathContains, "/lookup")]),
                    ]),
                ]),
            ],
            [new EntityIdentitySource(1, EntityIdentitySourceKind.CustomDimension, "StoreId", "store")],
            new CompanyGlossary("service", "flow", "step", "store", "team", "Acme screens shoppers at checkout."),
            Partners:
            [
                new PartnerDefinition("watchlists", "Watchlists", [new PartnerMatcher(PartnerMatcherKind.DependencyTargetContains, "watchlists.example.test")]),
            ],
            Privacy: new PrivacySettings(PiiDimensions: ["CustomerRef"]));

        /// <summary>Every anomaly of a profile: the domain aggregate ("") and each entity series the scripted backend emits.</summary>
        public async Task<List<KpiAnomaly>> AnomaliesAsync(string profileKey)
        {
            var all = new List<KpiAnomaly>();
            foreach (var entityKey in new[] { "", "store:s-1", "store:s-2", "watchlists" })
            {
                all.AddRange(await Anomalies.GetRecentAsync(null, null, 100, profileKey, entityKey));
            }
            return all;
        }

        public async Task SeedAsync()
        {
            Assert.False(CompanyProfileValidator.HasErrors(CompanyProfileValidator.Validate(AcmeProfile())));

            await _queries.UpsertAsync(new KqlQueryEntry(AlertsQueryKey, AlertsQueryText, Description: "acme alerts per bucket"), "age2e");

            ArriviaLatency = await Definitions.UpsertAsync(
                Definition(KpiPillar.Application, "Latency", "hotels", AppInsightsKpiSource.LatencySourceKey, KpiDirection.Increase, KpiAggregation.P95, 3000) with { ProfileKey = Arrivia },
                "age2e");
            AcmeDomainLatency = await Definitions.UpsertAsync(
                Definition(KpiPillar.Application, "Latency", "checkout", AppInsightsKpiSource.LatencySourceKey, KpiDirection.Increase, KpiAggregation.P95, 2000),
                "age2e");
            AcmeEntityLatency = await Definitions.UpsertAsync(
                Definition(KpiPillar.Application, "Latency", "checkout", AppInsightsKpiSource.LatencySourceKey, KpiDirection.Increase, KpiAggregation.P95, 2000) with { Grain = KpiGrain.Entity, SampleSize = 0 },
                "age2e");
            AcmePartner = await Definitions.UpsertAsync(
                Definition(KpiPillar.Application, "PartnerFailures", "screening", PartnerKpi.SourceKey, KpiDirection.Increase, KpiAggregation.Value, 20) with { Grain = KpiGrain.Entity, SampleSize = 0 },
                "age2e");
            AcmeAlerts = await Definitions.UpsertAsync(
                Definition(KpiPillar.Business, "Alerts", "checkout", KqlKpiContract.SourceKeyPrefix + AlertsQueryKey, KpiDirection.Decrease, KpiAggregation.Count, 10) with { SampleSize = 0 },
                "age2e");
        }

        public async Task RunDaemonsAsync()
        {
            var ingestion = await UntilNotOverlappingAsync(() => Ingestion().RunAsync(), r => r.KpisProcessed + r.Skipped > 0);
            Assert.True(ingestion.KpisProcessed == 5 && ingestion.Skipped == 0,
                $"ingestion {ingestion}: " + string.Join(" | ", IngestionLog.Messages));

            var application = await UntilNotOverlappingAsync(() => PillarMonitor().RunAsync(KpiPillar.Application), r => r.KpisEvaluated + r.Skipped > 0);
            Assert.Equal(2, application.KpisEvaluated);
            Assert.Equal(2, application.Triggered);
            var business = await UntilNotOverlappingAsync(() => PillarMonitor().RunAsync(KpiPillar.Business), r => r.KpisEvaluated + r.Skipped > 0);
            Assert.Equal(1, business.KpisEvaluated);
            Assert.Equal(0, business.Triggered);
            var entities = await UntilNotOverlappingAsync(() => EntityMonitor().RunAsync(), r => r.Definitions > 0);
            Assert.Equal(2, entities.Triggered);
        }

        public TriageMonkeyService Triage()
        {
            _investigationIndex++;
            var kpiBusiness = new KpiBusinessEvidenceProvider(Definitions, Buckets, Anomalies, NullLogger<KpiBusinessEvidenceProvider>.Instance);
            IEvidenceProvider[] providers =
            [
                new Recording(new FabricBusinessEvidenceProvider(Fabric, NullLogger<FabricBusinessEvidenceProvider>.Instance, kpiBusiness), Sections),
                new Recording(new ApplicationEvidenceProvider(Definitions, Buckets, Incidents, NullLogger<ApplicationEvidenceProvider>.Instance), Sections),
                new Recording(new RawTelemetryEvidence(), Sections),
            ];
            return new TriageMonkeyService(
                Investigations,
                providers,
                Llm,
                new A5PauseResolver(),
                new A5BudgetResolver(),
                new A5RecordingNotificationService(),
                Options.Create(new TriageOptions { Mode = TriageMode.Shadow, EvidenceWindowMinutes = 180 }),
                _time,
                NullLogger<TriageMonkeyService>.Instance,
                profiles: _profiles,
                redactor: new EvidenceRedactor());
        }

        public async Task<JsonNode> ReadAsync(Func<Api, Task<Microsoft.Azure.Functions.Worker.Http.HttpResponseData>> call)
        {
            var response = await call(new Api(this));
            Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
            return response.ReadJson();
        }

        public sealed class Api(World world)
        {
            private readonly KpiProfileResolver _resolver = new(
                Options.Create(new ProfileDrivenCompanyOptions { DefaultProfileKey = Arrivia }),
                new IncumbentActive(),
                world._profiles);

            private static IOptions<MonitoringOptions> Monitoring() =>
                Options.Create(new MonitoringOptions { InternalApiSecret = A4Http.InternalSecret, SettingsWriteSecret = A4Http.WriteSecret });

            public KpiDefinitionAdminFunctions Admin => new(
                world.Definitions, Monitoring(), world._time, NullLogger<KpiDefinitionAdminFunctions>.Instance, _resolver, world._profiles);

            public HealthReadFunctions Health => new(
                new GetPillarHealthService(world.Definitions, world.Buckets, world.States, world.Anomalies, world._time),
                world.Anomalies, world.Buckets, world.Definitions, world._time, NullLogger<HealthReadFunctions>.Instance, _resolver);

            public TriageInvestigationsFunction Triage => new(
                world.Submitter(), world.Investigations, Monitoring(), NullLogger<TriageInvestigationsFunction>.Instance, _resolver);
        }

        private TriageSignalSubmitter Submitter() => new(Investigations, Queue, _time, NullLogger<TriageSignalSubmitter>.Instance);

        private KpiIngestionService Ingestion()
        {
            var catalog = new KpiSourceDoubles.CatalogStore(
            [
                new("web-front", InsightsEnvironment.Production, TelemetrySourceKind.AppInsights, "/res/web-front", AppId: "hotel-app"),
                new("acme-checkout", InsightsEnvironment.Production, TelemetrySourceKind.AppInsights, "/res/acme-checkout", AppId: "acme-checkout-app", TenantId: "acme-tenant"),
                new("acme-screening", InsightsEnvironment.Production, TelemetrySourceKind.AppInsights, "/res/acme-screening", AppId: "acme-screening-app", TenantId: "acme-tenant"),
            ]);
            var resolver = new KpiSourceDoubles.AliasResolver();
            var appInsights = new AppInsightsKpiSource(
                Telemetry,
                Options.Create(new InsightsOptions()),
                Options.Create(new JourneySyncOptions()),
                _profiles,
                resolver,
                catalog);
            var kql = new CatalogKqlKpiSource(Telemetry, _queries, _profiles, resolver, Options.Create(new JourneySyncOptions()), appInsights);

            return new KpiIngestionService(
                Definitions, [appInsights, kql], Buckets, Samples, Watermarks, Options.Create(_monitoring), _time,
                IngestionLog, Options.Create(new KpiIngestionOptions { LagMinutes = 0 }),
                companyProfiles: _profiles);
        }

        private PillarHealthMonitorService PillarMonitor() => new(
            Definitions, Buckets, States, Anomalies, Watermarks, new KpiEvaluator(), Submitter(),
            Options.Create(_monitoring), _time, NullLogger<PillarHealthMonitorService>.Instance, companyProfiles: _profiles);

        private EntityDetectionMonitor EntityMonitor() => new(
            Definitions, Buckets, States, Anomalies, Watermarks, new KpiEvaluator(),
            Options.Create(_monitoring), _time, NullLogger<EntityDetectionMonitor>.Instance, null, Submitter());

        /// <summary>
        /// The ingestion and monitor services guard against overlapping runs with a static lock; a run
        /// that found it taken (another test class in flight) did nothing, so it is simply retried.
        /// </summary>
        private static async Task<T> UntilNotOverlappingAsync<T>(Func<Task<T>> run, Func<T, bool> ran)
        {
            for (var attempt = 0; ; attempt++)
            {
                var result = await run();
                if (ran(result) || attempt >= 200) return result;
                await Task.Delay(50);
            }
        }

        private static KpiDefinition Definition(
            KpiPillar pillar, string kpi, string domain, string sourceKey, KpiDirection direction, KpiAggregation aggregation, double threshold) =>
            new(0, pillar, kpi, domain, "", sourceKey, direction, aggregation,
                TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5), TimeSpan.FromHours(1),
                120, 0, DetectionMethodKind.StaticThreshold, $$"""{"threshold":{{threshold}}}""", 1, 60, true, Now, "age2e")
            {
                ProfileKey = Acme,
            };

        /// <summary>Only allowed tokens, and telemetry reached solely through acme's own alias.</summary>
        private const string AlertsQueryText = """
            // acme-alerts business count per bucket
            app('{{SOURCE:acme-checkout}}').customEvents
            | where timestamp >= {{from}} and timestamp < {{to}}
            | where name == "alert"
            | summarize TotalCount = count() by BucketStart = bin_at(timestamp, {{bucket}}, datetime(1970-01-01))
            | project ['kind'] = "bucket", BucketStart, TotalCount = tolong(TotalCount), Value = todouble(TotalCount)
            """;

        private sealed class IncumbentActive : IActiveCompanyProfileStore
        {
            public Task<string?> GetActiveAsync(CancellationToken ct = default) => Task.FromResult<string?>(Arrivia);

            public Task<ActiveCompanyProfile?> GetActiveDetailAsync(CancellationToken ct = default) => throw new NotSupportedException();

            public Task SetActiveAsync(string profileKey, string changedBy, CancellationToken ct = default) => throw new NotSupportedException();
        }

        /// <summary>Records every section the Triage run collected, so the test reads the evidence and not only the prompt.</summary>
        private sealed class Recording(IEvidenceProvider inner, List<EvidenceSection> sink) : IEvidenceProvider
        {
            public string Dimension => inner.Dimension;

            public async Task<EvidenceSection> CollectAsync(TriageSignal signal, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken ct = default)
            {
                var section = await inner.CollectAsync(signal, windowStart, windowEnd, ct);
                sink.Add(section);
                return section;
            }
        }

        /// <summary>Raw telemetry as an exception provider would hand it over: it carries a shopper's PII.</summary>
        private sealed class RawTelemetryEvidence : IEvidenceProvider
        {
            public string Dimension => TriageDimensions.InfrastructureHealth;

            public Task<EvidenceSection> CollectAsync(TriageSignal signal, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken ct = default)
            {
                var json = JsonSerializer.SerializeToElement(new
                {
                    exception = $"Timeout on {signal.Domain} for {CustomerEmail}",
                    customDimensions = new Dictionary<string, string> { ["CustomerRef"] = CustomerRef, ["Step"] = "pay" },
                });
                return Task.FromResult(new EvidenceSection(Dimension, true, json, null));
            }
        }

        /// <summary>Keeps warnings and errors so a failed daemon assertion says why.</summary>
        public sealed class CollectingLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
        {
            public List<string> Messages { get; } = [];

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => logLevel >= Microsoft.Extensions.Logging.LogLevel.Warning;

            public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel)) Messages.Add(formatter(state, exception) + (exception is null ? "" : " " + exception.Message));
            }
        }

        /// <summary>Bucket store that remembers which KPI was read during which Triage investigation.</summary>
        public sealed class RecordingBuckets(InMemoryKpiBucketStore inner, World world) : IKpiBucketStore
        {
            public Task UpsertAsync(IReadOnlyList<KpiBucket> buckets, CancellationToken ct = default) => inner.UpsertAsync(buckets, ct);

            public Task<IReadOnlyList<KpiBucket>> GetRangeAsync(int kpiDefinitionId, DateTimeOffset fromInclusive, DateTimeOffset toExclusive, CancellationToken ct = default)
            {
                world.BucketReads.Add((world.CurrentInvestigation, kpiDefinitionId));
                return inner.GetRangeAsync(kpiDefinitionId, fromInclusive, toExclusive, ct);
            }

            public Task<IReadOnlyList<KpiBucket>> GetRangeAsync(int kpiDefinitionId, DateTimeOffset fromInclusive, DateTimeOffset toExclusive, string? entityKey, CancellationToken ct = default)
            {
                world.BucketReads.Add((world.CurrentInvestigation, kpiDefinitionId));
                return inner.GetRangeAsync(kpiDefinitionId, fromInclusive, toExclusive, entityKey, ct);
            }

            public Task<IReadOnlyList<KpiBucket>> GetEntityRangeAsync(int kpiDefinitionId, DateTimeOffset fromInclusive, DateTimeOffset toExclusive, CancellationToken ct = default)
            {
                world.BucketReads.Add((world.CurrentInvestigation, kpiDefinitionId));
                return inner.GetEntityRangeAsync(kpiDefinitionId, fromInclusive, toExclusive, ct);
            }
        }
    }

    /// <summary>
    /// One scripted Application Insights for every app. It answers by what was asked: the acme-alerts
    /// catalog query, the partner dependency query, an entity-grain or a domain-grain latency query -
    /// per app, so the incumbent's app and acme's apps return different, recognisable numbers. The
    /// last closed bucket carries the injected anomaly.
    /// </summary>
    private sealed class RoutingTelemetry : ITelemetrySource
    {
        public List<TelemetryQueryRequest> Requests { get; } = [];

        private static IEnumerable<DateTimeOffset> BucketStarts()
        {
            for (var t = Now.AddHours(-2); t < Now; t = t.AddMinutes(5)) yield return t;
        }

        private static string At(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);

        public Task<IReadOnlyList<TelemetryJourneyRow>> QueryAsync(TelemetryQueryRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var code = KpiSourceDoubles.Code(request.Kql);
            var rows = new List<string>();

            if (request.Kql.Contains(World.QueryMarker, StringComparison.Ordinal))
            {
                foreach (var t in BucketStarts())
                    rows.Add($$"""{"kind":"bucket","BucketStart":"{{At(t)}}","TotalCount":100,"Value":100}""");
            }
            else if (code.Contains("EntityKey = Partner", StringComparison.Ordinal))
            {
                foreach (var t in BucketStarts())
                {
                    var failed = t == LastBucket ? 60 : 1;
                    rows.Add($$"""{"kind":"bucket","BucketStart":"{{At(t)}}","EntityKey":"watchlists","TotalCount":100,"Value":{{failed}},"P50":80,"P95":200,"Mean":90,"FailedCount":{{failed}}}""");
                }
            }
            else if (KpiSourceDoubles.FinalStatement(request.Kql) == "entityRows")
            {
                foreach (var t in BucketStarts())
                {
                    foreach (var store in new[] { "store:s-1", "store:s-2" })
                    {
                        var p95 = t == LastBucket && store == "store:s-2" ? 6100 : 400;
                        rows.Add($$"""{"kind":"bucket","BucketStart":"{{At(t)}}","EntityKey":"{{store}}","TotalCount":30,"Value":{{p95}},"P50":200,"P95":{{p95}},"Mean":250,"FailedCount":0}""");
                    }
                }
            }
            else
            {
                var incumbent = request.AppInsightsAppId == "hotel-app";
                foreach (var t in BucketStarts())
                {
                    rows.Add($$"""{"kind":"bucket","BucketStart":"{{At(t)}}","TotalCount":20,"Value":null}""");
                    var value = t == LastBucket ? (incumbent ? World.ArriviaSpikeMs : 4800) : (incumbent ? 250 : 300);
                    for (var i = 0; i < 5; i++)
                        rows.Add($$"""{"kind":"sample","BucketStart":"{{At(t)}}","Timestamp":"{{At(t.AddSeconds(i))}}","Value":{{value + i}},"OperationId":"op-{{i}}"}""");
                }
            }

            return Task.FromResult<IReadOnlyList<TelemetryJourneyRow>>(rows.Select(KpiSourceDoubles.Row).ToList());
        }
    }
}
