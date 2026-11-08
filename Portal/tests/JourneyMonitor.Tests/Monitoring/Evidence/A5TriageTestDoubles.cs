using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring;
using JourneyMonitor.Core.Monitoring.Evidence;
using JourneyMonitor.Core.Monitoring.Inspector;
using JourneyMonitor.Core.Options;
using Microsoft.Extensions.Logging.Abstractions;

namespace JourneyMonitor.Tests.Spec1000.A5;

/// <summary>Builders and hand-written doubles for the A5 triage tests (no mocking framework in this repo).</summary>
internal static class A5TriageFixtures
{
    public static readonly DateTimeOffset Now = new(2026, 11, 4, 14, 20, 0, TimeSpan.Zero);
    public static readonly DateTimeOffset DetectedAt = new(2026, 11, 4, 14, 5, 0, TimeSpan.Zero);

    public const string RedAlertJson =
        """{"alertLevel":"red_alert","possibleRedAlert":true,"confidence":0.9,"summary":"Bookings are failing across hotels.","recommendedNextActions":["Page the hotels on-call","Check the vendor status page"]}""";

    public const string AmberJson =
        """{"alertLevel":"amber","possibleRedAlert":false,"confidence":0.6,"summary":"Latency doubled in hotels.","recommendedNextActions":["Check recent deployments"]}""";

    public static TriageSignal Signal(string? domain = "hotels") => new(
        DetectedAt,
        "Application Health",
        "Latency",
        "2 consecutive increases: Latency 4200 vs reference 2100 (+100.0%)",
        domain,
        null,
        42,
        TriageSignalSources.Daemon);

    public static TriageInvestigation Queued(Guid? id = null, TriageSignal? signal = null)
    {
        var s = signal ?? Signal();
        return new TriageInvestigation(
            id ?? Guid.NewGuid(), TriageDedupKey.For(s), s.AnomalyId, s, TriageInvestigationStatus.Queued,
            null, null, false, Now.AddMinutes(-1), null, null);
    }

    /// <summary>The six production providers' shapes, but every one available with a tiny payload.</summary>
    public static List<IEvidenceProvider> AllAvailableProviders() =>
        TriageDimensions.All.Select(d => (IEvidenceProvider)new A5StubEvidenceProvider(d)).ToList();

    public static TriageMonkeyService Service(
        A5InMemoryTriageStore store,
        A5FakeLlmClient llm,
        A5RecordingNotificationService? notifications = null,
        TriageMode mode = TriageMode.Shadow,
        IEnumerable<IEvidenceProvider>? providers = null,
        A5PauseResolver? pauses = null,
        A5BudgetResolver? budgets = null,
        int evidenceWindowMinutes = 180) =>
        new(
            store,
            providers ?? AllAvailableProviders(),
            llm,
            pauses ?? new A5PauseResolver(),
            budgets ?? new A5BudgetResolver(),
            notifications ?? new A5RecordingNotificationService(),
            Microsoft.Extensions.Options.Options.Create(new TriageOptions { Mode = mode, EvidenceWindowMinutes = evidenceWindowMinutes }),
            new A5FixedTimeProvider(Now),
            NullLogger<TriageMonkeyService>.Instance);
}

internal sealed class A5FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

internal sealed class A5InMemoryTriageStore : ITriageInvestigationStore
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, TriageInvestigation> _rows = [];

    public List<(Guid Id, TriageInvestigationStatus Status)> StatusHistory { get; } = [];

    public int CompleteCalls { get; private set; }

    public Task CreateAsync(TriageInvestigation investigation, CancellationToken ct = default)
    {
        lock (_gate) _rows[investigation.InvestigationId] = investigation;
        return Task.CompletedTask;
    }

    public Task<TriageInvestigation?> GetAsync(Guid investigationId, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_rows.TryGetValue(investigationId, out var row) ? row : null);
    }

    public Task<TriageInvestigation?> FindOpenByDedupKeyAsync(string dedupKey, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_rows.Values.FirstOrDefault(r =>
                r.DedupKey == dedupKey && r.Status is TriageInvestigationStatus.Queued or TriageInvestigationStatus.Running));
        }
    }

    public Task UpdateStatusAsync(Guid investigationId, TriageInvestigationStatus status, string? error, CancellationToken ct = default)
    {
        lock (_gate)
        {
            StatusHistory.Add((investigationId, status));
            if (_rows.TryGetValue(investigationId, out var row)) _rows[investigationId] = row with { Status = status, Error = error };
        }
        return Task.CompletedTask;
    }

    public Task CompleteAsync(Guid investigationId, TriageResult result, bool notified, CancellationToken ct = default)
    {
        lock (_gate)
        {
            CompleteCalls++;
            StatusHistory.Add((investigationId, TriageInvestigationStatus.Completed));
            if (_rows.TryGetValue(investigationId, out var row))
            {
                _rows[investigationId] = row with
                {
                    Status = TriageInvestigationStatus.Completed,
                    Result = result,
                    AlertLevel = result.Assessment.AlertLevel,
                    Notified = notified,
                    CompletedAt = A5TriageFixtures.Now,
                };
            }
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TriageInvestigation>> GetRecentAsync(int top, string? profileKey = null, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<TriageInvestigation>>(
                _rows.Values.Where(r => profileKey is null || r.Signal.ProfileKey == profileKey).OrderByDescending(r => r.CreatedAt).Take(top).ToList());
        }
    }
}

internal sealed class A5FakeLlmClient(Func<string, string> respond) : ILlmClient
{
    public int Calls { get; private set; }

    public List<string> Prompts { get; } = [];

    public static A5FakeLlmClient Returning(string content) => new(_ => content);

    public static A5FakeLlmClient Throwing() => new(_ => throw new InvalidOperationException("provider down"));

    public Task<(string Content, int InputTokens, int OutputTokens, string ModelUsed)> CompleteAsync(string prompt, int maxTokens, CancellationToken ct = default)
    {
        Calls++;
        Prompts.Add(prompt);
        return Task.FromResult((respond(prompt), 100, 20, "fake-model"));
    }

    public Task<EmbeddingResult> GetEmbeddingAsync(string text, CancellationToken ct = default) =>
        Task.FromResult(EmbeddingResult.Unavailable);
}

internal sealed class A5PauseResolver : IAgentPauseResolver
{
    private readonly Dictionary<string, bool> _paused = new(StringComparer.OrdinalIgnoreCase);

    public A5PauseResolver Pause(string key)
    {
        _paused[key] = true;
        return this;
    }

    public Task<bool> IsPausedAsync(string monkeyKey, CancellationToken cancellationToken) =>
        Task.FromResult(_paused.TryGetValue(monkeyKey, out var paused) && paused);

    public Task<IReadOnlyDictionary<string, bool>> GetAllAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<string, bool>>(_paused);

    public void Invalidate() { }
}

internal sealed class A5BudgetResolver : IAgentBudgetResolver
{
    private readonly Dictionary<string, AgentBudgetSetting> _settings = new(StringComparer.OrdinalIgnoreCase);

    public A5BudgetResolver WithMaxRunsPerDay(string key, int max)
    {
        _settings[key] = AgentBudgetSetting.Empty(key) with { MaxRunsPerDay = max };
        return this;
    }

    public Task<ResolvedInspectorBudget> ResolveInspectorAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new ResolvedInspectorBudget(["model-a"], 0, 0, 40, 0));

    public Task<IReadOnlyDictionary<string, AgentBudgetSetting>> GetAllAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<string, AgentBudgetSetting>>(_settings);

    public void Invalidate() { }
}

internal sealed class A5RecordingNotificationService : INotificationService
{
    public List<NotificationRequest> Requests { get; } = [];

    public Task<int> NotifyAsync(NotificationRequest request, CancellationToken ct = default)
    {
        Requests.Add(request);
        return Task.FromResult(1);
    }
}

internal sealed class A5StubEvidenceProvider(string dimension) : IEvidenceProvider
{
    public int Calls { get; private set; }

    public string Dimension => dimension;

    public Task<EvidenceSection> CollectAsync(TriageSignal signal, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken ct = default)
    {
        Calls++;
        var data = EvidenceJson.ToElement(new { Dimension = dimension, From = windowStart, To = windowEnd });
        return Task.FromResult(new EvidenceSection(dimension, true, data, null));
    }
}

internal sealed class A5ThrowingEvidenceProvider(string dimension) : IEvidenceProvider
{
    public string Dimension => dimension;

    public Task<EvidenceSection> CollectAsync(TriageSignal signal, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken ct = default) =>
        throw new InvalidOperationException("warehouse timeout");
}

/// <summary>Wraps a real in-memory queue and records the payload type of every lease attempt, in order.</summary>
internal sealed class A5RecordingQueue(IAgentWorkQueue inner) : IAgentWorkQueue
{
    public List<string> LeaseAttempts { get; } = [];

    public Task<bool> EnqueueAsync(AgentWorkItem item, CancellationToken ct = default) => inner.EnqueueAsync(item, ct);

    public Task<AgentWorkItem?> TryLeaseAsync(string payloadType, string leasedBy, TimeSpan leaseDuration, CancellationToken ct = default)
    {
        LeaseAttempts.Add(payloadType);
        return inner.TryLeaseAsync(payloadType, leasedBy, leaseDuration, ct);
    }

    public Task CompleteAsync(Guid itemId, string leasedBy, CancellationToken ct = default) => inner.CompleteAsync(itemId, leasedBy, ct);

    public Task FailAsync(Guid itemId, string leasedBy, string error, CancellationToken ct = default) => inner.FailAsync(itemId, leasedBy, error, ct);

    public Task<IReadOnlyList<AgentWorkItem>> GetQueuedAsync(string payloadType, int top, CancellationToken ct = default) => inner.GetQueuedAsync(payloadType, top, ct);

    public Task<AgentWorkQueueStats> GetStatsAsync(string payloadType, CancellationToken ct = default) => inner.GetStatsAsync(payloadType, ct);

    public Task<int> DiscardQueuedAsync(string payloadType, IReadOnlyCollection<Guid>? itemIds, string reason, string discardedBy, CancellationToken ct = default) =>
        inner.DiscardQueuedAsync(payloadType, itemIds, reason, discardedBy, ct);
}
