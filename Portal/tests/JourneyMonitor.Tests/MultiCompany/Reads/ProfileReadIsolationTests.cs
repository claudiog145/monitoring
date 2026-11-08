using System.Net;
using System.Text.Json.Nodes;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring.Kpi;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Core.UseCases;
using JourneyMonitor.Functions.Functions;
using JourneyMonitor.Infrastructure.Monitoring;
using JourneyMonitor.Tests.Company;
using JourneyMonitor.Tests.Functions;
using JourneyMonitor.Tests.Monitoring;
using JourneyMonitor.Tests.Spec1000.A4;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Tests.MultiCompany.Reads;

/// <summary>
/// Contract v6 §5: every existing read the portal shows is filtered by <c>?profileKey=</c> (missing = the
/// active company). Each test seeds a row for two companies and checks that asking for one never returns
/// the other's.
/// </summary>
public sealed class ProfileReadIsolationTests
{
    private const string Banco = "banco";
    private const string Arrivia = KpiProfiles.Default;
    private const string Secret = A4Http.InternalSecret;
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The active company is banco, so a read without profileKey must show banco's rows only.</summary>
    private static KpiProfileResolver Resolver() => new(
        Options.Create(new ProfileDrivenCompanyOptions { DefaultProfileKey = Arrivia }),
        new FakeActiveStore(Banco),
        new InMemoryCompanyProfileStore(
        [
            CompanyKqlTemplaterTests.Profile() with { ProfileKey = Arrivia },
            CompanyKqlTemplaterTests.Profile() with { ProfileKey = Banco },
        ]));

    private static IOptions<MonitoringOptions> Monitoring() =>
        Options.Create(new MonitoringOptions { InternalApiSecret = Secret, SettingsWriteSecret = A4Http.WriteSecret });

    private static FakeHttpRequestData Get(string pathAndQuery, bool secret = false)
    {
        var req = new FakeHttpRequestData("http://localhost/api/" + pathAndQuery);
        if (secret) req.WithHeader("X-Internal-Api-Secret", Secret);
        return req;
    }

    [Fact]
    public async Task HealthPillars_FilteredByProfile()
    {
        var definitions = new A4FakeKpiDefinitionStore();
        definitions.Definitions.Add(A4Http.Definition(1, KpiPillar.Application));
        definitions.Definitions.Add(A4Http.Definition(2, KpiPillar.Application, domain: "cards") with { ProfileKey = Banco });
        var buckets = new A4FakeKpiBucketStore();
        var anomalies = new A4FakeKpiAnomalyStore();
        var time = new A4FakeTimeProvider(Now);
        var reader = new GetPillarHealthService(definitions, buckets, new A4FakeKpiStateStore(), anomalies, time);
        var health = new HealthReadFunctions(reader, anomalies, buckets, definitions, time, NullLogger<HealthReadFunctions>.Instance, Resolver());

        static int[] KpiIds(JsonNode body) =>
            body.AsArray().SelectMany(p => p!["kpis"]!.AsArray()).Select(k => k!["kpiDefinitionId"]!.GetValue<int>()).ToArray();

        Assert.Equal(new[] { 2 }, KpiIds((await health.GetPillarHealthAsync(Get("health/pillars"), default)).ReadJson()));
        Assert.Equal(new[] { 1 }, KpiIds((await health.GetPillarHealthAsync(Get("health/pillars?profileKey=arrivia"), default)).ReadJson()));

        // KPI buckets: another company's KPI does not exist for this one.
        Assert.Equal(HttpStatusCode.OK, (await health.GetBucketsAsync(Get("health/kpis/2/buckets"), 2, default)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await health.GetBucketsAsync(Get("health/kpis/1/buckets"), 1, default)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await health.GetBucketsAsync(Get("health/kpis/2/buckets?profileKey=arrivia"), 2, default)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await health.GetBucketsAsync(Get("health/kpis/1/buckets?profileKey=arrivia"), 1, default)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await health.GetBucketsAsync(Get("health/kpis/1/buckets?profileKey=*"), 1, default)).StatusCode);
    }

    [Fact]
    public async Task Triage_FilteredByProfile()
    {
        var store = new A4FakeInvestigationStore();
        var triage = new TriageInvestigationsFunction(
            new TriageSignalSubmitter(store, new A4FakeWorkQueue(), new A4FakeTimeProvider(Now), NullLogger<TriageSignalSubmitter>.Instance),
            store,
            Monitoring(),
            NullLogger<TriageInvestigationsFunction>.Instance,
            Resolver());

        const string body = """{"detectedAt":"2026-11-07T11:40:00Z","pillar":"Application Health","kpi":"Latency","detection":"manual check","domain":"hotels"}""";
        Assert.Equal(HttpStatusCode.Accepted, (await triage.SubmitAsync(A4Http.Request("http://localhost/api/triage/investigations", body, internalSecret: Secret), default)).StatusCode);
        var arriviaBody = body.Replace("\"domain\":\"hotels\"", "\"domain\":\"cars\",\"profileKey\":\"arrivia\"", StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Accepted, (await triage.SubmitAsync(A4Http.Request("http://localhost/api/triage/investigations", arriviaBody, internalSecret: Secret), default)).StatusCode);

        var bancoId = store.Items.Values.Single(i => i.Signal.ProfileKey == Banco).InvestigationId;
        var arriviaId = store.Items.Values.Single(i => i.Signal.ProfileKey == Arrivia).InvestigationId;

        var active = (await triage.GetRecentAsync(Get("triage/investigations"), default)).ReadJson().AsArray();
        Assert.Equal([Banco], active.Select(i => i!["profileKey"]!.GetValue<string>()).ToArray());
        var incumbent = (await triage.GetRecentAsync(Get("triage/investigations?profileKey=arrivia"), default)).ReadJson().AsArray();
        Assert.Equal([Arrivia], incumbent.Select(i => i!["profileKey"]!.GetValue<string>()).ToArray());

        Assert.Equal(HttpStatusCode.OK, (await triage.GetByIdAsync(Get($"triage/investigations/{bancoId}"), bancoId, default)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await triage.GetByIdAsync(Get($"triage/investigations/{arriviaId}"), arriviaId, default)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await triage.GetByIdAsync(Get($"triage/investigations/{arriviaId}?profileKey=arrivia"), arriviaId, default)).StatusCode);
    }

    [Fact]
    public async Task Monkeys_FilteredByProfile()
    {
        var world = await SeedTwoCompaniesAsync();
        var findings = new InMemoryFindingsRepository();
        await findings.AddAsync(LatencyFinding());

        var monkeys = new GetMonkeysFunction(
            findings,
            new InMemoryDoctorReportsRepository(),
            new InMemoryNotificationLogRepository(),
            world.Packages,
            world.Runs,
            world.Cases,
            new InMemoryRcaReportRepository(),
            new InMemoryAgentWorkQueue(),
            FakeAgentPauseResolver.NothingPaused(),
            Options.Create(new InspectorOptions()),
            Options.Create(new RcaOptions()),
            new InMemoryMonkeyRunTracker(),
            NullLogger<GetMonkeysFunction>.Instance,
            profileResolver: Resolver());

        static JsonNode Card(JsonNode roster, string id) => roster.AsArray().Single(m => m!["id"]!.GetValue<string>() == id)!;
        static string[] CaseIds(JsonNode card) => card["cases"]!.AsArray().Select(c => c!["caseId"]!.GetValue<string>()).ToArray();

        var active = (await monkeys.RunAsync(Get("monkeys"), default)).ReadJson();
        Assert.Equal([world.BancoCase.CaseId], CaseIds(Card(active, MonkeyKey.InspectorMonkey)));
        Assert.Equal(1, Card(active, ScoutBoard.MonkeyKeyFor(ScoutBoard.Exception))["findings"]!.GetValue<int>());
        // The latency-spike findings come from the incumbent's journey pipeline: banco has none.
        Assert.Equal(0, Card(active, MonkeyKey.LatencySpikeMonkey)["findings"]!.GetValue<int>());

        var incumbent = (await monkeys.RunAsync(Get("monkeys?profileKey=arrivia"), default)).ReadJson();
        Assert.Equal([world.ArriviaCase.CaseId], CaseIds(Card(incumbent, MonkeyKey.InspectorMonkey)));
        Assert.Equal(1, Card(incumbent, MonkeyKey.LatencySpikeMonkey)["findings"]!.GetValue<int>());

        // /api/scout/packages and /api/scout/runs behind the same cards.
        var scouts = new GetScoutPackagesFunction(
            world.Packages, world.Runs, new InMemoryScoutLlmCallLogRepository(), Monitoring(),
            NullLogger<GetScoutPackagesFunction>.Instance, Resolver());
        var packages = (await scouts.GetPackagesAsync(Get("scout/packages"), default)).ReadJson().AsArray();
        Assert.Equal([world.BancoPackage.Id.ToString()], packages.Select(p => p!["id"]!.GetValue<string>()).ToArray());
        var runs = (await scouts.GetRunsAsync(Get("scout/runs?profileKey=arrivia"), default)).ReadJson().AsArray();
        Assert.Equal([world.ArriviaRun.Id.ToString()], runs.Select(r => r!["id"]!.GetValue<string>()).ToArray());
        var foreign = await scouts.GetPackageByIdAsync(Get($"scout/packages/{world.ArriviaPackage.Id}", secret: true), world.ArriviaPackage.Id.ToString(), default);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await scouts.GetPackagesAsync(Get("scout/packages?profileKey=nobody"), default)).StatusCode);
    }

    [Fact]
    public async Task Inspector_FilteredByProfile()
    {
        var world = await SeedTwoCompaniesAsync();
        var queue = new InMemoryAgentWorkQueue();
        foreach (var package in new[] { world.ArriviaPackage, world.BancoPackage })
        {
            await queue.EnqueueAsync(new AgentWorkItem
            {
                SourceMonkey = MonkeyKey.ExceptionScoutMonkey,
                PayloadType = AgentWorkPayloadType.InvestigationPackage,
                PayloadId = package.Id.ToString(),
                DedupKey = package.CaseId,
            });
        }
        var notifications = new InMemoryNotificationLogRepository();
        foreach (var forensicCase in new[] { world.ArriviaCase, world.BancoCase })
        {
            await notifications.AddAsync(new NotificationLogEntry(
                Guid.NewGuid(), NotificationSourceType.ForensicCase, forensicCase.Id, NotificationChannel.Toast,
                null, true, null, DateTimeOffset.UtcNow, forensicCase.CaseId));
        }

        var inspector = new GetInspectorFunction(
            world.Cases,
            new InMemoryAgentTranscriptRepository(),
            queue,
            notifications,
            Options.Create(new InspectorOptions()),
            Monitoring(),
            NullLogger<GetInspectorFunction>.Instance,
            Resolver(),
            world.Packages);

        var cases = (await inspector.GetCasesAsync(Get("inspector/cases"), default)).ReadJson()["cases"]!.AsArray();
        Assert.Equal([world.BancoCase.CaseId], cases.Select(c => c!["caseId"]!.GetValue<string>()).ToArray());
        var incumbentCases = (await inspector.GetCasesAsync(Get("inspector/cases?profileKey=arrivia"), default)).ReadJson()["cases"]!.AsArray();
        Assert.Equal([world.ArriviaCase.CaseId], incumbentCases.Select(c => c!["caseId"]!.GetValue<string>()).ToArray());

        Assert.Equal(HttpStatusCode.NotFound,
            (await inspector.GetCaseByIdAsync(Get($"inspector/cases/{world.ArriviaCase.Id}", secret: true), world.ArriviaCase.Id.ToString(), default)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await inspector.GetCaseByIdAsync(Get($"inspector/cases/{world.BancoCase.Id}", secret: true), world.BancoCase.Id.ToString(), default)).StatusCode);

        var queued = (await inspector.GetQueueAsync(Get("inspector/queue"), default)).ReadJson()["queued"]!.AsArray();
        Assert.Equal([world.BancoPackage.Id.ToString()], queued.Select(q => q!["payloadId"]!.GetValue<string>()).ToArray());
        var allQueued = (await inspector.GetQueueAsync(Get("inspector/queue?profileKey=*"), default)).ReadJson()["queued"]!.AsArray();
        Assert.Equal(2, allQueued.Count);

        var toasts = (await inspector.GetToastsAsync(Get("inspector/toasts?profileKey=arrivia"), default)).ReadJson()["toasts"]!.AsArray();
        Assert.Equal([world.ArriviaCase.Id.ToString()], toasts.Select(t => t!["sourceId"]!.GetValue<string>()).ToArray());

        var reports = new InMemoryRcaReportRepository();
        await reports.SaveAsync(new RcaReport { ForensicCaseId = world.ArriviaCase.Id, Title = "Arrivia RCA" });
        var rca = new GetRcaFunction(reports, Monitoring(), NullLogger<GetRcaFunction>.Instance, world.Cases, Resolver());
        var arriviaRcaId = world.ArriviaCase.Id.ToString();
        Assert.Equal(HttpStatusCode.NotFound,
            (await rca.RunAsync(Get($"inspector/cases/{arriviaRcaId}/rca", secret: true), arriviaRcaId, default)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await rca.RunAsync(Get($"inspector/cases/{arriviaRcaId}/rca?profileKey=arrivia", secret: true), arriviaRcaId, default)).StatusCode);
    }

    [Fact]
    public async Task Knowledge_FilteredByProfile()
    {
        var retriever = new ScopedKnowledgeRetriever();
        await retriever.IndexAsync(Chunk("Arrivia hotel outage RCA", "hotel booking timeout"), default, Arrivia);
        await retriever.IndexAsync(Chunk("Banco card authorization RCA", "card authorization timeout"), default, Banco);
        await retriever.IndexAsync(Chunk("Shared SRE handbook", "timeout handling guidance"), default, null);

        var documents = new GetKnowledgeDocumentsFunction(retriever, Monitoring(), NullLogger<GetKnowledgeDocumentsFunction>.Instance, Resolver());
        var search = new SearchKnowledgeFunction(retriever, Monitoring(), NullLogger<SearchKnowledgeFunction>.Instance, Resolver());

        static string[] Titles(JsonNode body) => body["items"]!.AsArray().Select(i => i!["title"]!.GetValue<string>()).OrderBy(t => t).ToArray();

        Assert.Equal(["Banco card authorization RCA", "Shared SRE handbook"],
            Titles((await documents.RunAsync(Get("knowledge/documents", secret: true), default)).ReadJson()));
        Assert.Equal(new[] { "Arrivia hotel outage RCA", "Shared SRE handbook" },
            Titles((await documents.RunAsync(Get("knowledge/documents?profileKey=arrivia", secret: true), default)).ReadJson()));

        Assert.Equal(HttpStatusCode.NotFound,
            (await documents.RunDocumentAsync(Get("knowledge/document?title=Arrivia%20hotel%20outage%20RCA", secret: true), default)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await documents.RunDocumentAsync(Get("knowledge/document?title=Banco%20card%20authorization%20RCA", secret: true), default)).StatusCode);

        var hits = (await search.RunAsync(Get("knowledge/search?q=timeout&top=10", secret: true), default)).ReadJson()["results"]!.AsArray();
        Assert.Equal(["Banco card authorization RCA", "Shared SRE handbook"], hits.Select(h => h!["document"]!.GetValue<string>()).OrderBy(t => t).ToArray());
    }

    [Fact]
    public async Task DaemonActivity_ProfileAndGlobal()
    {
        var log = new InMemoryDaemonActivityLog();
        await log.AppendAsync(
        [
            Entry("run started", null),
            Entry("arrivia kpi", """{"kpiDefinitionId":1,"profileKey":"arrivia"}"""),
            Entry("banco kpi", """{"kpiDefinitionId":2,"profileKey":"banco"}"""),
            Entry("run completed", """{"kpisProcessed":2}"""),
        ]);
        var daemons = new DaemonFunctions(new EmptyDaemonStatusReader(), log, NullLogger<DaemonFunctions>.Instance, Resolver());

        static string[] Messages(JsonNode page) => page["entries"]!.AsArray().Select(e => e!["message"]!.GetValue<string>()).ToArray();

        Assert.Equal(new[] { "run started", "banco kpi", "run completed" },
            Messages((await daemons.GetDaemonActivityAsync(Get("daemons/activity"), default)).ReadJson()));
        Assert.Equal(new[] { "run started", "arrivia kpi", "run completed" },
            Messages((await daemons.GetDaemonActivityAsync(Get("daemons/activity?profileKey=arrivia"), default)).ReadJson()));
        Assert.Equal(4, Messages((await daemons.GetDaemonActivityAsync(Get("daemons/activity?profileKey=*"), default)).ReadJson()).Length);

        // Polling with afterId keeps the same scope and never skips this company's entries.
        var first = (await daemons.GetDaemonActivityAsync(Get("daemons/activity?top=1"), default)).ReadJson();
        var afterId = first["lastId"]!.GetValue<long>();
        await log.AppendAsync([Entry("arrivia later", """{"profileKey":"arrivia"}"""), Entry("banco later", """{"profileKey":"banco"}""")]);
        var next = (await daemons.GetDaemonActivityAsync(Get($"daemons/activity?afterId={afterId}"), default)).ReadJson();
        Assert.Equal(["banco later"], Messages(next));
    }

    [Fact]
    public async Task ClubInventory_IncumbentOnly()
    {
        var store = new OneClubStore();
        var inventory = new GetClubInventoryFunction(new GetClubInventoryService(store), NullLogger<GetClubInventoryFunction>.Instance, Resolver());

        var active = (await inventory.RunAsync(Get("club-inventory"), default)).ReadJson();
        Assert.False(active["applicable"]!.GetValue<bool>());
        Assert.Empty(active["clubs"]!.AsArray());
        Assert.Equal(Banco, active["profileKey"]!.GetValue<string>());
        Assert.Equal(0, store.Reads);

        var incumbent = (await inventory.RunAsync(Get("club-inventory?profileKey=arrivia"), default)).ReadJson();
        Assert.True(incumbent["applicable"]!.GetValue<bool>());
        Assert.Single(incumbent["clubs"]!.AsArray());

        // Hotels business health is the incumbent's too.
        var source = new CountingHotelSource();
        var businessHealth = new GetHotelBusinessHealthFunction(
            new GetHotelBusinessHealthService(source), Monitoring(), NullLogger<GetHotelBusinessHealthFunction>.Instance, Resolver());
        var other = (await businessHealth.RunAsync(Get("hotels/business-health", secret: true))).ReadJson();
        Assert.False(other["applicable"]!.GetValue<bool>());
        Assert.False(other["available"]!.GetValue<bool>());
        Assert.Equal(0, source.Reads);
        var own = (await businessHealth.RunAsync(Get("hotels/business-health?profileKey=arrivia", secret: true))).ReadJson();
        Assert.Null(own["applicable"]);
    }

    [Fact]
    public async Task Findings_IncumbentOnly()
    {
        var findings = new InMemoryFindingsRepository();
        var finding = await findings.AddAsync(LatencyFinding());
        var function = new GetFindingsFunction(
            findings, new InMemoryDoctorReportsRepository(), new InMemoryLlmCallLogRepository(), Monitoring(),
            NullLogger<GetFindingsFunction>.Instance, Resolver());

        var active = (await function.GetFindingsAsync(Get("findings"), default)).ReadJson();
        Assert.False(active["applicable"]!.GetValue<bool>());
        Assert.Empty(active["findings"]!.AsArray());
        Assert.Equal(HttpStatusCode.NotFound,
            (await function.GetDoctorReportAsync(Get($"findings/{finding.Id}/doctor-report", secret: true), finding.Id, default)).StatusCode);

        var incumbent = (await function.GetFindingsAsync(Get("findings?profileKey=arrivia"), default)).ReadJson();
        Assert.Single(incumbent["findings"]!.AsArray());
    }

    private sealed record TwoCompanies(
        InMemoryForensicCaseRepository Cases,
        InMemoryInvestigationPackageRepository Packages,
        InMemoryScoutRunRepository Runs,
        ForensicCase ArriviaCase,
        ForensicCase BancoCase,
        InvestigationPackage ArriviaPackage,
        InvestigationPackage BancoPackage,
        ScoutRun ArriviaRun);

    private static async Task<TwoCompanies> SeedTwoCompaniesAsync()
    {
        var cases = new InMemoryForensicCaseRepository();
        var packages = new InMemoryInvestigationPackageRepository();
        var runs = new InMemoryScoutRunRepository();

        var arriviaRun = await runs.AddAsync(Run(Arrivia));
        await runs.AddAsync(Run(Banco));
        var arriviaPackage = await packages.AddAsync(Package(Arrivia, "SOR | Hotels | host:stays.example.com | 07 Booking"));
        var bancoPackage = await packages.AddAsync(Package(Banco, "Cards | Authorization | merchant:42 | 02 Authorize"));
        var arriviaCase = await cases.AddAsync(Case(Arrivia, arriviaPackage));
        var bancoCase = await cases.AddAsync(Case(Banco, bancoPackage));
        return new TwoCompanies(cases, packages, runs, arriviaCase, bancoCase, arriviaPackage, bancoPackage, arriviaRun);
    }

    private static Finding LatencyFinding() => new()
    {
        Domain = "hotels",
        FindingType = "latency_spike_cluster",
        Stage = "07 Booking",
        Title = "spike",
        Severity = "low",
        EvidenceJson = "{}",
        OriginMonkeyKey = MonkeyKey.LatencySpikeMonkey,
        CorrelationId = "hotels:07 Booking",
    };

    private static ScoutRun Run(string profileKey) => new()
    {
        Board = ScoutBoard.Exception,
        ProfileKey = profileKey,
        StartedAt = Now.AddMinutes(-10),
        CompletedAt = Now.AddMinutes(-9),
        Status = ScoutRunStatus.Detected,
        WindowFrom = Now.AddHours(-4),
        WindowTo = Now,
    };

    private static InvestigationPackage Package(string profileKey, string caseId) => new()
    {
        CaseId = caseId,
        ProfileKey = profileKey,
        Board = ScoutBoard.Exception,
        Vertical = "Vertical",
        Stage = "Stage",
        Source = "Source",
        EntityKey = "entity:1",
        EntityKind = ClubEntityKind.Host,
        ClubResolvedBy = ClubResolution.Unresolved,
        Priority = "Investigate now",
        PriorityRank = 3,
        Reason = "exceptions",
        WindowFrom = Now.AddHours(-4),
        WindowTo = Now,
        DeterministicJson = "{}",
        AssessmentJson = "{}",
        ScoutRunId = Guid.NewGuid(),
    };

    private static ForensicCase Case(string profileKey, InvestigationPackage package) => new()
    {
        InvestigationPackageId = package.Id,
        ProfileKey = profileKey,
        CaseId = package.CaseId,
        EntityKey = package.EntityKey,
        Vertical = package.Vertical,
        Stage = package.Stage,
        Status = ForensicCaseStatus.Resolved,
        StartedAt = DateTimeOffset.UtcNow.AddMinutes(-3),
        CompletedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
    };

    private static KnowledgeChunk Chunk(string title, string content) => new()
    {
        SourceType = "rca",
        Content = content,
        Embedding = [],
        EmbeddingModel = KnowledgeChunk.NoEmbeddingModel,
        DocumentTitle = title,
        SectionTitle = "Summary",
        SourceRef = title,
    };

    private static DaemonActivityEntry Entry(string message, string? dataJson) =>
        new(0, DaemonKeys.KpiIngestion, Guid.NewGuid(), Now, DaemonActivityLevel.Info, DaemonActivityKinds.IngestionKpi, message, dataJson);

    private sealed class FakeActiveStore(string active) : IActiveCompanyProfileStore
    {
        public Task<string?> GetActiveAsync(CancellationToken ct = default) => Task.FromResult<string?>(active);

        public Task<ActiveCompanyProfile?> GetActiveDetailAsync(CancellationToken ct = default) => throw new NotSupportedException();

        public Task SetActiveAsync(string profileKey, string changedBy, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class EmptyDaemonStatusReader : IDaemonStatusReader
    {
        public Task<IReadOnlyList<DaemonStatus>> GetAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DaemonStatus>>([]);
    }

    private sealed class OneClubStore : IClubInventoryStore
    {
        public int Reads { get; private set; }

        public bool IsConfigured => true;

        public Task<IReadOnlyList<ClubInventoryRow>> GetAllAsync(CancellationToken cancellationToken)
        {
            Reads++;
            return Task.FromResult<IReadOnlyList<ClubInventoryRow>>(
                [new ClubInventoryRow([], "Stays Club", 42, "Stays", null, null, "stays.example.com", "SOR", 10, null, "sql", "production")]);
        }
    }

    private sealed class CountingHotelSource : IHotelBusinessHealthSource
    {
        public int Reads { get; private set; }

        public Task<HotelBusinessHealth?> ReadAsync(string environment, long? vacationClubId, CancellationToken cancellationToken)
        {
            Reads++;
            return Task.FromResult<HotelBusinessHealth?>(null);
        }
    }

    /// <summary>Keeps each chunk's company, like monitoring.KnowledgeChunk: blank is the shared corpus.</summary>
    private sealed class ScopedKnowledgeRetriever : IKnowledgeRetriever
    {
        private readonly List<(KnowledgeChunk Chunk, string ProfileKey)> _chunks = [];

        private IEnumerable<KnowledgeChunk> Visible(string? profileKey) =>
            _chunks.Where(c => profileKey is null || c.ProfileKey.Length == 0 || c.ProfileKey == profileKey).Select(c => c.Chunk);

        public Task IndexAsync(KnowledgeChunk chunk, CancellationToken ct = default, string? profileKey = null)
        {
            _chunks.Add((chunk, profileKey ?? ""));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<KnowledgeChunk>> RetrieveAsync(string query, int topK, CancellationToken ct = default, string? profileKey = null) =>
            Task.FromResult<IReadOnlyList<KnowledgeChunk>>(
                Visible(profileKey).Where(c => c.Content.Contains(query, StringComparison.OrdinalIgnoreCase)).Take(topK).ToList());

        public Task<IReadOnlySet<string>> GetIndexedHashesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());

        public Task<IReadOnlyList<KnowledgeDocumentSummary>> GetIndexedDocumentsAsync(CancellationToken ct = default) =>
            GetIndexedDocumentsForProfileAsync(null, ct);

        public Task<IReadOnlyList<KnowledgeDocumentSummary>> GetIndexedDocumentsForProfileAsync(string? profileKey, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<KnowledgeDocumentSummary>>(Visible(profileKey)
                .Select(c => new KnowledgeDocumentSummary(c.DocumentTitle!, c.SourceType, "", 1, 0, c.CreatedAt))
                .ToList());

        public Task<IReadOnlyList<KnowledgeDocumentSection>> GetDocumentSectionsAsync(string documentTitle, string? sourceType, CancellationToken ct = default) =>
            GetDocumentSectionsForProfileAsync(documentTitle, sourceType, null, ct);

        public Task<IReadOnlyList<KnowledgeDocumentSection>> GetDocumentSectionsForProfileAsync(string documentTitle, string? sourceType, string? profileKey, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<KnowledgeDocumentSection>>(Visible(profileKey)
                .Where(c => c.DocumentTitle == documentTitle)
                .Select(c => new KnowledgeDocumentSection(c.SectionTitle ?? "", c.Content, c.SourceRef ?? ""))
                .ToList());
    }
}
