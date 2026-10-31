using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Infrastructure.Monitoring;
using Microsoft.Extensions.Logging.Abstractions;

namespace JourneyMonitor.Tests.Monitoring;

/// <summary>
/// Hand-written doubles for the Inspector Monkey tests (this repo uses plain xUnit - no mocking
/// framework). Shared across the orchestrator, planner and verdict tests so a change to a contract
/// breaks one file instead of four.
/// </summary>
internal static class InspectorFixtures
{
    public static readonly DateTimeOffset WindowFrom = DateTimeOffset.Parse("2026-10-07T15:00:00Z");
    public static readonly DateTimeOffset WindowTo = DateTimeOffset.Parse("2026-10-07T15:05:00Z");

    public static InvestigationPackage Package(
        string caseId = "SOR | Hotels | host:stays.example.com | 07 Booking | 2026-10-07T15:00:00Z",
        string stage = "07 Booking",
        string? deterministicJson = null) => new()
    {
        CaseId = caseId,
        ProfileKey = "fixture-company",
        Board = ScoutBoard.Exception,
        Vertical = "Hotels",
        Stage = stage,
        Source = "SOR",
        EntityKey = "host:stays.example.com",
        EntityKind = ClubEntityKind.Host,
        ClubResolvedBy = ClubResolution.Unresolved,
        Priority = "Investigate now",
        PriorityRank = 3,
        Reason = "At least 100 exceptions and >=3x the preceding interval",
        WindowFrom = WindowFrom,
        WindowTo = WindowTo,
        DeterministicJson = deterministicJson ?? """
            {"caseId":"c","groupPayload":{"Ops":["op-aaa","op-bbb"],"Paths":["/booking/confirm"]},"boardRow":{"ExceptionTypes":["System.TimeoutException"],"Stage":"07 Booking"}}
            """,
        AssessmentJson = """{"available":true,"symptom":"bookings failing","interpretation":"vendor timeouts","confidence":0.4}""",
        ExceptionCount = 1683,
        ScoutRunId = Guid.NewGuid(),
    };

    public static ForensicCase Case(InvestigationPackage package) => new()
    {
        InvestigationPackageId = package.Id,
        CaseId = package.CaseId,
        EntityKey = package.EntityKey,
        Vertical = package.Vertical,
        Stage = package.Stage,
    };

    public static ClubIdentity Club(InvestigationPackage package) =>
        new(package.EntityKey, package.EntityKind, null, null, null, null, ClubResolution.Unresolved);

    /// <summary>
    /// The real resolver over an optional store. With no store the JourneyStore options are left
    /// unconfigured, which is the path that falls back to InspectorOptions - so the tests exercise
    /// the production precedence rule rather than a stub of it.
    /// </summary>
    public static IAgentBudgetResolver BudgetResolver(InspectorOptions options, IAgentBudgetStore? store = null) =>
        new AgentBudgetResolver(
            store ?? new InMemoryAgentBudgetStore(),
            Microsoft.Extensions.Options.Options.Create(new JourneyStoreOptions
            {
                ConnectionString = store is null ? null : "Server=test;Database=test;",
            }),
            Microsoft.Extensions.Options.Options.Create(options),
            NullLogger<AgentBudgetResolver>.Instance);

    /// <summary>
    /// Off by default: the Inspector's own tests are about the investigation, and an RCA firing in
    /// the middle of them would make every assertion depend on a second agent. Tests that are
    /// about the hand-off pass their own options with AutoComposeOnEscalation enabled.
    /// </summary>
    public static RcaAutoComposer RcaComposer(
        IRcaAgent? agent = null,
        IRcaReportRepository? reports = null,
        IAgentPauseResolver? pauses = null,
        RcaOptions? options = null) =>
        new(agent ?? new CountingRcaAgent(),
            reports ?? new InMemoryRcaReportRepository(),
            pauses ?? FakeAgentPauseResolver.NothingPaused(),
            Microsoft.Extensions.Options.Options.Create(options ?? new RcaOptions { AutoComposeOnEscalation = false }),
            NullLogger<RcaAutoComposer>.Instance);

    /// <summary>
    /// A resolver over an owner store that can be seeded. Empty by default, which is the honest
    /// state of the real ownership map until the named responders are agreed.
    /// </summary>
    public static IServiceOwnerResolver OwnerResolver(params ServiceOwner[] owners)
    {
        var store = new InMemoryServiceOwnerStore();
        foreach (var owner in owners) store.UpsertAsync(owner, "test").GetAwaiter().GetResult();
        return new ServiceOwnerResolver(
            store,
            // Configured on purpose: an unconfigured store short-circuits to an empty map before it
            // is ever read, which would make a seeded fixture silently resolve to nothing.
            Microsoft.Extensions.Options.Options.Create(new JourneyStoreOptions { ConnectionString = "test" }),
            NullLogger<ServiceOwnerResolver>.Instance);
    }

    public static InspectorContext Context(InvestigationPackage package, params BlackboardEntry[] blackboard) =>
        new(Case(package), package, Club(package), blackboard);

    public static LlmCompletion Answer(string model, string content) =>
        new(content, [], "stop", TokensIn: 100, TokensOut: 20, model, Error: null);

    /// <summary>A synthesis that passes grounding and the resolve bar, used wherever the test is about something else.</summary>
    public const string GoodSynthesis = """
        {"conclusion":"root_cause","summary":"vendor timeouts on /booking/confirm","rootCause":"vendor","confidence":0.9,
         "citations":[{"kind":"operation-id","value":"op-aaa","citedBy":"LogsAgent"}],
         "conflicts":[],"agreementAssessment":"independent","unknowns":[],"blockedBy":null,"runbook":[]}
        """;

    /// <summary>
    /// Names a root cause but lands under the confidence bar, so the case escalates while still
    /// carrying something worth writing up. This is the shape the RCA hand-off exists for.
    /// </summary>
    public const string EscalatingSynthesisWithARootCause = """
        {"conclusion":"root_cause","summary":"vendor timeouts on /booking/confirm","rootCause":"the vendor search API stopped responding","confidence":0.55,
         "citations":[{"kind":"operation-id","value":"op-aaa","citedBy":"LogsAgent"}],
         "conflicts":[],"agreementAssessment":"independent","unknowns":[],"blockedBy":null,"runbook":[]}
        """;

    /// <summary>Escalates with nothing named - the "insufficient_evidence" shape, 17 of 23 real cases.</summary>
    public const string EscalatingSynthesisWithoutARootCause = """
        {"conclusion":"insufficient_evidence","summary":"a latency cluster nobody could attribute","rootCause":null,"confidence":0.3,
         "citations":[{"kind":"operation-id","value":"op-aaa","citedBy":"LogsAgent"}],
         "conflicts":[],"agreementAssessment":"independent","unknowns":["everything"],"blockedBy":"a full drill-down","runbook":[]}
        """;
}

/// <summary>Counts every completion and answers from a script, so "this path spent no LLM call" is assertable rather than assumed.</summary>
internal sealed class ScriptedToolCallingLlmClient(Func<string, IReadOnlyList<LlmMessage>, LlmCompletion> respond) : IToolCallingLlmClient
{
    private readonly object _gate = new();

    public int Calls { get; private set; }

    public List<string> ModelsCalled { get; } = [];

    public Task<LlmCompletion> CompleteAsync(
        string model,
        IReadOnlyList<LlmMessage> messages,
        IReadOnlyList<LlmToolDefinition> tools,
        int maxTokens,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            Calls++;
            ModelsCalled.Add(model);
        }
        return Task.FromResult(respond(model, messages));
    }

    /// <summary>Every call fails, which is what "the provider is down" looks like from here.</summary>
    public static ScriptedToolCallingLlmClient AlwaysUnavailable() =>
        new((model, _) => LlmCompletion.Unavailable(model, "the provider is unreachable"));
}

internal sealed class FakeSpecialist(string agentRole, bool hasSource, string answer = "the fake specialist answered") : IInspectorSpecialist
{
    public int GatherCalls { get; private set; }

    public int AnswerCalls { get; private set; }

    public string AgentRole => agentRole;

    public bool HasSource => hasSource;

    public string NoSourceReason => hasSource ? "" : "this fake has no source";

    public Task<BlackboardEntry> GatherAsync(InspectorContext context, CancellationToken ct = default)
    {
        GatherCalls++;
        return Task.FromResult(hasSource
            ? new BlackboardEntry(agentRole, AgentSourceStatus.Contributed, answer, null)
            : BlackboardEntry.NoSource(agentRole, NoSourceReason));
    }

    public Task<string> AnswerAsync(InspectorContext context, string question, CancellationToken ct = default)
    {
        AnswerCalls++;
        return Task.FromResult(answer);
    }
}

internal sealed class FakeClubIdentityResolver : IClubIdentityResolver
{
    public Task<ClubIdentity> ResolveAsync(string entityKey, CancellationToken ct = default) =>
        Task.FromResult(new ClubIdentity(entityKey, ClubEntityKind.Host, null, null, null, null, ClubResolution.Unresolved));
}

/// <summary>A LogsAgent tool that records every invocation, so the tool-round cap is assertable without touching Application Insights.</summary>
internal sealed class CountingAgentTool : IAgentTool
{
    public int Executions { get; private set; }

    public string Name => "fake_tool";

    public string Description => "a tool that always succeeds with one row";

    public string ParametersJsonSchema => """{"type":"object","properties":{},"required":[],"additionalProperties":false}""";

    public string OwnerAgent => InspectorAgent.Logs;

    public Task<AgentToolResult> ExecuteAsync(string argumentsJson, InspectorContext context, CancellationToken ct = default)
    {
        Executions++;
        return Task.FromResult(new AgentToolResult(true, """{"rows":[{"value":1}]}""", 1, false, null));
    }
}

internal sealed class CountingNotificationService : INotificationService
{
    public List<NotificationRequest> Requests { get; } = [];

    public Task<int> NotifyAsync(NotificationRequest request, CancellationToken ct = default)
    {
        Requests.Add(request);
        return Task.FromResult(1);
    }
}

/// <summary>
/// Wraps a real in-memory case repository and records every phase write, so "phases only move
/// forward and none is missing" is observable instead of inferred from the final row.
/// </summary>
internal sealed class PhaseRecordingForensicCaseRepository(IForensicCaseRepository inner) : IForensicCaseRepository
{
    public List<string> Phases { get; } = [];

    public Task<ForensicCase> AddAsync(ForensicCase forensicCase, CancellationToken ct = default) => inner.AddAsync(forensicCase, ct);

    public Task<ForensicCase> UpdateAsync(ForensicCase forensicCase, CancellationToken ct = default) => inner.UpdateAsync(forensicCase, ct);

    public Task UpdatePhaseAsync(Guid caseId, string phase, CancellationToken ct = default)
    {
        Phases.Add(phase);
        return inner.UpdatePhaseAsync(caseId, phase, ct);
    }

    public Task<ForensicCase?> GetByIdAsync(Guid id, CancellationToken ct = default) => inner.GetByIdAsync(id, ct);

    public Task<ForensicCase?> GetByPackageIdAsync(Guid investigationPackageId, CancellationToken ct = default) =>
        inner.GetByPackageIdAsync(investigationPackageId, ct);

    public Task<IReadOnlyList<ForensicCase>> GetRecentAsync(int top, string? vertical = null, string? clubId = null, string? profileKey = null, CancellationToken ct = default) => inner.GetRecentAsync(top, vertical, clubId, profileKey, ct);

    public Task<InspectorStats> GetStatsAsync(string? profileKey = null, CancellationToken ct = default) => inner.GetStatsAsync(profileKey, ct);
}

/// <summary>Counts compositions so "the Inspector called the RCA Agent" is observable.</summary>
internal sealed class CountingRcaAgent : IRcaAgent
{
    public int Calls { get; private set; }

    public List<Guid> ComposedFor { get; } = [];

    public Task<RcaReport> ComposeAsync(ForensicCase forensicCase, CancellationToken ct = default)
    {
        Calls++;
        ComposedFor.Add(forensicCase.Id);
        return Task.FromResult(new RcaReport
        {
            ForensicCaseId = forensicCase.Id,
            Title = "composed",
            Sections = [new RcaSection("Incident Snapshot", [new RcaStatement("something happened", RcaConfidence.Inference)])],
        });
    }
}


/// <summary>
/// Reproduces the race the unique index on InvestigationPackageId creates: the first lookup finds
/// nothing, the insert then fails because another attempt got there in between, and the second
/// lookup finds the row.
/// </summary>
internal sealed class RacingForensicCaseRepository(ForensicCase existing) : IForensicCaseRepository
{
    public int AddAttempts { get; private set; }

    public int Lookups { get; private set; }

    public Task<ForensicCase> AddAsync(ForensicCase forensicCase, CancellationToken ct = default)
    {
        AddAttempts++;
        throw new InvalidOperationException($"A forensic case already exists for investigation package {forensicCase.InvestigationPackageId}.");
    }

    public Task<ForensicCase> UpdateAsync(ForensicCase forensicCase, CancellationToken ct = default) => Task.FromResult(forensicCase);

    public Task UpdatePhaseAsync(Guid caseId, string phase, CancellationToken ct = default) => Task.CompletedTask;

    public Task<ForensicCase?> GetByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult<ForensicCase?>(existing);

    public Task<ForensicCase?> GetByPackageIdAsync(Guid investigationPackageId, CancellationToken ct = default)
    {
        Lookups++;
        return Task.FromResult<ForensicCase?>(Lookups == 1 ? null : existing);
    }

    public Task<IReadOnlyList<ForensicCase>> GetRecentAsync(int top, string? vertical = null, string? clubId = null, string? profileKey = null, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ForensicCase>>([existing]);

    public Task<InspectorStats> GetStatsAsync(string? profileKey = null, CancellationToken ct = default) => Task.FromResult(InspectorStats.Empty);
}

/// <summary>Reports a fixed day count so the daily cap can be tested without opening 40 real cases.</summary>
internal sealed class StubStatsForensicCaseRepository(int investigationsToday) : IForensicCaseRepository
{
    public Task<ForensicCase> AddAsync(ForensicCase forensicCase, CancellationToken ct = default) => Task.FromResult(forensicCase);

    public Task<ForensicCase> UpdateAsync(ForensicCase forensicCase, CancellationToken ct = default) => Task.FromResult(forensicCase);

    public Task UpdatePhaseAsync(Guid caseId, string phase, CancellationToken ct = default) => Task.CompletedTask;

    public Task<ForensicCase?> GetByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult<ForensicCase?>(null);

    public Task<ForensicCase?> GetByPackageIdAsync(Guid investigationPackageId, CancellationToken ct = default) => Task.FromResult<ForensicCase?>(null);

    public Task<IReadOnlyList<ForensicCase>> GetRecentAsync(int top, string? vertical = null, string? clubId = null, string? profileKey = null, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ForensicCase>>([]);

    public Task<InspectorStats> GetStatsAsync(string? profileKey = null, CancellationToken ct = default) =>
        Task.FromResult(InspectorStats.Empty with { InvestigationsToday = investigationsToday });
}
