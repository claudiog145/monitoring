using System.Text.Json;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring;
using JourneyMonitor.Core.Monitoring.Inspector;
using JourneyMonitor.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Core.UseCases;

/// <summary>Result of one on-demand or scheduled monkey run - what the Settings/Simian Army "Run Now" button shows back to the user.</summary>
public sealed record MonkeyRunSummary(string MonkeyKey, int NewFindings, int Diagnosed, int Notified, DateTimeOffset RanAt);

/// <summary>
/// Per-domain detect-then-diagnose cycle for the (legacy, flat-threshold) Latency Spike Monkey -
/// shared by the Timer trigger (<c>LatencySpikeMonkeyFunction</c>) and the on-demand "Run Now"
/// trigger (<c>RunMonkeyNowFunction</c>) so both call the exact same Core logic (see AGENTS.md -
/// business logic never lives in a Function body).
/// </summary>
public sealed class LatencySpikeMonkeyRunner(
    ILatencySpikeDetector detector,
    IDoctorAgent doctorAgent,
    IMonkeyRunTracker runTracker,
    IAgentPauseResolver pauseResolver,
    ILogger<LatencySpikeMonkeyRunner> logger)
{
    public static readonly string[] Domains = ["cars", "hotels", "cruises"]; // Air: KQL not authored yet, see KqlQueryTextProvider.

    // Process-wide (not per-instance: this runner is DI-scoped, a new instance per invocation) so
    // an overlapping Timer tick and a "Run Now" click on the same host instance serialize instead
    // of racing each other past the non-atomic open-finding lookup/insert. Does NOT cover true
    // multi-instance scale-out - see docs/sessions for the distributed-lease follow-up.
    private static readonly SemaphoreSlim RunLock = new(1, 1);

    public async Task<MonkeyRunSummary> RunAsync(CancellationToken ct = default)
    {
        if (!await RunLock.WaitAsync(0, ct))
        {
            logger.LogInformation("[LatencySpikeMonkey] a run is already in progress on this instance - skipping this overlapping invocation.");
            return new MonkeyRunSummary(MonkeyKey.LatencySpikeMonkey, NewFindings: 0, Diagnosed: 0, Notified: 0, DateTimeOffset.UtcNow);
        }
        try
        {
            return await RunCoreAsync(ct);
        }
        finally
        {
            RunLock.Release();
        }
    }

    private async Task<MonkeyRunSummary> RunCoreAsync(CancellationToken ct)
    {
        if (await pauseResolver.IsPausedAsync(MonkeyKey.LatencySpikeMonkey, ct))
        {
            // No detection, and no run recorded: a paused agent that kept refreshing its "last
            // run" timestamp would look alive on the card, which is the lie this feature removes.
            logger.LogInformation("[LatencySpikeMonkey] paused - no detection and no diagnosis this cycle.");
            return new MonkeyRunSummary(MonkeyKey.LatencySpikeMonkey, NewFindings: 0, Diagnosed: 0, Notified: 0, DateTimeOffset.UtcNow);
        }

        await runTracker.RecordRunAsync(MonkeyKey.LatencySpikeMonkey, DateTimeOffset.UtcNow, ct);

        var newFindings = 0;
        var diagnosed = 0;

        foreach (var domain in Domains)
        {
            IReadOnlyList<Finding> findings;
            try
            {
                findings = await detector.RunAsync(domain, "production", ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[LatencySpikeMonkey] detection failed for domain {Domain}", domain);
                continue;
            }

            newFindings += findings.Count;

            foreach (var finding in findings)
            {
                try
                {
                    var report = await doctorAgent.DiagnoseAsync(finding, ct);
                    diagnosed++;
                    logger.LogInformation(
                        "[DoctorMonkey] finding {FindingId} ({Domain}/{Stage}) -> category={Category} confidence={Confidence}",
                        finding.Id, finding.Domain, finding.Stage, report.ErrorCategory, report.Confidence);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "[DoctorMonkey] diagnosis failed for finding {FindingId}", finding.Id);
                }
            }
        }

        return new MonkeyRunSummary(MonkeyKey.LatencySpikeMonkey, newFindings, diagnosed, Notified: 0, DateTimeOffset.UtcNow);
    }
}

/// <summary>
/// Per-domain collect-evaluate-diagnose-notify cycle for the SLO Burn-Rate Monkey - shared by the
/// Timer trigger (<c>SloBurnRateMonkeyFunction</c>) and the on-demand "Run Now" trigger.
/// </summary>
public sealed class SloBurnRateMonkeyRunner(
    GoldenSignalCollector collector,
    IBurnRateDetector detector,
    IDoctorAgent doctorAgent,
    INotificationService notifications,
    IMonkeyRunTracker runTracker,
    IAgentPauseResolver pauseResolver,
    ILogger<SloBurnRateMonkeyRunner> logger)
{
    public static readonly string[] Domains = ["hotels", "cars", "cruises", "air"];

    // Same single-instance overlap guard as LatencySpikeMonkeyRunner - see its RunLock comment.
    private static readonly SemaphoreSlim RunLock = new(1, 1);

    public async Task<MonkeyRunSummary> RunAsync(CancellationToken ct = default)
    {
        if (!await RunLock.WaitAsync(0, ct))
        {
            logger.LogInformation("[SloBurnRateMonkey] a run is already in progress on this instance - skipping this overlapping invocation.");
            return new MonkeyRunSummary(MonkeyKey.SloBurnRateMonkey, NewFindings: 0, Diagnosed: 0, Notified: 0, DateTimeOffset.UtcNow);
        }
        try
        {
            return await RunCoreAsync(ct);
        }
        finally
        {
            RunLock.Release();
        }
    }

    private async Task<MonkeyRunSummary> RunCoreAsync(CancellationToken ct)
    {
        if (await pauseResolver.IsPausedAsync(MonkeyKey.SloBurnRateMonkey, ct))
        {
            // Collection is skipped too, not just evaluation: a paused agent must not keep
            // writing golden-signal samples that a later unpause would then evaluate as history.
            logger.LogInformation("[SloBurnRateMonkey] paused - no collection, evaluation or notification this cycle.");
            return new MonkeyRunSummary(MonkeyKey.SloBurnRateMonkey, NewFindings: 0, Diagnosed: 0, Notified: 0, DateTimeOffset.UtcNow);
        }

        await runTracker.RecordRunAsync(MonkeyKey.SloBurnRateMonkey, DateTimeOffset.UtcNow, ct);

        var newFindings = 0;
        var diagnosed = 0;
        var notified = 0;

        foreach (var domain in Domains)
        {
            try
            {
                await collector.CollectAsync(domain, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[SloBurnRateMonkey] golden signal collection failed for domain {Domain}", domain);
            }

            IReadOnlyList<Finding> findings;
            try
            {
                findings = await detector.RunAsync(domain, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[SloBurnRateMonkey] burn-rate evaluation failed for domain {Domain}", domain);
                continue;
            }

            if (findings.Count == 0) continue;
            newFindings += findings.Count;
            logger.LogInformation("[SloBurnRateMonkey] {Count} new finding(s) for domain {Domain}", findings.Count, domain);

            foreach (var finding in findings)
            {
                try
                {
                    var report = await doctorAgent.DiagnoseAsync(finding, ct);
                    diagnosed++;
                    logger.LogInformation(
                        "[DoctorMonkey] finding {FindingId} ({Domain}/{Stage}) -> category={Category} confidence={Confidence}",
                        finding.Id, finding.Domain, finding.Stage, report.ErrorCategory, report.Confidence);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "[DoctorMonkey] diagnosis failed for finding {FindingId}", finding.Id);
                }

                if (!IsPageSeverity(finding)) continue;

                try
                {
                    // NotifyAsync returns real deliveries, not attempts, so a disabled or failing
                    // channel still cannot inflate what the "Run Now" summary reports.
                    notified += await notifications.NotifyAsync(FindingNotificationRequestFactory.From(finding), ct);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "[SloBurnRateMonkey] notification fan-out failed for finding {FindingId}", finding.Id);
                }
            }
        }

        return new MonkeyRunSummary(MonkeyKey.SloBurnRateMonkey, newFindings, diagnosed, notified, DateTimeOffset.UtcNow);
    }

    private static bool IsPageSeverity(Finding finding)
    {
        try
        {
            using var doc = JsonDocument.Parse(finding.EvidenceJson);
            return doc.RootElement.TryGetProperty("alertSeverity", out var s) && s.GetString() == Domain.AlertSeverity.Page;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

/// <summary>
/// On-demand Doctor Monkey sweep: diagnoses any OPEN finding that somehow has no
/// <see cref="DoctorReport"/> yet (normally every finding is diagnosed synchronously the moment
/// it's created by <see cref="LatencySpikeMonkeyRunner"/>/<see cref="SloBurnRateMonkeyRunner"/> -
/// this exists for the "Run Now" button and for self-healing a past diagnosis failure that was
/// logged and swallowed at the time). Unlike the other two monkeys, the Doctor Monkey has no
/// Timer trigger of its own - it only ever runs inline as part of the other two, or here.
/// </summary>
public sealed class DoctorMonkeyRunner(
    IFindingsRepository findingsRepository,
    IDoctorReportsRepository doctorReportsRepository,
    IDoctorAgent doctorAgent,
    IMonkeyRunTracker runTracker,
    IAgentPauseResolver pauseResolver,
    ILogger<DoctorMonkeyRunner> logger)
{
    // Same single-instance overlap guard as LatencySpikeMonkeyRunner - see its RunLock comment.
    private static readonly SemaphoreSlim RunLock = new(1, 1);

    public async Task<MonkeyRunSummary> RunAsync(CancellationToken ct = default)
    {
        if (!await RunLock.WaitAsync(0, ct))
        {
            logger.LogInformation("[DoctorMonkeyRunner] a run is already in progress on this instance - skipping this overlapping invocation.");
            return new MonkeyRunSummary(MonkeyKey.DoctorMonkey, NewFindings: 0, Diagnosed: 0, Notified: 0, DateTimeOffset.UtcNow);
        }
        try
        {
            return await RunCoreAsync(ct);
        }
        finally
        {
            RunLock.Release();
        }
    }

    private async Task<MonkeyRunSummary> RunCoreAsync(CancellationToken ct)
    {
        if (await pauseResolver.IsPausedAsync(MonkeyKey.DoctorMonkey, ct))
        {
            // Stops this sweep only. The other two runners still diagnose their own new findings
            // inline via IDoctorAgent - see the pause gap noted in docs/sessions.
            logger.LogInformation("[DoctorMonkeyRunner] paused - the undiagnosed-findings sweep did nothing this cycle.");
            return new MonkeyRunSummary(MonkeyKey.DoctorMonkey, NewFindings: 0, Diagnosed: 0, Notified: 0, DateTimeOffset.UtcNow);
        }

        var findings = await findingsRepository.GetAllAsync(ct);
        var reports = await doctorReportsRepository.GetAllAsync(ct);
        var diagnosedFindingIds = reports.Select(r => r.FindingId).ToHashSet();
        var undiagnosed = findings.Where(f => f.Status == "open" && !diagnosedFindingIds.Contains(f.Id)).ToList();

        var diagnosed = 0;
        foreach (var finding in undiagnosed)
        {
            try
            {
                await doctorAgent.DiagnoseAsync(finding, ct); // Records its own MonkeyKey.DoctorMonkey run internally.
                diagnosed++;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[DoctorMonkeyRunner] diagnosis failed for finding {FindingId}", finding.Id);
            }
        }

        if (undiagnosed.Count == 0)
        {
            // DiagnoseAsync already records a run for every finding it processes above - only
            // needed here for the "nothing to do" case, so "Run Now" still reflects that it ran.
            await runTracker.RecordRunAsync(MonkeyKey.DoctorMonkey, DateTimeOffset.UtcNow, ct);
        }

        return new MonkeyRunSummary(MonkeyKey.DoctorMonkey, NewFindings: 0, diagnosed, Notified: 0, DateTimeOffset.UtcNow);
    }
}

/// <summary>
/// One scout detection cycle - shared by both scouts' Timer triggers and the "Run Now" button,
/// like every other runner here. The real work lives in <see cref="ScoutMonkeyService"/>; this
/// wrapper only adds the single-instance overlap guard and maps the cycle onto the shared
/// <see cref="MonkeyRunSummary"/> shape the UI already understands, where NewFindings is the
/// number of InvestigationPackages handed to L3.
///
/// One instance serves both scouts; the board is a parameter. The overlap guard is PER BOARD on
/// purpose - a shared lock would make the two scouts take turns, which is the opposite of why
/// they were split.
/// </summary>
public sealed class ScoutMonkeyRunner(
    ScoutMonkeyService scout,
    IMonkeyRunTracker runTracker,
    IAgentPauseResolver pauseResolver,
    ILogger<ScoutMonkeyRunner> logger)
{
    // Same single-instance overlap guard as LatencySpikeMonkeyRunner - see its RunLock comment.
    // It matters more here: a cycle takes ~45-50s (one cross-resource board query) against a 5
    // minute timer, so a slow run and the next tick can genuinely overlap.
    private static readonly IReadOnlyDictionary<string, SemaphoreSlim> RunLocks =
        ScoutBoard.All.ToDictionary(b => b, _ => new SemaphoreSlim(1, 1), StringComparer.OrdinalIgnoreCase);

    public async Task<MonkeyRunSummary> RunAsync(string board, CancellationToken ct = default)
    {
        var monkeyKey = ScoutBoard.MonkeyKeyFor(board);
        var gate = RunLocks[board];

        if (!await gate.WaitAsync(0, ct))
        {
            logger.LogInformation("[{Monkey}] a run is already in progress on this instance - skipping this overlapping invocation.", monkeyKey);
            return Nothing(monkeyKey);
        }
        try
        {
            if (await pauseResolver.IsPausedAsync(monkeyKey, ct))
            {
                // ScoutMonkeyService is never entered, so no ScoutRun row is written at all.
                // The scout's own Enabled=false has a different meaning (a recorded "disabled"
                // cycle) and is left exactly as it was - this is a second, independent switch.
                logger.LogInformation("[{Monkey}] paused - no board query and no cycle recorded.", monkeyKey);
                return Nothing(monkeyKey);
            }

            await runTracker.RecordRunAsync(monkeyKey, DateTimeOffset.UtcNow, ct);
            var run = await scout.RunAsync(board, ct);
            return new MonkeyRunSummary(monkeyKey, run.PackagesCreated, Diagnosed: 0, Notified: 0, DateTimeOffset.UtcNow);
        }
        finally
        {
            gate.Release();
        }
    }

    private static MonkeyRunSummary Nothing(string monkeyKey) =>
        new(monkeyKey, NewFindings: 0, Diagnosed: 0, Notified: 0, DateTimeOffset.UtcNow);
}

/// <summary>
/// The Inspector Monkey's consumer loop: claim work off the shared queue, investigate it, and
/// report the outcome back to the queue. Shared by its Timer trigger and the "Run Now" button like
/// every other runner here; the investigation itself lives in <see cref="InspectorMonkeyService"/>.
///
/// What this class is actually responsible for is spend and lease hygiene, in that order:
/// - The daily cap is checked BEFORE anything is leased. At the cap the run claims nothing, which
///   is the only thing that bounds what this agent costs - one investigation is ~20-25 completions
///   against much larger prompts than the Scout's.
/// - Every item it leases is Completed or Failed, including when the investigation throws and
///   including on cancellation. An item left "leased" past its attempt limit is claimed by nobody
///   and returned by no query - see <see cref="ReportBacklogAsync"/> for why this runner cannot
///   clean that up afterwards.
/// </summary>
public sealed class InspectorMonkeyRunner(
    IAgentWorkQueue queue,
    IInvestigationPackageRepository packages,
    IForensicCaseRepository cases,
    InspectorMonkeyService inspector,
    IMonkeyRunTracker runTracker,
    IAgentPauseResolver pauseResolver,
    IAgentBudgetResolver budgetResolver,
    IOptions<InspectorOptions> options,
    ILogger<InspectorMonkeyRunner> logger)
{
    // Same single-instance overlap guard as LatencySpikeMonkeyRunner - see its RunLock comment.
    // The queue's atomic claim already prevents two INSTANCES taking the same item; this prevents
    // one instance leasing a second batch while the first is still running, which would multiply
    // the configured concurrency by however many ticks overlap.
    private static readonly SemaphoreSlim RunLock = new(1, 1);

    public async Task<MonkeyRunSummary> RunAsync(CancellationToken ct = default)
    {
        if (!await RunLock.WaitAsync(0, ct))
        {
            logger.LogInformation("[InspectorMonkey] a run is already in progress on this instance - skipping this overlapping invocation.");
            return Nothing();
        }
        try
        {
            return await RunCoreAsync(ct);
        }
        finally
        {
            RunLock.Release();
        }
    }

    private async Task<MonkeyRunSummary> RunCoreAsync(CancellationToken ct)
    {
        // Checked before anything is leased: a leased item is an item nobody else can take, and
        // an investigation is ~20-25 LLM completions. A paused Inspector must cost zero, not
        // "one batch that was already in flight".
        if (await pauseResolver.IsPausedAsync(MonkeyKey.InspectorMonkey, ct))
        {
            logger.LogInformation("[InspectorMonkey] paused - nothing leased, nothing spent; the queue keeps filling.");
            return Nothing();
        }

        var limits = options.Value;
        await runTracker.RecordRunAsync(MonkeyKey.InspectorMonkey, DateTimeOffset.UtcNow, ct);

        if (!limits.ResolvedEnabled)
        {
            // The Scout keeps producing while this is off, so the backlog is waiting rather than lost.
            logger.LogInformation("[InspectorMonkey] Inspector:Enabled is false - nothing leased; the queue keeps filling.");
            return Nothing();
        }

        // Named argument: GetStatsAsync now leads with an optional profile filter, and the cap
        // below is a per-agent ceiling that is not scoped to one company.
        var stats = await cases.GetStatsAsync(ct: ct);
        // The cap is the only hard spend ceiling this agent has, so it is read from the resolver:
        // lowering it must not require a redeploy, which is how 1.6M tokens went out unattended.
        var budget = await budgetResolver.ResolveInspectorAsync(ct);
        var remaining = budget.MaxInvestigationsPerDay - stats.InvestigationsToday;
        if (remaining <= 0)
        {
            logger.LogWarning(
                "[InspectorMonkey] daily cap reached ({Today} of {Cap} investigations today) - leasing nothing until the UTC day rolls over.",
                stats.InvestigationsToday, budget.MaxInvestigationsPerDay);
            return Nothing();
        }

        await ReportBacklogAsync(ct);

        // One identifier per run, reused for every Complete/Fail: the queue fences those writes on
        // it, so a different string would silently fail to release the lease this run took.
        var leasedBy = $"{Environment.MachineName}:{Guid.NewGuid():N}";
        var leaseDuration = TimeSpan.FromMinutes(limits.ResolvedLeaseMinutes);
        var batchSize = Math.Min(limits.ResolvedConcurrency, remaining);

        var leased = new List<AgentWorkItem>();
        for (var i = 0; i < batchSize; i++)
        {
            var item = await queue.TryLeaseAsync(AgentWorkPayloadType.InvestigationPackage, leasedBy, leaseDuration, ct);
            if (item is null) break;
            leased.Add(item);
        }

        if (leased.Count == 0)
        {
            logger.LogInformation("[InspectorMonkey] nothing to investigate this cycle.");
            return Nothing();
        }

        var outcomes = await Task.WhenAll(leased.Select(item => InvestigateOneAsync(item, leasedBy, ct)));
        var investigated = outcomes.Count(ok => ok);

        logger.LogInformation(
            "[InspectorMonkey] {Investigated} of {Leased} leased item(s) investigated ({Today} of {Cap} used today).",
            investigated, leased.Count, stats.InvestigationsToday + leased.Count, limits.ResolvedMaxInvestigationsPerDay);

        return new MonkeyRunSummary(MonkeyKey.InspectorMonkey, leased.Count, investigated, Notified: 0, DateTimeOffset.UtcNow);
    }

    private async Task<bool> InvestigateOneAsync(AgentWorkItem item, string leasedBy, CancellationToken ct)
    {
        try
        {
            if (!Guid.TryParse(item.PayloadId, out var packageId))
            {
                await ReleaseAsFailedAsync(item, leasedBy, $"PayloadId \"{item.PayloadId}\" is not an investigation package id.", ct);
                return false;
            }

            var package = await packages.GetByIdAsync(packageId, ct);
            if (package is null)
            {
                await ReleaseAsFailedAsync(item, leasedBy, $"investigation package {packageId} no longer exists.", ct);
                return false;
            }

            var forensicCase = await inspector.InvestigateAsync(package, ct);
            if (forensicCase.Status == ForensicCaseStatus.Failed)
            {
                // The case already carries the error; the item goes back so a transient provider
                // outage is retried rather than silently swallowed as an investigated package.
                await ReleaseAsFailedAsync(item, leasedBy, forensicCase.ErrorMessage ?? "the investigation failed without a message.", ct);
                return false;
            }

            await queue.CompleteAsync(item.Id, leasedBy, ct);
            return true;
        }
        catch (Exception ex)
        {
            // Cancellation included deliberately, and reported with a token that is not cancelled:
            // a shutdown that skips this leaves the item "leased", and once its attempts are gone
            // it is eligible for nothing and returned by no query - a permanent hole in the backlog.
            logger.LogError(ex, "[InspectorMonkey] work item {ItemId} ({DedupKey}) failed.", item.Id, item.DedupKey);
            await ReleaseAsFailedAsync(item, leasedBy, ex.Message, CancellationToken.None);
            return false;
        }
    }

    private async Task ReleaseAsFailedAsync(AgentWorkItem item, string leasedBy, string error, CancellationToken ct)
    {
        try
        {
            await queue.FailAsync(item.Id, leasedBy, Truncate(error, 1000), ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[InspectorMonkey] could not release the lease on work item {ItemId} - it will only return when the lease expires.", item.Id);
        }
    }

    /// <summary>
    /// Logs the backlog counters, and is as close to a stuck-item sweep as this runner can get.
    ///
    /// An item whose consumer died without calling FailAsync stays "leased" with an expired lease.
    /// Once Attempts has reached MaxAttempts the claim predicate excludes it, so it is not returned
    /// by TryLeaseAsync nor by GetQueuedAsync, and GetStatsAsync counts it under neither Queued (it
    /// is not eligible) nor Leased (its lease expired). It is therefore invisible: there is no
    /// method on IAgentWorkQueue that can see it, let alone mark it abandoned. The real mitigation
    /// is upstream - this runner always Completes or Fails what it leases - but a hard process kill
    /// still cannot be cleaned up without a new queue method (something like
    /// AbandonExpiredAsync(payloadType)). That is a contract change and is not invented here.
    /// </summary>
    private async Task ReportBacklogAsync(CancellationToken ct)
    {
        try
        {
            var stats = await queue.GetStatsAsync(AgentWorkPayloadType.InvestigationPackage, ct);
            logger.LogInformation(
                "[InspectorMonkey] backlog: {Queued} queued, {Leased} in flight, {Done} done today, {Failed} failed today, {Abandoned} abandoned, oldest queued {Oldest}.",
                stats.Queued, stats.Leased, stats.DoneToday, stats.FailedToday, stats.AbandonedTotal,
                stats.OldestQueuedAt?.ToString("O") ?? "(none)");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "[InspectorMonkey] could not read the queue backlog.");
        }
    }

    private static MonkeyRunSummary Nothing() =>
        new(MonkeyKey.InspectorMonkey, NewFindings: 0, Diagnosed: 0, Notified: 0, DateTimeOffset.UtcNow);

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
