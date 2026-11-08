using System.Text.Json;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring.Evidence;
using JourneyMonitor.Core.Monitoring.Inspector;
using JourneyMonitor.Core.Options;

namespace JourneyMonitor.Tests.Spec1000.A5;

public class TriageMonkeyServiceTests
{
    [Fact]
    public async Task Triage_CollectsAllSixDimensions_UnavailableOnesMarked()
    {
        var store = new A5InMemoryTriageStore();
        var investigation = A5TriageFixtures.Queued();
        await store.CreateAsync(investigation);
        var llm = A5FakeLlmClient.Returning(A5TriageFixtures.AmberJson);
        IEvidenceProvider[] providers =
        [
            new A5StubEvidenceProvider(TriageDimensions.ApplicationHealth),
            new A5StubEvidenceProvider(TriageDimensions.KnowledgeBase),
            new InfrastructureEvidenceProvider(),
            new CodeReliabilityEvidenceProvider(),
            new RecentChangesEvidenceProvider(),
            // No BusinessHealth provider on purpose.
        ];

        var result = await A5TriageFixtures.Service(store, llm, providers: providers).InvestigateAsync(investigation.InvestigationId, CancellationToken.None);

        Assert.NotNull(result);
        Assert.NotNull(result.Correlation.ApplicationHealth);
        Assert.NotNull(result.Correlation.KnowledgeBase);
        Assert.Null(result.Correlation.BusinessHealth);
        Assert.Null(result.Correlation.InfrastructureHealth);
        Assert.Null(result.Correlation.CodeReliability);
        Assert.Null(result.Correlation.RecentChanges);

        var prompt = Assert.Single(llm.Prompts);
        foreach (var dimension in TriageDimensions.All) Assert.Contains($"[{dimension}]", prompt);
        Assert.Contains("[businessHealth] unavailable: no provider", prompt);
        Assert.Contains("[infrastructureHealth] unavailable: not implemented (Fase 3)", prompt);
        Assert.Contains("[codeReliability] unavailable: not implemented (Fase 3)", prompt);
        Assert.Contains("[recentChanges] unavailable: not implemented (Fase 3)", prompt);

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(result, TriageJson.Options));
        var correlation = doc.RootElement.GetProperty("correlation");
        Assert.Equal(6, correlation.EnumerateObject().Count());
    }

    [Fact]
    public async Task Triage_EvidenceProviderFailure_DoesNotFailInvestigation()
    {
        var store = new A5InMemoryTriageStore();
        var investigation = A5TriageFixtures.Queued();
        await store.CreateAsync(investigation);
        var llm = A5FakeLlmClient.Returning(A5TriageFixtures.AmberJson);
        var providers = A5TriageFixtures.AllAvailableProviders()
            .Where(p => p.Dimension != TriageDimensions.ApplicationHealth)
            .Append(new A5ThrowingEvidenceProvider(TriageDimensions.ApplicationHealth))
            .ToList();

        var result = await A5TriageFixtures.Service(store, llm, providers: providers).InvestigateAsync(investigation.InvestigationId, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Null(result.Correlation.ApplicationHealth);
        Assert.NotNull(result.Correlation.BusinessHealth);
        Assert.NotNull(result.Correlation.KnowledgeBase);
        Assert.Contains("[applicationHealth] unavailable: error: warehouse timeout", Assert.Single(llm.Prompts));

        var stored = await store.GetAsync(investigation.InvestigationId);
        Assert.Equal(TriageInvestigationStatus.Completed, stored!.Status);
        Assert.Equal(TriageAlertLevels.Amber, stored.AlertLevel);
    }

    [Fact]
    public async Task Triage_ProducesContractValidJson()
    {
        var store = new A5InMemoryTriageStore();
        var investigation = A5TriageFixtures.Queued();
        await store.CreateAsync(investigation);
        var llm = A5FakeLlmClient.Returning(A5TriageFixtures.AmberJson);

        var result = await A5TriageFixtures.Service(store, llm).InvestigateAsync(investigation.InvestigationId, CancellationToken.None);

        Assert.NotNull(result);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(result, TriageJson.Options));
        var root = doc.RootElement;

        AssertProperties(root, "investigationId", "trigger", "correlation", "assessment");
        AssertProperties(root.GetProperty("trigger"), "detectedAt", "pillar", "kpi", "domain", "stage", "condition");
        AssertProperties(root.GetProperty("correlation"), [.. TriageDimensions.All]);
        AssertProperties(root.GetProperty("assessment"), "alertLevel", "possibleRedAlert", "confidence", "summary", "recommendedNextActions");

        Assert.Equal(investigation.InvestigationId, root.GetProperty("investigationId").GetGuid());
        var trigger = root.GetProperty("trigger");
        Assert.Equal(A5TriageFixtures.DetectedAt, trigger.GetProperty("detectedAt").GetDateTimeOffset());
        Assert.Equal("Application Health", trigger.GetProperty("pillar").GetString());
        Assert.Equal("Latency", trigger.GetProperty("kpi").GetString());
        Assert.Equal("hotels", trigger.GetProperty("domain").GetString());
        Assert.Equal(JsonValueKind.Null, trigger.GetProperty("stage").ValueKind);
        Assert.Equal(investigation.Signal.Detection, trigger.GetProperty("condition").GetString());

        foreach (var dimension in TriageDimensions.All)
        {
            Assert.Equal(JsonValueKind.Object, root.GetProperty("correlation").GetProperty(dimension).ValueKind);
        }

        var assessment = root.GetProperty("assessment");
        Assert.Equal("amber", assessment.GetProperty("alertLevel").GetString());
        Assert.False(assessment.GetProperty("possibleRedAlert").GetBoolean());
        Assert.Equal(0.6, assessment.GetProperty("confidence").GetDouble(), 3);
        Assert.Equal("Latency doubled in hotels.", assessment.GetProperty("summary").GetString());
        Assert.Equal(["Check recent deployments"], assessment.GetProperty("recommendedNextActions").EnumerateArray().Select(a => a.GetString()!).ToArray());
    }

    [Fact]
    public async Task Triage_EvidenceWindow_SpansWindowMinutesBeforeTo15MinutesAfterDetection()
    {
        var store = new A5InMemoryTriageStore();
        var investigation = A5TriageFixtures.Queued();
        await store.CreateAsync(investigation);

        var result = await A5TriageFixtures.Service(store, A5FakeLlmClient.Returning(A5TriageFixtures.AmberJson), evidenceWindowMinutes: 60)
            .InvestigateAsync(investigation.InvestigationId, CancellationToken.None);

        var data = result!.Correlation.ApplicationHealth!.Value;
        Assert.Equal(A5TriageFixtures.DetectedAt.AddMinutes(-60), data.GetProperty("from").GetDateTimeOffset());
        Assert.Equal(A5TriageFixtures.DetectedAt.AddMinutes(15), data.GetProperty("to").GetDateTimeOffset());
    }

    public static TheoryData<string> InvalidLlmResponses() => new()
    {
        "this is not json",
        "",
        """{"alertLevel":"purple","possibleRedAlert":false,"confidence":0.5,"summary":"x","recommendedNextActions":[]}""",
        """{"alertLevel":"amber","possibleRedAlert":false,"confidence":1.5,"summary":"x","recommendedNextActions":[]}""",
        """{"alertLevel":"amber","possibleRedAlert":false,"confidence":0.5,"summary":"   ","recommendedNextActions":[]}""",
        """{"alertLevel":"red_alert","possibleRedAlert":"yes","confidence":0.5,"summary":"x","recommendedNextActions":[]}""",
        """{"alertLevel":"red_alert","possibleRedAlert":true,"confidence":0.5,"summary":"x","recommendedNextActions":"call someone"}""",
    };

    [Theory]
    [MemberData(nameof(InvalidLlmResponses))]
    public async Task Triage_InvalidLlmJson_FallsBackToInformational(string llmContent)
    {
        var store = new A5InMemoryTriageStore();
        var investigation = A5TriageFixtures.Queued();
        await store.CreateAsync(investigation);
        var notifications = new A5RecordingNotificationService();

        var result = await A5TriageFixtures.Service(store, A5FakeLlmClient.Returning(llmContent), notifications, TriageMode.Live)
            .InvestigateAsync(investigation.InvestigationId, CancellationToken.None);

        AssertFallback(result, TriageMonkeyService.LlmFallbackSummary);
        Assert.Empty(notifications.Requests);
        Assert.Equal(TriageInvestigationStatus.Completed, (await store.GetAsync(investigation.InvestigationId))!.Status);
    }

    [Fact]
    public async Task Triage_LlmThrows_FallsBackToInformational()
    {
        var store = new A5InMemoryTriageStore();
        var investigation = A5TriageFixtures.Queued();
        await store.CreateAsync(investigation);

        var result = await A5TriageFixtures.Service(store, A5FakeLlmClient.Throwing())
            .InvestigateAsync(investigation.InvestigationId, CancellationToken.None);

        AssertFallback(result, TriageMonkeyService.LlmFallbackSummary);
    }

    [Fact]
    public async Task Triage_LlmResponseIsNormalizedAndCapped()
    {
        var actions = string.Join(",", Enumerable.Range(1, 15).Select(i => $"\"action {i}\""));
        var summary = new string('s', 2500);
        var content = "```json\n{\"alertLevel\":\"AMBER\",\"possibleRedAlert\":false,\"confidence\":null,\"summary\":\"" + summary + "\",\"recommendedNextActions\":[" + actions + "]}\n```";
        var store = new A5InMemoryTriageStore();
        var investigation = A5TriageFixtures.Queued();
        await store.CreateAsync(investigation);

        var result = await A5TriageFixtures.Service(store, A5FakeLlmClient.Returning(content))
            .InvestigateAsync(investigation.InvestigationId, CancellationToken.None);

        Assert.Equal(TriageAlertLevels.Amber, result!.Assessment.AlertLevel);
        Assert.Null(result.Assessment.Confidence);
        Assert.Equal(2000, result.Assessment.Summary.Length);
        Assert.Equal(10, result.Assessment.RecommendedNextActions.Count);
    }

    [Fact]
    public async Task Triage_ShadowMode_NeverNotifies()
    {
        var store = new A5InMemoryTriageStore();
        var investigation = A5TriageFixtures.Queued();
        await store.CreateAsync(investigation);
        var notifications = new A5RecordingNotificationService();

        var result = await A5TriageFixtures.Service(store, A5FakeLlmClient.Returning(A5TriageFixtures.RedAlertJson), notifications, TriageMode.Shadow)
            .InvestigateAsync(investigation.InvestigationId, CancellationToken.None);

        Assert.Equal(TriageAlertLevels.RedAlert, result!.Assessment.AlertLevel);
        Assert.True(result.Assessment.PossibleRedAlert);
        Assert.Empty(notifications.Requests);
        var stored = await store.GetAsync(investigation.InvestigationId);
        Assert.False(stored!.Notified);
        Assert.Equal(TriageAlertLevels.RedAlert, stored.AlertLevel);
    }

    [Fact]
    public async Task Triage_LiveMode_NotifiesOnlyRedAlert()
    {
        var store = new A5InMemoryTriageStore();
        var red = A5TriageFixtures.Queued();
        var amber = A5TriageFixtures.Queued();
        await store.CreateAsync(red);
        await store.CreateAsync(amber);
        var notifications = new A5RecordingNotificationService();

        await A5TriageFixtures.Service(store, A5FakeLlmClient.Returning(A5TriageFixtures.AmberJson), notifications, TriageMode.Live)
            .InvestigateAsync(amber.InvestigationId, CancellationToken.None);
        Assert.Empty(notifications.Requests);
        Assert.False((await store.GetAsync(amber.InvestigationId))!.Notified);

        await A5TriageFixtures.Service(store, A5FakeLlmClient.Returning(A5TriageFixtures.RedAlertJson), notifications, TriageMode.Live)
            .InvestigateAsync(red.InvestigationId, CancellationToken.None);

        var request = Assert.Single(notifications.Requests);
        Assert.Equal(red.InvestigationId, request.SourceId);
        Assert.Equal(TriageMonkeyService.NotificationSourceType, request.SourceType);
        Assert.Equal(NotificationSeverity.Critical, request.Severity);
        Assert.Contains("Bookings are failing across hotels.", request.Body);
        Assert.True((await store.GetAsync(red.InvestigationId))!.Notified);
    }

    [Fact]
    public async Task Triage_BudgetExhausted_DoesNotCallLlm()
    {
        var store = new A5InMemoryTriageStore();
        var earlier = A5TriageFixtures.Queued() with { Status = TriageInvestigationStatus.Completed };
        var investigation = A5TriageFixtures.Queued();
        await store.CreateAsync(earlier);
        await store.CreateAsync(investigation);
        var llm = A5FakeLlmClient.Returning(A5TriageFixtures.RedAlertJson);
        var notifications = new A5RecordingNotificationService();
        var budgets = new A5BudgetResolver().WithMaxRunsPerDay(new TriageOptions().AgentKey, 1);

        var result = await A5TriageFixtures.Service(store, llm, notifications, TriageMode.Live, budgets: budgets)
            .InvestigateAsync(investigation.InvestigationId, CancellationToken.None);

        Assert.Equal(0, llm.Calls);
        Assert.NotNull(result);
        Assert.Equal(TriageAlertLevels.Informational, result.Assessment.AlertLevel);
        Assert.False(result.Assessment.PossibleRedAlert);
        Assert.Null(result.Assessment.Confidence);
        Assert.Contains("budget", result.Assessment.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(notifications.Requests);
        Assert.Equal(TriageInvestigationStatus.Completed, (await store.GetAsync(investigation.InvestigationId))!.Status);
    }

    [Fact]
    public async Task Triage_BudgetNotYetReached_CallsLlm()
    {
        var store = new A5InMemoryTriageStore();
        var investigation = A5TriageFixtures.Queued();
        await store.CreateAsync(investigation);
        var llm = A5FakeLlmClient.Returning(A5TriageFixtures.AmberJson);
        var budgets = new A5BudgetResolver().WithMaxRunsPerDay(new TriageOptions().AgentKey, 1);

        var result = await A5TriageFixtures.Service(store, llm, budgets: budgets).InvestigateAsync(investigation.InvestigationId, CancellationToken.None);

        Assert.Equal(1, llm.Calls);
        Assert.Equal(TriageAlertLevels.Amber, result!.Assessment.AlertLevel);
    }

    [Fact]
    public async Task Triage_AgentPaused_DoesNotCallLlm()
    {
        var store = new A5InMemoryTriageStore();
        var investigation = A5TriageFixtures.Queued();
        await store.CreateAsync(investigation);
        var llm = A5FakeLlmClient.Returning(A5TriageFixtures.RedAlertJson);
        var pauses = new A5PauseResolver().Pause(new TriageOptions().AgentKey);

        var result = await A5TriageFixtures.Service(store, llm, pauses: pauses).InvestigateAsync(investigation.InvestigationId, CancellationToken.None);

        Assert.Equal(0, llm.Calls);
        Assert.Equal(TriageAlertLevels.Informational, result!.Assessment.AlertLevel);
        Assert.Contains("paused", result.Assessment.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Triage_ModeOff_CompletesWithoutLlm()
    {
        var store = new A5InMemoryTriageStore();
        var investigation = A5TriageFixtures.Queued();
        await store.CreateAsync(investigation);
        var llm = A5FakeLlmClient.Returning(A5TriageFixtures.RedAlertJson);
        var notifications = new A5RecordingNotificationService();

        var result = await A5TriageFixtures.Service(store, llm, notifications, TriageMode.Off)
            .InvestigateAsync(investigation.InvestigationId, CancellationToken.None);

        Assert.Equal(0, llm.Calls);
        AssertFallback(result, TriageMonkeyService.ModeOffSummary);
        Assert.Equal("Triage disabled (Mode=Off)", result!.Assessment.Summary);
        Assert.NotNull(result.Correlation.ApplicationHealth);
        Assert.Empty(notifications.Requests);
        Assert.Equal(TriageInvestigationStatus.Completed, (await store.GetAsync(investigation.InvestigationId))!.Status);
    }

    [Fact]
    public async Task Triage_UpdatesStatusRunningThenCompleted()
    {
        var store = new A5InMemoryTriageStore();
        var investigation = A5TriageFixtures.Queued();
        await store.CreateAsync(investigation);

        await A5TriageFixtures.Service(store, A5FakeLlmClient.Returning(A5TriageFixtures.AmberJson))
            .InvestigateAsync(investigation.InvestigationId, CancellationToken.None);

        Assert.Equal(
            [TriageInvestigationStatus.Running, TriageInvestigationStatus.Completed],
            store.StatusHistory.Where(h => h.Id == investigation.InvestigationId).Select(h => h.Status).ToArray());
        var stored = await store.GetAsync(investigation.InvestigationId);
        Assert.Equal(TriageInvestigationStatus.Completed, stored!.Status);
        Assert.NotNull(stored.Result);
    }

    [Fact]
    public async Task Triage_MissingOrNotQueuedInvestigation_ReturnsNullWithoutWork()
    {
        var store = new A5InMemoryTriageStore();
        var running = A5TriageFixtures.Queued() with { Status = TriageInvestigationStatus.Running };
        await store.CreateAsync(running);
        var llm = A5FakeLlmClient.Returning(A5TriageFixtures.AmberJson);
        var service = A5TriageFixtures.Service(store, llm);

        Assert.Null(await service.InvestigateAsync(Guid.NewGuid(), CancellationToken.None));
        Assert.Null(await service.InvestigateAsync(running.InvestigationId, CancellationToken.None));
        Assert.Equal(0, llm.Calls);
        Assert.Empty(store.StatusHistory);
    }

    [Fact]
    public async Task Triage_UnexpectedFailure_MarksFailedAndRethrows()
    {
        var store = new A5FailingCompleteTriageStore();
        var investigation = A5TriageFixtures.Queued();
        await store.CreateAsync(investigation);
        var failing = new TriageMonkeyService(
            store,
            A5TriageFixtures.AllAvailableProviders(),
            A5FakeLlmClient.Returning(A5TriageFixtures.AmberJson),
            new A5PauseResolver(),
            new A5BudgetResolver(),
            new A5RecordingNotificationService(),
            Microsoft.Extensions.Options.Options.Create(new TriageOptions()),
            new A5FixedTimeProvider(A5TriageFixtures.Now),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TriageMonkeyService>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => failing.InvestigateAsync(investigation.InvestigationId, CancellationToken.None));

        var stored = await store.GetAsync(investigation.InvestigationId);
        Assert.Equal(TriageInvestigationStatus.Failed, stored!.Status);
        Assert.Equal("store write failed", stored.Error);
    }

    private static void AssertFallback(TriageResult? result, string expectedSummary)
    {
        Assert.NotNull(result);
        Assert.Equal(TriageAlertLevels.Informational, result.Assessment.AlertLevel);
        Assert.False(result.Assessment.PossibleRedAlert);
        Assert.Null(result.Assessment.Confidence);
        Assert.Equal(expectedSummary, result.Assessment.Summary);
    }

    private static void AssertProperties(JsonElement element, params string[] expected)
    {
        Assert.Equal(JsonValueKind.Object, element.ValueKind);
        var actual = element.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(expected.OrderBy(n => n, StringComparer.Ordinal).ToArray(), actual);
    }
}

internal sealed class A5FailingCompleteTriageStore(A5InMemoryTriageStore? inner = null) : ITriageInvestigationStore
{
    public A5InMemoryTriageStore Inner { get; } = inner ?? new();

    public Task CreateAsync(TriageInvestigation investigation, CancellationToken ct = default) => Inner.CreateAsync(investigation, ct);

    public Task<TriageInvestigation?> GetAsync(Guid investigationId, CancellationToken ct = default) => Inner.GetAsync(investigationId, ct);

    public Task<TriageInvestigation?> FindOpenByDedupKeyAsync(string dedupKey, CancellationToken ct = default) => Inner.FindOpenByDedupKeyAsync(dedupKey, ct);

    public Task UpdateStatusAsync(Guid investigationId, TriageInvestigationStatus status, string? error, CancellationToken ct = default) =>
        Inner.UpdateStatusAsync(investigationId, status, error, ct);

    public Task CompleteAsync(Guid investigationId, TriageResult result, bool notified, CancellationToken ct = default) =>
        throw new InvalidOperationException("store write failed");

    public Task<IReadOnlyList<TriageInvestigation>> GetRecentAsync(int top, string? profileKey = null, CancellationToken ct = default) => Inner.GetRecentAsync(top, profileKey, ct);
}
