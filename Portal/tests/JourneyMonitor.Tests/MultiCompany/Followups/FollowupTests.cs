using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring.Daemons;
using JourneyMonitor.Core.Monitoring.Inspector;
using JourneyMonitor.Core.Monitoring.Kpi;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Functions.Functions;
using JourneyMonitor.Infrastructure.Monitoring;
using JourneyMonitor.Tests.Company;
using JourneyMonitor.Tests.Functions;
using JourneyMonitor.Tests.Monitoring;
using JourneyMonitor.Tests.Monitoring.Kpi;
using JourneyMonitor.Tests.Monitoring.Kpi.Fakes;
using JourneyMonitor.Tests.Spec1000.A5;
using JourneyMonitor.Tests.Spec1000.D1;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Tests.MultiCompany.Followups;

/// <summary>
/// MC7 follow-ups of the MC5 review: the daemon console of one company never shows another company's lines,
/// and the Simian Army texts speak the active company's vocabulary instead of the incumbent's.
/// PillarHealthMonitorService holds static per-pillar locks: serial with the A1 monitor tests.
/// </summary>
[Collection(A1MonitorRunLockCollection.Name)]
public sealed class FollowupTests
{
    private const string Banco = "banco";
    private const string MercadoLibre = "mercadolibre";
    private const string Arrivia = KpiProfiles.Default;
    private static readonly DateTimeOffset Day = new(2026, 11, 4, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = Day.AddHours(17).AddMinutes(10);

    /// <summary>Words only the incumbent (Arrivia) uses; none may reach another company's screens.</summary>
    private static readonly string[] IncumbentWords = ["Hotel", "Cruise", "Cars", "club", "Club", "booking", "Booking", "VacationClub", "member", "GMV", "Air "];

    [Fact]
    public async Task DaemonActivity_PerCompanyEntriesTagged()
    {
        // A pillar monitor run over one banco KPI that triggers: evaluation, anomaly and triage hand-off.
        var definitions = new A1InMemoryDefinitionStore();
        var buckets = new A1InMemoryBucketStore();
        var states = new A1InMemoryStateStore();
        var watermarks = new A1InMemoryWatermarkStore();
        definitions.Definitions.Add(Definition(1, Banco, "{\"threshold\":3000}") with { Stage = "book" });
        states.States[1] = new KpiDetectionState(1, 1, Day.AddHours(15), null, Now.AddHours(-1));
        watermarks.Watermarks[1] = Now;
        buckets.Buckets.Add(new KpiBucket(1, Day.AddHours(16), 100, 0, null, 3500, null, null, null));
        var monitorActivity = new D1ActivityHarness(new D1FixedTimeProvider(Now));
        var monitor = new PillarHealthMonitorService(
            definitions, buckets, states, new A1InMemoryAnomalyStore(), watermarks, new KpiEvaluator(), new A1RecordingTriageSubmitter(),
            Options.Create(new HealthMonitoringOptions { Enabled = true, Pillars = ["application"] }), new A1FakeTimeProvider(Now),
            NullLogger<PillarHealthMonitorService>.Instance, monitorActivity.Recorder, monitorActivity.Tracker);

        await monitor.RunAsync(KpiPillar.Application);

        var monitorEntries = await monitorActivity.EntriesAsync(DaemonKeys.ApplicationHealthMonitor);
        var perKpi = monitorEntries.Where(e => e.Kind is not (DaemonActivityKinds.RunStarted or DaemonActivityKinds.RunCompleted)).ToList();
        Assert.Contains(perKpi, e => e.Kind == DaemonActivityKinds.KpiEvaluated);
        Assert.Contains(perKpi, e => e.Kind == DaemonActivityKinds.Anomaly);
        Assert.Contains(perKpi, e => e.Kind == DaemonActivityKinds.TriageSubmitted);
        Assert.All(perKpi, e => Assert.Equal(Banco, ProfileDaemonActivityReader.ProfileKeyOf(e)));
        // The run's own start and end are about the daemon, not about a company: they stay global.
        Assert.All(monitorEntries.Except(perKpi), e => Assert.Null(ProfileDaemonActivityReader.ProfileKeyOf(e)));

        // The evaluation line keeps its own data next to the tag.
        var evaluated = perKpi.First(e => e.Kind == DaemonActivityKinds.KpiEvaluated && e.DataJson!.Contains("outcome", StringComparison.Ordinal));
        using (var data = JsonDocument.Parse(evaluated.DataJson!))
        {
            Assert.Equal("triggered", data.RootElement.GetProperty("outcome").GetString());
            Assert.Equal(1, data.RootElement.GetProperty("kpiDefinitionId").GetInt32());
        }

        // A triage run investigates one company's signal: every line of it, start and end included, is that company's.
        var store = new A5InMemoryTriageStore();
        var investigation = A5TriageFixtures.Queued(signal: A5TriageFixtures.Signal() with { ProfileKey = Banco });
        await store.CreateAsync(investigation);
        var triageActivity = new D1ActivityHarness(new D1FixedTimeProvider(A5TriageFixtures.Now));
        var triage = new TriageMonkeyService(
            store,
            A5TriageFixtures.AllAvailableProviders(),
            A5FakeLlmClient.Returning(A5TriageFixtures.AmberJson),
            new A5PauseResolver(),
            new A5BudgetResolver(),
            new A5RecordingNotificationService(),
            Options.Create(new TriageOptions { Mode = TriageMode.Shadow }),
            new A5FixedTimeProvider(A5TriageFixtures.Now),
            NullLogger<TriageMonkeyService>.Instance,
            triageActivity.Recorder,
            triageActivity.Tracker);

        Assert.NotNull(await triage.InvestigateAsync(investigation.InvestigationId, CancellationToken.None));

        var triageEntries = await triageActivity.EntriesAsync(DaemonKeys.TriageMonkey);
        Assert.Equal(4, triageEntries.Count);
        Assert.All(triageEntries, e => Assert.Equal(Banco, ProfileDaemonActivityReader.ProfileKeyOf(e)));

        // The tag never overrides a key the call site already wrote, and non-object data is kept.
        Assert.Equal("""{"profileKey":"mercadolibre","n":1}""",
            JsonSerializer.Serialize(ProfileTaggedDaemonRun.Tag(new { profileKey = MercadoLibre, n = 1 }, Banco)));
        Assert.Equal("""{"data":[1,2],"profileKey":"banco"}""", JsonSerializer.Serialize(ProfileTaggedDaemonRun.Tag(new[] { 1, 2 }, Banco)));
    }

    [Fact]
    public async Task DaemonActivity_OtherCompanyEntriesHidden()
    {
        var log = new InMemoryDaemonActivityLog();
        await log.AppendAsync(
        [
            Entry("run started", null),
            Entry("banco kpi", """{"kpiDefinitionId":8,"profileKey":"banco"}"""),
            Entry("Transactions screened/screening 05:00: not evaluated", """{"kpiDefinitionId":7,"verdict":"notEvaluated"}"""),
            Entry("Payments/banca-web 05:00: within baseline", """{"kpiDefinitionId":8,"verdict":"ok"}"""),
            Entry("Checkout/legacy line of an unknown KPI", """{"kpiDefinitionId":99}"""),
            Entry("red_alert · checkout latency", """{"investigationId":"ecbe5b44-4144-442d-83b6-309c00b0cf2a","alertLevel":"red_alert"}"""),
            Entry("mercadolibre kpi", """{"kpiDefinitionId":7,"profileKey":"mercadolibre"}"""),
            Entry("run completed", """{"kpisEvaluated":2}"""),
        ]);
        var definitions = new A1InMemoryDefinitionStore();
        definitions.Definitions.Add(Definition(7, MercadoLibre));
        definitions.Definitions.Add(Definition(8, Banco));

        var daemons = new DaemonFunctions(new FixedStatusReader(), log, NullLogger<DaemonFunctions>.Instance, Resolver(), definitions);

        static string[] Messages(JsonNode page) => page["entries"]!.AsArray().Select(e => e!["message"]!.GetValue<string>()).ToArray();

        // Active company (banco): its own lines, the untagged line of its own KPI and the global run lines.
        Assert.Equal(
            ["run started", "banco kpi", "Payments/banca-web 05:00: within baseline", "run completed"],
            Messages((await daemons.GetDaemonActivityAsync(Get("daemons/activity"), default)).ReadJson()));
        Assert.Equal(
            ["run started", "Transactions screened/screening 05:00: not evaluated", "mercadolibre kpi", "run completed"],
            Messages((await daemons.GetDaemonActivityAsync(Get("daemons/activity?profileKey=mercadolibre"), default)).ReadJson()));
        Assert.Equal(["run started", "run completed"],
            Messages((await daemons.GetDaemonActivityAsync(Get("daemons/activity?profileKey=arrivia"), default)).ReadJson()));
        Assert.Equal(8, Messages((await daemons.GetDaemonActivityAsync(Get("daemons/activity?profileKey=*"), default)).ReadJson()).Length);

        // Polling forward keeps the scope.
        var first = (await daemons.GetDaemonActivityAsync(Get("daemons/activity?profileKey=banco"), default)).ReadJson();
        await log.AppendAsync([Entry("mercadolibre later", """{"kpiDefinitionId":7}"""), Entry("banco later", """{"kpiDefinitionId":8}""")]);
        var next = (await daemons.GetDaemonActivityAsync(Get($"daemons/activity?profileKey=banco&afterId={first["lastId"]!.GetValue<long>()}"), default)).ReadJson();
        Assert.Equal(["banco later"], Messages(next));

        // The cards: banco sees its own latest error, a company with none sees a neutral text, "*" the raw one.
        var errors = new InMemoryDaemonActivityLog();
        await errors.AppendAsync(
        [
            Error("Payments/banca-web: fetch failed — timeout", """{"kpiDefinitionId":8,"profileKey":"banco"}"""),
            Error("Transactions screened/screening: fetch failed — watchlist down", """{"kpiDefinitionId":7,"profileKey":"mercadolibre"}"""),
        ]);
        var cards = new DaemonFunctions(new FixedStatusReader(), errors, NullLogger<DaemonFunctions>.Instance, Resolver(), definitions);

        static string? LastError(JsonNode body) => body.AsArray().Single()!["lastErrorMessage"]?.GetValue<string>();

        Assert.Equal("Payments/banca-web: fetch failed — timeout", LastError((await cards.GetDaemonsAsync(Get("daemons"), default)).ReadJson()));
        Assert.Equal(ProfileDaemonStatusScoper.OtherCompanyError, LastError((await cards.GetDaemonsAsync(Get("daemons?profileKey=arrivia"), default)).ReadJson()));
        Assert.Equal("Transactions screened/screening: fetch failed — watchlist down",
            LastError((await cards.GetDaemonsAsync(Get("daemons?profileKey=*"), default)).ReadJson()));
    }

    [Fact]
    public async Task Monkeys_DescriptionsUseProfileVocabulary()
    {
        var bancoProfile = CompanyKqlTemplaterTests.Profile() with
        {
            ProfileKey = Banco,
            DisplayName = "Banco Andino (demo)",
            Labels = new CompanyLabels("Canal", "Operación", "Paso", "Sucursal", "Equipo"),
            Glossary = new CompanyGlossary("canal", "operación", "paso", "sucursal", "equipo"),
        };
        var monkeys = new GetMonkeysFunction(
            new InMemoryFindingsRepository(),
            new InMemoryDoctorReportsRepository(),
            new InMemoryNotificationLogRepository(),
            new InMemoryInvestigationPackageRepository(),
            new InMemoryScoutRunRepository(),
            new InMemoryForensicCaseRepository(),
            new InMemoryRcaReportRepository(),
            new InMemoryAgentWorkQueue(),
            FakeAgentPauseResolver.NothingPaused(),
            Options.Create(new InspectorOptions()),
            Options.Create(new RcaOptions()),
            new InMemoryMonkeyRunTracker(),
            NullLogger<GetMonkeysFunction>.Instance,
            profileResolver: Resolver(bancoProfile),
            profileStore: new InMemoryCompanyProfileStore([bancoProfile]));

        var roster = (await monkeys.RunAsync(Get("monkeys"), default)).ReadJson().AsArray();
        string Purpose(string id) => roster.Single(m => m!["id"]!.GetValue<string>() == id)!["purpose"]!.GetValue<string>();

        Assert.Contains("per-sucursal latency priority board across Checkout and Catalog", Purpose(ScoutBoard.MonkeyKeyFor(ScoutBoard.Latency)));
        Assert.Contains("per-sucursal exception priority board across Checkout and Catalog", Purpose(ScoutBoard.MonkeyKeyFor(ScoutBoard.Exception)));
        Assert.Contains("within a paso", Purpose(MonkeyKey.LatencySpikeMonkey));
        Assert.Contains("every canal/paso", Purpose(MonkeyKey.SloBurnRateMonkey));
        foreach (var purpose in roster.Select(m => m!["purpose"]!.GetValue<string>()))
        {
            Assert.DoesNotContain(IncumbentWords, word => purpose.Contains(word, StringComparison.Ordinal));
        }

        // Without a profile (every company) the texts stay neutral rather than naming anyone's verticals.
        var everyone = (await monkeys.RunAsync(Get("monkeys?profileKey=*"), default)).ReadJson().AsArray();
        Assert.Contains("per-entity latency priority board across every monitored service",
            everyone.Single(m => m!["id"]!.GetValue<string>() == ScoutBoard.MonkeyKeyFor(ScoutBoard.Latency))!["purpose"]!.GetValue<string>());

        // The mocked rest of the roster carries placeholders, never the incumbent's words.
        var mock = File.ReadAllText(Path.Combine(FindPortalDirectory(), "src", "JourneyMonitor.Web", "wwwroot", "data", "monkeys.json"));
        Assert.DoesNotContain(IncumbentWords, word => mock.Contains(word, StringComparison.Ordinal));
        Assert.Contains("{service}", mock, StringComparison.Ordinal);
    }

    private static KpiDefinition Definition(int id, string profileKey, string paramsJson = "{\"threshold\":1}") =>
        KpiEvaluatorTests.Definition(DetectionMethodKind.StaticThreshold, KpiDirection.Increase, paramsJson) with
        {
            KpiDefinitionId = id,
            ProfileKey = profileKey,
        };

    private static KpiProfileResolver Resolver(params CompanyProfile[] extra) => new(
        Options.Create(new ProfileDrivenCompanyOptions { DefaultProfileKey = Arrivia }),
        new FakeActiveStore(Banco),
        new InMemoryCompanyProfileStore(
        [
            CompanyKqlTemplaterTests.Profile() with { ProfileKey = Arrivia },
            .. extra.Length > 0 ? extra : [CompanyKqlTemplaterTests.Profile() with { ProfileKey = Banco }],
            CompanyKqlTemplaterTests.Profile() with { ProfileKey = MercadoLibre },
        ]));

    private static FakeHttpRequestData Get(string pathAndQuery) => new("http://localhost/api/" + pathAndQuery);

    private static DaemonActivityEntry Entry(string message, string? dataJson) =>
        new(0, DaemonKeys.ApplicationHealthMonitor, Guid.NewGuid(), Now, DaemonActivityLevel.Info, DaemonActivityKinds.KpiEvaluated, message, dataJson);

    private static DaemonActivityEntry Error(string message, string dataJson) =>
        new(0, DaemonKeys.ApplicationHealthMonitor, Guid.NewGuid(), Now, DaemonActivityLevel.Error, DaemonActivityKinds.Error, message, dataJson);

    private static string FindPortalDirectory([CallerFilePath] string thisFile = "")
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(thisFile) })
        {
            for (var dir = start is null ? null : new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "JourneyMonitor.slnx"))) return dir.FullName;
                var nested = Path.Combine(dir.FullName, "Portal");
                if (File.Exists(Path.Combine(nested, "JourneyMonitor.slnx"))) return nested;
            }
        }
        throw new InvalidOperationException("Could not locate the Portal source tree (JourneyMonitor.slnx).");
    }

    private sealed class FakeActiveStore(string active) : IActiveCompanyProfileStore
    {
        public Task<string?> GetActiveAsync(CancellationToken ct = default) => Task.FromResult<string?>(active);

        public Task<ActiveCompanyProfile?> GetActiveDetailAsync(CancellationToken ct = default) => throw new NotSupportedException();

        public Task SetActiveAsync(string profileKey, string changedBy, CancellationToken ct = default) => throw new NotSupportedException();
    }

    /// <summary>One card whose raw latest error is mercadolibre's, as the daemon-wide status reader reports it.</summary>
    private sealed class FixedStatusReader : IDaemonStatusReader
    {
        public Task<IReadOnlyList<DaemonStatus>> GetAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DaemonStatus>>(
        [
            new DaemonStatus(
                DaemonKeys.ApplicationHealthMonitor, "Application Health Monitor", "*/5", true, null,
                Now, "2 evaluated, 0 triggered, 0 suppressed, 0 skipped", DaemonActivityLevel.Info,
                Now, "Transactions screened/screening: fetch failed — watchlist down", DaemonHealthStates.Error),
        ]);
    }
}
