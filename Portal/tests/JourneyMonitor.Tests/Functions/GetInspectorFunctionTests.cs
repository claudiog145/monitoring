using System.Net;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Functions.Functions;
using JourneyMonitor.Infrastructure.Monitoring;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Tests.Functions;

/// <summary>
/// The HTTP contract of the Inspector Monkey's read side. The Web proxy and the portal UI are
/// written against these exact keys, so the shape is asserted here rather than left to whatever an
/// anonymous object happens to serialise to.
/// </summary>
public class GetInspectorFunctionTests
{
    private const string Secret = "internal-secret";

    private static ForensicCase Case(
        string status = ForensicCaseStatus.Resolved,
        string? decision = InspectorDecision.Resolve,
        DateTimeOffset? startedAt = null,
        DateTimeOffset? completedAt = null) => new()
        {
            InvestigationPackageId = Guid.NewGuid(),
            CaseId = "SOR | Hotels | host:stays.example.com | 07 Booking | 2026-10-07T15:00:00Z",
            EntityKey = "host:stays.example.com",
            BrandName = "Stays Club",
            Vertical = "Hotels",
            Stage = "07 Booking",
            Status = status,
            Decision = decision,
            Verdict = "Vendor timeouts on /booking/confirm.",
            Confidence = 0.82,
            CitationsTotal = 4,
            CitationsVerified = 3,
            LlmCalls = 22,
            TokensIn = 50_000,
            TokensOut = 6_000,
            DurationMs = 142_000,
            StartedAt = startedAt ?? DateTimeOffset.UtcNow.AddMinutes(-3),
            CompletedAt = completedAt ?? DateTimeOffset.UtcNow.AddMinutes(-1),
            SynthesisJson = """{"conclusion":"root_cause"}""",
            BlackboardJson = """[{"agentRole":"LogsAgent","status":"contributed"}]""",
            GroundingJson = """[{"kind":"operation-id","verification":"verified"}]""",
            RunbookJson = """["Check the vendor status page"]""",
        };

    private static GetInspectorFunction Build(
        IForensicCaseRepository cases,
        IAgentTranscriptRepository? transcripts = null,
        IAgentWorkQueue? queue = null,
        INotificationLogRepository? notifications = null,
        InspectorOptions? inspector = null) =>
        new(
            cases,
            transcripts ?? new InMemoryAgentTranscriptRepository(),
            queue ?? new InMemoryAgentWorkQueue(),
            notifications ?? new InMemoryNotificationLogRepository(),
            Options.Create(inspector ?? new InspectorOptions()),
            Options.Create(new MonitoringOptions { InternalApiSecret = Secret }),
            NullLogger<GetInspectorFunction>.Instance);

    [Fact]
    public async Task CasesListIsCamelCasedAndCarriesNoTranscript()
    {
        var repo = new InMemoryForensicCaseRepository();
        await repo.AddAsync(Case());

        var response = await Build(repo).GetCasesAsync(new FakeHttpRequestData("https://localhost/api/inspector/cases"), default);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var item = response.ReadJson()["cases"]!.AsArray().Single()!;
        Assert.Equal("Stays Club", item["brandName"]!.GetValue<string>());
        Assert.Equal(ForensicCaseStatus.Resolved, item["status"]!.GetValue<string>());
        Assert.Equal(3, item["citationsVerified"]!.GetValue<int>());
        // The anonymous list must never carry the half that embeds member/correlation ids.
        Assert.Null(item["turns"]);
        Assert.Null(item["synthesis"]);
    }

    [Fact]
    public async Task CasesListHonoursTopAndDefaultsTo25()
    {
        var repo = new InMemoryForensicCaseRepository();
        for (var i = 0; i < 30; i++)
        {
            await repo.AddAsync(Case(startedAt: DateTimeOffset.UtcNow.AddMinutes(-i)));
        }
        var function = Build(repo);

        var defaulted = await function.GetCasesAsync(new FakeHttpRequestData("https://localhost/api/inspector/cases"), default);
        var capped = await function.GetCasesAsync(new FakeHttpRequestData("https://localhost/api/inspector/cases?top=3"), default);

        Assert.Equal(25, defaulted.ReadJson()["cases"]!.AsArray().Count);
        Assert.Equal(3, capped.ReadJson()["cases"]!.AsArray().Count);
    }

    [Fact]
    public async Task CaseDetailFailsClosedWithoutTheInternalSecret()
    {
        var repo = new InMemoryForensicCaseRepository();
        var forensicCase = await repo.AddAsync(Case());

        var response = await Build(repo).GetCaseByIdAsync(
            new FakeHttpRequestData($"https://localhost/api/inspector/cases/{forensicCase.Id}"),
            forensicCase.Id.ToString(),
            default);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CaseDetailReturnsTheTranscriptAndParsesStoredJson()
    {
        var repo = new InMemoryForensicCaseRepository();
        var forensicCase = await repo.AddAsync(Case());
        var transcripts = new InMemoryAgentTranscriptRepository();
        var turn = await transcripts.AddTurnAsync(new AgentTurn
        {
            ForensicCaseId = forensicCase.Id,
            AgentRole = InspectorAgent.Logs,
            Kind = AgentKind.Specialist,
            PromptText = "You are the LogsAgent...",
            RawResponseText = """{"summary":"timeouts"}""",
            OutcomeStatus = AgentOutcomeStatus.Final,
            OutcomeJson = """{"summary":"timeouts"}""",
            ModelUsed = "gpt-4o",
        });
        await transcripts.AddToolCallAsync(new AgentToolCall
        {
            AgentTurnId = turn.Id,
            ToolName = "run_kql",
            ArgumentsJson = """{"query":"exceptions | take 5"}""",
            ResultJson = """[{"count":5}]""",
            Success = true,
            ResultRowCount = 1,
        });

        var response = await Build(repo, transcripts).GetCaseByIdAsync(
            new FakeHttpRequestData($"https://localhost/api/inspector/cases/{forensicCase.Id}")
                .WithHeader("X-Internal-Api-Secret", Secret),
            forensicCase.Id.ToString(),
            default);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = response.ReadJson();
        // Parsed to real JSON, not JSON-in-a-string - that is the whole point of the *Json columns
        // being unwrapped here.
        Assert.Equal("root_cause", body["synthesis"]!["conclusion"]!.GetValue<string>());
        Assert.Single(body["blackboard"]!.AsArray());
        Assert.Single(body["grounding"]!.AsArray());
        Assert.Single(body["runbook"]!.AsArray());

        var returnedTurn = body["turns"]!.AsArray().Single()!;
        Assert.Equal("You are the LogsAgent...", returnedTurn["promptText"]!.GetValue<string>());
        Assert.Equal("timeouts", returnedTurn["outcome"]!["summary"]!.GetValue<string>());

        var returnedToolCall = body["toolCalls"]!.AsArray().Single()!;
        Assert.Equal("run_kql", returnedToolCall["toolName"]!.GetValue<string>());
        Assert.Equal("exceptions | take 5", returnedToolCall["arguments"]!["query"]!.GetValue<string>());
        Assert.False(returnedToolCall["truncated"]!.GetValue<bool>());
    }

    [Fact]
    public async Task CaseDetailRejectsANonGuidIdBeforeTouchingTheRepository()
    {
        var response = await Build(new InMemoryForensicCaseRepository()).GetCaseByIdAsync(
            new FakeHttpRequestData("https://localhost/api/inspector/cases/not-a-guid")
                .WithHeader("X-Internal-Api-Secret", Secret),
            "not-a-guid",
            default);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task QueueReportsTheBacklogAndTheResolvedCostLimits()
    {
        var queue = new InMemoryAgentWorkQueue();
        await queue.EnqueueAsync(new AgentWorkItem
        {
            SourceMonkey = MonkeyKey.LatencyScoutMonkey,
            PayloadType = AgentWorkPayloadType.InvestigationPackage,
            PayloadId = Guid.NewGuid().ToString(),
            DedupKey = "case-1",
            Priority = 3,
        });
        var repo = new InMemoryForensicCaseRepository();
        // Explicitly "now" rather than the helper's default of three minutes ago: that default
        // lands on the previous UTC day during the first minutes after midnight, which made this
        // assertion fail once a day for reasons that have nothing to do with what it checks.
        await repo.AddAsync(Case(startedAt: DateTimeOffset.UtcNow));

        var response = await Build(repo, queue: queue, inspector: new InspectorOptions { Concurrency = 2 })
            .GetQueueAsync(new FakeHttpRequestData("https://localhost/api/inspector/queue"), default);

        var body = response.ReadJson();
        Assert.Equal("case-1", body["queued"]!.AsArray().Single()!["dedupKey"]!.GetValue<string>());
        Assert.Equal(1, body["stats"]!["queued"]!.GetValue<int>());
        // Resolved values, never the raw nullable setting - a null would read as "no limit".
        Assert.Equal(40, body["limits"]!["maxInvestigationsPerDay"]!.GetValue<int>());
        Assert.Equal(2, body["limits"]!["concurrency"]!.GetValue<int>());
        Assert.Equal(1, body["limits"]!["investigationsToday"]!.GetValue<int>());
    }

    [Fact]
    public async Task ToastsOnlyReturnsWhatIsNewerThanSince()
    {
        var notifications = new InMemoryNotificationLogRepository();
        var cutoff = DateTimeOffset.UtcNow.AddHours(-1);
        await notifications.AddAsync(new NotificationLogEntry(
            Guid.NewGuid(), NotificationSourceType.ForensicCase, Guid.NewGuid(), NotificationChannel.Toast,
            "toast", true, null, cutoff.AddMinutes(-10), "Old"));
        await notifications.AddAsync(new NotificationLogEntry(
            Guid.NewGuid(), NotificationSourceType.ForensicCase, Guid.NewGuid(), NotificationChannel.Toast,
            "toast", true, null, cutoff.AddMinutes(10), "New"));

        var response = await Build(new InMemoryForensicCaseRepository(), notifications: notifications)
            .GetToastsAsync(
                new FakeHttpRequestData($"https://localhost/api/inspector/toasts?since={Uri.EscapeDataString(cutoff.ToString("O"))}"),
                default);

        var toasts = response.ReadJson()["toasts"]!.AsArray();
        Assert.Equal("New", Assert.Single(toasts)!["subject"]!.GetValue<string>());
    }
}
