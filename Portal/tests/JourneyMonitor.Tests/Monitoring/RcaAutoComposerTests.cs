using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Infrastructure.Monitoring;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Tests.Monitoring;

/// <summary>
/// The rule that decides whether a finished investigation pays for an RCA. Every case here is a
/// provider call that either happens or does not, so each one is worth asserting on.
/// </summary>
public class RcaAutoComposerTests
{
    private static ForensicCase Case(
        string status = ForensicCaseStatus.Escalated,
        string? probableRootCause = "Vendor search API stopped responding") => new()
        {
            InvestigationPackageId = Guid.NewGuid(),
            CaseId = "case-1",
            EntityKey = "host:booking.travorium.com",
            Vertical = "Hotels",
            Stage = "04 Hotel Details",
            Status = status,
            ProbableRootCause = probableRootCause,
            Confidence = 0.55,
            CompletedAt = DateTimeOffset.UtcNow,
        };

    private static (RcaAutoComposer Composer, CountingRcaAgent Agent, IRcaReportRepository Reports) Build(
        RcaOptions? options = null,
        IAgentPauseResolver? pauses = null,
        IRcaReportRepository? reports = null)
    {
        var agent = new CountingRcaAgent();
        var store = reports ?? new InMemoryRcaReportRepository();
        var composer = new RcaAutoComposer(
            agent,
            store,
            pauses ?? FakeAgentPauseResolver.NothingPaused(),
            Options.Create(options ?? new RcaOptions()),
            NullLogger<RcaAutoComposer>.Instance);
        return (composer, agent, store);
    }

    [Fact]
    public async Task ComposesWhenTheCaseEscalatedWithAProbableRootCause()
    {
        var (composer, agent, reports) = Build();
        var forensicCase = Case();

        var report = await composer.ComposeIfEligibleAsync(forensicCase, CancellationToken.None);

        Assert.NotNull(report);
        Assert.Equal(1, agent.Calls);
        Assert.NotNull(await reports.GetByForensicCaseIdAsync(forensicCase.Id, CancellationToken.None));
    }

    [Theory]
    [InlineData(ForensicCaseStatus.ClosedNoIssue)]
    [InlineData(ForensicCaseStatus.Resolved)]
    [InlineData(ForensicCaseStatus.Failed)]
    [InlineData(ForensicCaseStatus.Running)]
    public async Task ComposesNothingForAnyStatusOtherThanEscalated(string status)
    {
        var (composer, agent, _) = Build();

        Assert.Null(await composer.ComposeIfEligibleAsync(Case(status: status), CancellationToken.None));
        Assert.Equal(0, agent.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AnEscalatedCaseWithoutARootCauseIsNotWorthAProviderCall(string? rootCause)
    {
        // These are the cases that closed "insufficient_evidence". An RCA over one of them spends
        // a call to say "we do not know" in eight sections - which the verdict already said.
        var (composer, agent, _) = Build();

        Assert.Null(await composer.ComposeIfEligibleAsync(Case(probableRootCause: rootCause), CancellationToken.None));
        Assert.Equal(0, agent.Calls);
    }

    [Fact]
    public async Task APausedRcaMonkeyComposesNothing()
    {
        var paused = FakeAgentPauseResolver.NothingPaused().Pause(MonkeyKey.RcaMonkey);
        var (composer, agent, _) = Build(pauses: paused);

        Assert.Null(await composer.ComposeIfEligibleAsync(Case(), CancellationToken.None));
        Assert.Equal(0, agent.Calls);
    }

    [Fact]
    public async Task APausedInspectorDoesNotStopTheRcaAgentOnItsOwnCases()
    {
        // The two pauses are independent: a case that finished before the Inspector was paused
        // still deserves its write-up.
        var paused = FakeAgentPauseResolver.NothingPaused().Pause(MonkeyKey.InspectorMonkey);
        var (composer, agent, _) = Build(pauses: paused);

        Assert.NotNull(await composer.ComposeIfEligibleAsync(Case(), CancellationToken.None));
        Assert.Equal(1, agent.Calls);
    }

    [Fact]
    public async Task AnInvestigationThatAlreadyHasAnRcaIsNotComposedAgain()
    {
        // The Inspector re-opens the same case when its queue item is retried. Without this guard
        // every retry would pay for the same document.
        var (composer, agent, _) = Build();
        var forensicCase = Case();

        await composer.ComposeIfEligibleAsync(forensicCase, CancellationToken.None);
        var second = await composer.ComposeIfEligibleAsync(forensicCase, CancellationToken.None);

        Assert.Null(second);
        Assert.Equal(1, agent.Calls);
    }

    [Fact]
    public async Task TurningOffAutoComposeLeavesTheOnDemandRouteAsTheOnlyWayIn()
    {
        var (composer, agent, _) = Build(new RcaOptions { AutoComposeOnEscalation = false });

        Assert.Null(await composer.ComposeIfEligibleAsync(Case(), CancellationToken.None));
        Assert.Equal(0, agent.Calls);
    }

    [Fact]
    public async Task TheMasterSwitchDisablesTheAutomaticRouteToo()
    {
        var (composer, agent, _) = Build(new RcaOptions { Enabled = false });

        Assert.Null(await composer.ComposeIfEligibleAsync(Case(), CancellationToken.None));
        Assert.Equal(0, agent.Calls);
    }

    [Fact]
    public async Task AProviderFailureIsSwallowedBecauseTheInvestigationAlreadySucceeded()
    {
        var composer = new RcaAutoComposer(
            new ThrowingRcaAgent(),
            new InMemoryRcaReportRepository(),
            FakeAgentPauseResolver.NothingPaused(),
            Options.Create(new RcaOptions()),
            NullLogger<RcaAutoComposer>.Instance);

        Assert.Null(await composer.ComposeIfEligibleAsync(Case(), CancellationToken.None));
    }

    [Fact]
    public async Task AnEmptyCompositionIsNotStoredSoARateLimitDoesNotBecomePermanent()
    {
        // This is the real 429: the Inspector burned the quota, the provider returned nothing, and
        // storing that would have left the case looking like it already had its write-up.
        var reports = new InMemoryRcaReportRepository();
        var agent = new FailingRcaAgent();
        var composer = new RcaAutoComposer(
            agent, reports, FakeAgentPauseResolver.NothingPaused(),
            Options.Create(new RcaOptions()), NullLogger<RcaAutoComposer>.Instance);
        var forensicCase = Case();

        Assert.Null(await composer.ComposeIfEligibleAsync(forensicCase, CancellationToken.None));
        Assert.Null(await reports.GetByForensicCaseIdAsync(forensicCase.Id, CancellationToken.None));

        // And because nothing was stored, the next attempt actually runs.
        await composer.ComposeIfEligibleAsync(forensicCase, CancellationToken.None);
        Assert.Equal(2, agent.Calls);
    }

    private sealed class FailingRcaAgent : IRcaAgent
    {
        public int Calls { get; private set; }

        public Task<RcaReport> ComposeAsync(ForensicCase forensicCase, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(new RcaReport
            {
                ForensicCaseId = forensicCase.Id,
                Title = "nothing",
                Error = "The LLM provider returned nothing, so no RCA was written.",
            });
        }
    }

    [Fact]
    public async Task CancellationIsNotSwallowed()
    {
        // Host shutdown has to propagate: swallowing it would let the worker keep composing while
        // the process is being torn down.
        var composer = new RcaAutoComposer(
            new ThrowingRcaAgent(cancel: true),
            new InMemoryRcaReportRepository(),
            FakeAgentPauseResolver.NothingPaused(),
            Options.Create(new RcaOptions()),
            NullLogger<RcaAutoComposer>.Instance);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => composer.ComposeIfEligibleAsync(Case(), cts.Token));
    }

    private sealed class ThrowingRcaAgent(bool cancel = false) : IRcaAgent
    {
        public Task<RcaReport> ComposeAsync(ForensicCase forensicCase, CancellationToken ct = default) =>
            cancel
                ? throw new OperationCanceledException(ct)
                : throw new InvalidOperationException("the provider refused the request");
    }
}
