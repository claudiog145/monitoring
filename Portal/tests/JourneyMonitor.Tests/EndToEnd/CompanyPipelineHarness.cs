using System.Text.Json;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Infrastructure.Kql;
using JourneyMonitor.Infrastructure.Monitoring;
using JourneyMonitor.Tests.Fakes;
using JourneyMonitor.Tests.Monitoring;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Tests.EndToEnd;

/// <summary>
/// The detection half of the platform, assembled for real and run in process: the real
/// <see cref="ScoutBoardService"/>, the real <see cref="ScoutMonkeyService"/>, the real
/// <see cref="CompanyAdapterProvider"/> over a real <see cref="InMemoryCompanyProfileStore"/>, the
/// real <see cref="ScoutAssessorService"/> and the real <see cref="ClubIdentityResolver"/>. Only
/// the edges are fake - the telemetry backend, the board query text, the drill-down collector and
/// the LLM - and each of those is scripted, so a cycle costs no network, no Docker and no tokens.
///
/// The point of the harness is that ONE set of board rows is handed to SEVERAL companies. Nothing
/// about a run is per-company except which profile key it is asked to run under, so a difference
/// in the output is a difference the profile caused and cannot be anything else.
///
/// Every store is shared across runs on purpose. Dedup is the behaviour under test, and a fresh
/// repository per run would make every dedup assertion pass for the wrong reason.
/// </summary>
internal sealed class CompanyPipelineHarness
{
    private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web);

    private readonly InMemoryCompanyProfileStore _profileStore;
    private readonly CompanyAdapterProvider _adapters;
    private readonly ScriptedBoardQueryTextSource _boardQueryText = new();

    public CompanyPipelineHarness(IEnumerable<CompanyProfile> profiles, IReadOnlyList<TelemetryJourneyRow>? board = null)
    {
        _profileStore = new InMemoryCompanyProfileStore(profiles);
        Telemetry = new ScriptedBoardTelemetry(board ?? CompanyBoards.SharedBoard());

        // No DefaultProfileKey: every run in these tests names the company it is about, and a
        // default would let a test that forgot to name one still quietly pass.
        _adapters = new CompanyAdapterProvider(
            _profileStore,
            Options.Create(new ProfileDrivenCompanyOptions()),
            NullLogger<CompanyAdapterProvider>.Instance);
    }

    public ScriptedBoardTelemetry Telemetry { get; }

    public IInvestigationPackageRepository Packages { get; } = new InMemoryInvestigationPackageRepository();

    public IScoutRunRepository Runs { get; } = new InMemoryScoutRunRepository();

    public InMemoryAgentWorkQueue Queue { get; } = new();

    /// <summary>Which profile each board read was composed for - the seam a profile-driven board query plugs into.</summary>
    public IReadOnlyList<string> BoardQueryProfileKeys => _boardQueryText.ProfileKeys;

    /// <summary>
    /// One Scout cycle for one company over the shared board. Returns the run together with the
    /// packages THAT run produced, so a test can read as a story instead of re-querying and
    /// filtering by hand.
    /// </summary>
    public async Task<ScoutOutcome> RunScoutAsync(string profileKey, string board = ScoutBoard.Latency)
    {
        var run = await BuildScout(profileKey).RunAsync(board);
        var produced = (await AllPackagesAsync())
            .Where(p => p.ScoutRunId == run.Id)
            .OrderBy(p => p.CaseId, StringComparer.Ordinal)
            .ToList();

        return new ScoutOutcome(run, produced);
    }

    public async Task<IReadOnlyList<InvestigationPackage>> AllPackagesAsync() =>
        (await Packages.GetRecentAsync(500)).OrderBy(p => p.CaseId, StringComparer.Ordinal).ToList();

    /// <summary>
    /// Everything the store holds for one company. Filtered HERE rather than by the repository
    /// because <see cref="IInvestigationPackageRepository"/> has no profile-scoped read - see the
    /// note on the separation test that uses this.
    /// </summary>
    public async Task<IReadOnlyList<InvestigationPackage>> PackagesUnderAsync(string profileKey) =>
        (await AllPackagesAsync())
            .Where(p => string.Equals(p.ProfileKey, profileKey, StringComparison.OrdinalIgnoreCase))
            .ToList();

    /// <summary>
    /// What the company profile said about the row this package was built from, read back out of
    /// the deterministic half as the production record type rather than as loose JSON - a reader
    /// that invented its own shape here could not notice a field going missing.
    /// </summary>
    public static ScoutRowClassification ClassificationOf(InvestigationPackage package)
    {
        using var document = JsonDocument.Parse(package.DeterministicJson);
        var element = document.RootElement.GetProperty("companyClassification");
        return JsonSerializer.Deserialize<ScoutRowClassification>(element.GetRawText(), PayloadJson)!;
    }

    private ScoutMonkeyService BuildScout(string profileKey)
    {
        var options = new StubScoutOptionsMonitor(new ScoutOptions { CompanyProfileKey = profileKey });

        var boardReader = new ScoutBoardService(
            Telemetry,
            new KqlQueryTextProvider(),
            new FakeKqlSourceResolver(),
            _adapters,
            Options.Create(new InsightsOptions { AppInsightsAppId = "app-id" }),
            options,
            _boardQueryText);

        return new ScoutMonkeyService(
            boardReader,
            _adapters,
            // The real resolver over a catalogue that is not configured, which is the honest state
            // for a company that has no such catalogue at all: it echoes the entity key back and
            // reports the identity as unresolved instead of inventing a brand.
            new ClubIdentityResolver(new UnconfiguredClubInventoryStore(), NullLogger<ClubIdentityResolver>.Instance),
            new ScoutAssessorService(
                new FakeLlmClient(ScriptedAssessment), NullLogger<ScoutAssessorService>.Instance, _adapters),
            new ScriptedEvidenceCollector(),
            Packages,
            Queue,
            Runs,
            new InMemoryScoutLlmCallLogRepository(),
            options,
            NullLogger<ScoutMonkeyService>.Instance);
    }

    /// <summary>
    /// Deliberately says nothing a company profile would: the assessment must not be where a
    /// vocabulary assertion can accidentally get its answer from.
    /// </summary>
    private const string ScriptedAssessment = """
        {"symptom":"a burst of slow requests on one path","interpretation":"every slow request the board kept is on the same path",
         "confidence":0.6,"shouldEscalate":true,"whyNotEscalate":null}
        """;

    private sealed class StubScoutOptionsMonitor(ScoutOptions options) : IOptionsMonitor<ScoutOptions>
    {
        public ScoutOptions CurrentValue => options;

        public ScoutOptions Get(string? name) => options;

        public IDisposable? OnChange(Action<ScoutOptions, string?> listener) => null;
    }

    private sealed class ScriptedBoardQueryTextSource : IScoutBoardQueryTextSource
    {
        private readonly List<string> _profileKeys = [];

        public IReadOnlyList<string> ProfileKeys => _profileKeys;

        public Task<string> GetBoardQueryTextAsync(ScoutBoardQueryTextRequest request, CancellationToken ct = default)
        {
            _profileKeys.Add(request.ProfileKey);
            // No placeholders and no company words: the rows come from the scripted telemetry, so
            // the text only has to prove WHICH profile the board was composed for.
            return Task.FromResult("// board query text, composed elsewhere");
        }
    }

    private sealed class ScriptedEvidenceCollector : IScoutEvidenceCollector
    {
        public Task<ScoutRawEvidence> CollectAsync(ScoutBoardRow row, CancellationToken ct = default) =>
            Task.FromResult(new ScoutRawEvidence("// drill-down query", RowCount: 2, Truncated: false, RowsJson: "[]", Error: null));
    }

    private sealed class UnconfiguredClubInventoryStore : IClubInventoryStore
    {
        public bool IsConfigured => false;

        public Task<IReadOnlyList<ClubInventoryRow>> GetAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ClubInventoryRow>>([]);
    }
}

/// <summary>One cycle and what it handed on. Packages are this run's own, not the store's whole contents.</summary>
internal sealed record ScoutOutcome(ScoutRun Run, IReadOnlyList<InvestigationPackage> Packages);

/// <summary>
/// A scripted Application Insights. Returns the SAME rows however many times it is asked, which is
/// what lets two companies be handed identical bytes, and can be told to fail so that "a board
/// that cannot run" is distinguishable from "a board with nothing on it".
/// </summary>
internal sealed class ScriptedBoardTelemetry(IReadOnlyList<TelemetryJourneyRow> rows) : ITelemetrySource
{
    public int Queries { get; private set; }

    public Exception? Failure { get; set; }

    public Task<IReadOnlyList<TelemetryJourneyRow>> QueryAsync(TelemetryQueryRequest request, CancellationToken cancellationToken)
    {
        Queries++;
        return Failure is null
            ? Task.FromResult(rows)
            : Task.FromException<IReadOnlyList<TelemetryJourneyRow>>(Failure);
    }
}

/// <summary>
/// Two companies that share no vocabulary at all - a freight carrier and a streaming service - and
/// one board whose rows both of them have an opinion about.
///
/// The profiles are the only place a product word appears. Everything else in these files is
/// platform machinery, which is the shape the whole decoupling is aiming at: if a company word had
/// to be written anywhere outside <see cref="CompanyProfile"/> to make this test pass, the seam
/// would be decorative.
/// </summary>
internal static class CompanyProfiles
{
    public const string FreightKey = "drayline";
    public const string StreamingKey = "kestrel";

    /// <summary>A line-haul carrier. Nothing it calls anything is a word the streaming profile uses.</summary>
    public static CompanyProfile Freight(string profileKey = FreightKey, bool enabled = true) => new(
        ProfileKey: profileKey,
        DisplayName: "Dray Line Freight",
        Labels: new CompanyLabels("Lane", "Route", "Checkpoint", "Shipper", "Ops crew"),
        Services:
        [
            new ServiceDefinition("linehaul", "Linehaul", [], [],
            [
                new FlowDefinition("transit", "Transit",
                [
                    new StepDefinition("trip-opened", "Trip opened", 1,
                        [new StepMatcher(StepMatcherKind.UrlContains, SharedPath)]),
                ]),
            ]),
            new ServiceDefinition("settlement", "Settlement", [], [],
            [
                new FlowDefinition("billing", "Billing",
                [
                    new StepDefinition("invoice-raised", "Invoice raised", 2,
                        [new StepMatcher(StepMatcherKind.UrlContains, FreightOnlyPath)]),
                ]),
            ]),
        ],
        // One stamped dimension and nothing else: this carrier would rather report an unknown
        // shipper than attribute a delay to the wrong one.
        EntityIdentitySources:
        [
            new EntityIdentitySource(1, EntityIdentitySourceKind.CustomDimension, ShipperDimension, "shipper"),
        ],
        Glossary: new CompanyGlossary("lane", "route", "checkpoint", "shipper", "ops crew",
            "Dray Line Freight moves palletised cargo between regional depots."),
        Enabled: enabled);

    /// <summary>A video service. Same urls, not one word in common with the carrier.</summary>
    public static CompanyProfile Streaming(string profileKey = StreamingKey, bool enabled = true) => new(
        ProfileKey: profileKey,
        DisplayName: "Kestrel Stream",
        Labels: new CompanyLabels("Channel", "Session", "Beat", "Subscriber", "Duty rota"),
        Services:
        [
            new ServiceDefinition("playback", "Playback", [], [],
            [
                new FlowDefinition("watch", "Watch",
                [
                    new StepDefinition("stream-opened", "Stream opened", 1,
                        [new StepMatcher(StepMatcherKind.UrlContains, SharedPath)]),
                ]),
            ]),
            new ServiceDefinition("catalogue", "Catalogue", [], [],
            [
                new FlowDefinition("browse", "Browse",
                [
                    new StepDefinition("listing-served", "Listing served", 2,
                        [new StepMatcher(StepMatcherKind.UrlContains, StreamingOnlyPath)]),
                ]),
            ]),
        ],
        EntityIdentitySources:
        [
            new EntityIdentitySource(1, EntityIdentitySourceKind.CustomDimension, SubscriberDimension, "subscriber"),
        ],
        Glossary: new CompanyGlossary("channel", "session", "beat", "subscriber", "duty rota",
            "Kestrel Stream delivers on-demand video to subscriber devices."),
        Enabled: enabled);

    /// <summary>
    /// A profile whose matchers describe traffic this board does not carry. Every row still has to
    /// come out the far end, classified as unresolved - a profile that maps nothing must not look
    /// like a company with no traffic.
    /// </summary>
    public static CompanyProfile MatchesNothing(string profileKey = "unmapped") => new(
        ProfileKey: profileKey,
        DisplayName: "Not yet mapped",
        Labels: new CompanyLabels("Lane", "Route", "Checkpoint", "Shipper", "Ops crew"),
        Services:
        [
            new ServiceDefinition("depot", "Depot", [], [],
            [
                new FlowDefinition("intake", "Intake",
                [
                    new StepDefinition("gate", "Gate", 1,
                        [new StepMatcher(StepMatcherKind.UrlContains, "/nothing-on-this-board")]),
                ]),
            ]),
        ],
        EntityIdentitySources: [],
        Glossary: new CompanyGlossary("lane", "route", "checkpoint", "shipper", "ops crew"));

    public const string SharedPath = "/v2/session/start";
    public const string FreightOnlyPath = "/v2/ledger/settle";
    public const string StreamingOnlyPath = "/v2/media/listing";

    public const string ShipperDimension = "ShipperId";
    public const string SubscriberDimension = "SubscriberId";

    public const string ShipperValue = "shp-204";
    public const string SubscriberValue = "sub-9f2";

    public static readonly string ShipperEntityKey = $"shipper:{ShipperValue}";
    public static readonly string SubscriberEntityKey = $"subscriber:{SubscriberValue}";
}

/// <summary>
/// The board, built once. These are real-shaped rows: a GroupPayload carrying the dedup key and
/// the window, the projected counters, the paths the board kept, and the board's own guess at the
/// entity - which both profiles deliberately ignore in favour of their own identity sources.
/// </summary>
internal static class CompanyBoards
{
    public const string BoardHost = "edge.example.test";

    public const string Source = "edge";

    /// <summary>Both companies classify this one, and they classify it differently.</summary>
    public const string SharedCaseId = $"{Source} | host:{BoardHost} | {CompanyProfiles.SharedPath} | 2026-10-07T15:00:00Z";

    /// <summary>Only the carrier has a rule for this path.</summary>
    public const string FreightOnlyCaseId = $"{Source} | host:{BoardHost} | {CompanyProfiles.FreightOnlyPath} | 2026-10-07T15:00:00Z";

    /// <summary>Only the video service has a rule for this path.</summary>
    public const string StreamingOnlyCaseId = $"{Source} | host:{BoardHost} | {CompanyProfiles.StreamingOnlyPath} | 2026-10-07T15:00:00Z";

    /// <summary>The step resolves for both, but the request carried none of either profile's identity dimensions.</summary>
    public const string AnonymousCaseId = $"{Source} | host:{BoardHost} | {CompanyProfiles.SharedPath} | 2026-10-07T15:05:00Z";

    /// <summary>Ordinally sorted so a test can compare against it without restating the sort order.</summary>
    public static readonly string[] AllCaseIds =
        new[] { AnonymousCaseId, FreightOnlyCaseId, SharedCaseId, StreamingOnlyCaseId }
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// A fresh list every call, because <see cref="TelemetryJourneyRow"/> is handed to the reader
    /// as-is and a shared instance would let one test's parse affect the next one's.
    /// </summary>
    public static IReadOnlyList<TelemetryJourneyRow> SharedBoard() =>
    [
        Row(SharedCaseId, CompanyProfiles.SharedPath, requests: 940, identified: true),
        Row(FreightOnlyCaseId, CompanyProfiles.FreightOnlyPath, requests: 310, identified: true),
        Row(StreamingOnlyCaseId, CompanyProfiles.StreamingOnlyPath, requests: 275, identified: true),
        Row(AnonymousCaseId, CompanyProfiles.SharedPath, requests: 64, identified: false, bucket: "2026-10-07T15:05:00Z"),
    ];

    /// <param name="identified">
    /// Whether the request carried the dimensions the two profiles are configured to read. False is
    /// not an edge case - plenty of real traffic is anonymous - and it has to come out the far end
    /// saying so.
    /// </param>
    private static TelemetryJourneyRow Row(
        string caseId,
        string path,
        int requests,
        bool identified,
        int rank = 3,
        string bucket = "2026-10-07T15:00:00Z")
    {
        var identity = identified
            ? $"""
               "{CompanyProfiles.ShipperDimension}":"{CompanyProfiles.ShipperValue}",
                "{CompanyProfiles.SubscriberDimension}":"{CompanyProfiles.SubscriberValue}",
               """
            : "";

        // Vertical and JourneyStep carry what the board text believed, and neither profile agrees
        // with it: if an assertion ever matched one of these, the vocabulary would be coming from
        // the query and not from the company.
        var payload = $$"""
            {"CaseId":"{{caseId}}","GroupKey":"{{GroupKeyOf(caseId)}}",
             "Vertical":"[from the board text]","Stage":"[from the board text]",
             "Entity":"host:{{BoardHost}}","Source":"{{Source}}",
             "Start":"{{bucket}}","End":"2026-10-07T15:10:00Z",
             "Priority":" Investigate now","Reason":"P95 >=60s with >=20 requests",
             "Ops":["op-1"],"Users":[],"Correlations":["corr-1"],
             "Paths":["{{path}}"],"SlowPaths":["{{path}}"],"Operations":["POST {{path}}"]}
            """;

        var json = $$"""
            {"Priority":" Investigate now","PriorityRank":{{rank}},
             "Requests":{{requests}},"FailedRequests":9,"Exceptions":4,"UniqueUserIds":3,
             {{identity}}
             "Vertical":"[from the board text]","ClubOrHost":"host:{{BoardHost}}",
             "JourneyStep":"[from the board text]","Source":"{{Source}}",
             "GroupPayload":{{payload}}}
            """;

        return JsonSerializer.Deserialize<TelemetryJourneyRow>(json)!;
    }

    /// <summary>Same rule the board itself uses: the group key is the case id without its bucket.</summary>
    private static string GroupKeyOf(string caseId) => caseId[..caseId.LastIndexOf('|')].TrimEnd();
}
