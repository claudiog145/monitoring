using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Functions.Functions;
using JourneyMonitor.Infrastructure.Monitoring;
using JourneyMonitor.Tests.Monitoring;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Text.Json.Nodes;

namespace JourneyMonitor.Tests.Functions;

/// <summary>
/// The inspector-monkey card inside GET /api/monkeys. The two behaviours worth protecting are the
/// headline number (citation verification, not a win rate) and the abandoned-case handling that
/// already shipped as a bug on the Scout's card.
/// </summary>
public class GetMonkeysFunctionInspectorCardTests
{
    private static ForensicCase Case(
        string status,
        DateTimeOffset startedAt,
        DateTimeOffset? completedAt,
        int citationsTotal = 0,
        int citationsVerified = 0,
        string? decision = null) => new()
        {
            InvestigationPackageId = Guid.NewGuid(),
            CaseId = $"case-{Guid.NewGuid():N}",
            EntityKey = "host:stays.example.com",
            BrandName = "Stays Club",
            Vertical = "Hotels",
            Stage = "07 Booking",
            Status = status,
            Decision = decision,
            Verdict = "Vendor timeouts on /booking/confirm.",
            Confidence = 0.82,
            CitationsTotal = citationsTotal,
            CitationsVerified = citationsVerified,
            StartedAt = startedAt,
            CompletedAt = completedAt,
            Phase = completedAt is null ? ForensicCasePhase.Agents : null,
        };

    private static async Task<JsonNode> GetCardAsync(
        IForensicCaseRepository cases,
        InspectorOptions? inspector = null,
        IAgentWorkQueue? queue = null)
    {
        var function = new GetMonkeysFunction(
            new InMemoryFindingsRepository(),
            new InMemoryDoctorReportsRepository(),
            new InMemoryNotificationLogRepository(),
            new InMemoryInvestigationPackageRepository(),
            new InMemoryScoutRunRepository(),
            cases,
            new InMemoryRcaReportRepository(),
            queue ?? new InMemoryAgentWorkQueue(),
            FakeAgentPauseResolver.NothingPaused(),
            Options.Create(inspector ?? new InspectorOptions()),
            Options.Create(new RcaOptions()),
            new InMemoryMonkeyRunTracker(),
            NullLogger<GetMonkeysFunction>.Instance);

        var response = await function.RunAsync(new FakeHttpRequestData("https://localhost/api/monkeys"), default);
        return response.ReadJson().AsArray()
            .Single(m => m!["id"]!.GetValue<string>() == MonkeyKey.InspectorMonkey)!;
    }

    [Fact]
    public async Task SuccessRateIsTheCitationVerificationRateAndNotAWinRate()
    {
        var repo = new InMemoryForensicCaseRepository();
        var now = DateTimeOffset.UtcNow;
        // Every case "succeeded", but only half of what they cited exists. A resolved/total rate
        // would read 100% here, which is exactly the number this card must not show.
        await repo.AddAsync(Case(ForensicCaseStatus.Resolved, now.AddMinutes(-5), now.AddMinutes(-4), citationsTotal: 4, citationsVerified: 2, decision: InspectorDecision.Resolve));
        await repo.AddAsync(Case(ForensicCaseStatus.Resolved, now.AddMinutes(-9), now.AddMinutes(-8), citationsTotal: 4, citationsVerified: 2, decision: InspectorDecision.Resolve));

        var card = await GetCardAsync(repo);

        Assert.Equal(0.5, card["successRate"]!.GetValue<double>());
        Assert.Equal(0.5, card["inspectorStats"]!["citationVerificationRate"]!.GetValue<double>());
        Assert.Equal(2, card["inspectorStats"]!["resolved"]!.GetValue<int>());
    }

    [Fact]
    public async Task AnInterruptedCaseIsNotInFlightAndShowsAsFailedOnTheHeartbeat()
    {
        var repo = new InMemoryForensicCaseRepository();
        await repo.AddAsync(Case(ForensicCaseStatus.Running, DateTimeOffset.UtcNow.AddHours(-2), completedAt: null));

        var card = await GetCardAsync(repo);

        // Without this the card says "Investigating now" forever - the bug the Scout's card shipped.
        Assert.Null(card["currentCase"]);
        var bar = card["cases"]!.AsArray().Single()!;
        Assert.Equal(ForensicCaseStatus.Failed, bar["status"]!.GetValue<string>());
        Assert.Equal("The investigation was interrupted and never completed.", bar["errorMessage"]!.GetValue<string>());
        Assert.Equal("FAILED", card["activity"]!.AsArray().Single()!["stage"]!.GetValue<string>());
    }

    [Fact]
    public async Task ACaseStartedMomentsAgoIsReportedAsInFlightWithEqualPhaseWeights()
    {
        var repo = new InMemoryForensicCaseRepository();
        await repo.AddAsync(Case(ForensicCaseStatus.Running, DateTimeOffset.UtcNow.AddMinutes(-1), completedAt: null));

        var card = await GetCardAsync(repo);

        var current = card["currentCase"]!;
        Assert.Equal(ForensicCasePhase.Agents, current["phase"]!.GetValue<string>());
        var phases = current["phases"]!.AsArray();
        Assert.Equal(ForensicCasePhase.All.Length, phases.Count);
        // Equal weights are deliberate: per-phase duration has never been measured.
        Assert.All(phases, p => Assert.Equal(1, p!["weight"]!.GetValue<int>()));
        // It has no verdict yet, so it belongs in currentCase and not in the timeline.
        Assert.Empty(card["activity"]!.AsArray());
    }

    [Fact]
    public async Task StatusIsErrorAfterAFailedCaseAndPausedWhenTheInspectorIsOff()
    {
        var now = DateTimeOffset.UtcNow;
        var failedRepo = new InMemoryForensicCaseRepository();
        await failedRepo.AddAsync(Case(ForensicCaseStatus.Failed, now.AddMinutes(-5), now.AddMinutes(-4)));

        var disabledRepo = new InMemoryForensicCaseRepository();
        await disabledRepo.AddAsync(Case(ForensicCaseStatus.Resolved, now.AddMinutes(-5), now.AddMinutes(-4)));

        Assert.Equal("error", (await GetCardAsync(failedRepo))["status"]!.GetValue<string>());
        Assert.Equal("paused", (await GetCardAsync(disabledRepo, new InspectorOptions { Enabled = false }))["status"]!.GetValue<string>());
        Assert.Equal("active", (await GetCardAsync(disabledRepo))["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task ActivityBadgesClosedNoIssueAsClosed()
    {
        var repo = new InMemoryForensicCaseRepository();
        var now = DateTimeOffset.UtcNow;
        await repo.AddAsync(Case(ForensicCaseStatus.ClosedNoIssue, now.AddMinutes(-5), now.AddMinutes(-4), decision: InspectorDecision.CloseNoIssue));

        var card = await GetCardAsync(repo);

        var entry = card["activity"]!.AsArray().Single()!;
        Assert.Equal("CLOSED", entry["stage"]!.GetValue<string>());
        Assert.Equal("Vendor timeouts on /booking/confirm.", entry["text"]!.GetValue<string>());
        Assert.Equal(InspectorDecision.CloseNoIssue, entry["decision"]!.GetValue<string>());
    }

    [Fact]
    public async Task TheHeartbeatIsCappedAt24AndTheTimelineAt20()
    {
        var repo = new InMemoryForensicCaseRepository();
        var now = DateTimeOffset.UtcNow;
        for (var i = 1; i <= 30; i++)
        {
            await repo.AddAsync(Case(ForensicCaseStatus.Resolved, now.AddMinutes(-i), now.AddMinutes(-i).AddSeconds(30)));
        }

        var card = await GetCardAsync(repo);

        Assert.Equal(24, card["cases"]!.AsArray().Count);
        Assert.Equal(20, card["activity"]!.AsArray().Count);
        // Newest first, so the heartbeat reads left-to-right as "most recent".
        var bars = card["cases"]!.AsArray();
        Assert.True(
            DateTimeOffset.Parse(bars[0]!["startedAt"]!.GetValue<string>())
            > DateTimeOffset.Parse(bars[1]!["startedAt"]!.GetValue<string>()));
    }

    [Fact]
    public async Task ABrokenInspectorRepositoryDegradesItsOwnCardAndNotTheWholeResponse()
    {
        var response = await new GetMonkeysFunction(
            new InMemoryFindingsRepository(),
            new InMemoryDoctorReportsRepository(),
            new InMemoryNotificationLogRepository(),
            new InMemoryInvestigationPackageRepository(),
            new InMemoryScoutRunRepository(),
            new ThrowingForensicCaseRepository(),
            new InMemoryRcaReportRepository(),
            new InMemoryAgentWorkQueue(),
            FakeAgentPauseResolver.NothingPaused(),
            Options.Create(new InspectorOptions()),
            Options.Create(new RcaOptions()),
            new InMemoryMonkeyRunTracker(),
            NullLogger<GetMonkeysFunction>.Instance)
            .RunAsync(new FakeHttpRequestData("https://localhost/api/monkeys"), default);

        var monkeys = response.ReadJson().AsArray();
        // Seven cards now: the RCA Agent joined the roster.
        Assert.Equal(7, monkeys.Count);
        var card = monkeys.Single(m => m!["id"]!.GetValue<string>() == MonkeyKey.InspectorMonkey)!;
        Assert.Empty(card["cases"]!.AsArray());
        Assert.Equal(0, card["inspectorStats"]!["investigations"]!.GetValue<int>());
    }

    private sealed class ThrowingForensicCaseRepository : IForensicCaseRepository
    {
        public Task<ForensicCase> AddAsync(ForensicCase forensicCase, CancellationToken ct = default) => throw new InvalidOperationException("db down");

        public Task<ForensicCase> UpdateAsync(ForensicCase forensicCase, CancellationToken ct = default) => throw new InvalidOperationException("db down");

        public Task UpdatePhaseAsync(Guid caseId, string phase, CancellationToken ct = default) => throw new InvalidOperationException("db down");

        public Task<ForensicCase?> GetByIdAsync(Guid id, CancellationToken ct = default) => throw new InvalidOperationException("db down");

        public Task<ForensicCase?> GetByPackageIdAsync(Guid investigationPackageId, CancellationToken ct = default) => throw new InvalidOperationException("db down");

        public Task<IReadOnlyList<ForensicCase>> GetRecentAsync(int top, string? vertical = null, string? clubId = null, string? profileKey = null, CancellationToken ct = default) => throw new InvalidOperationException("db down");

        public Task<InspectorStats> GetStatsAsync(string? profileKey = null, CancellationToken ct = default) => throw new InvalidOperationException("db down");
    }
}
