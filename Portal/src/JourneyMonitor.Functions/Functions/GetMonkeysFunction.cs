using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring;
using JourneyMonitor.Core.Options;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Functions.Functions;

/// <summary>
/// GET /api/monkeys — real backend for the 2 new agents in the "Simian Army" tab
/// (latency-spike-monkey, doctor-monkey), computed from the real Findings/DoctorReports
/// repositories instead of the static wwwroot/data/monkeys.json mock (see the implementation
/// plan, Phase 3). Shaped exactly like a monkeys.json entry so app.js needs no rendering changes.
/// The rest of the Simian Army roster is out of this MVP's scope and stays mocked in
/// data/monkeys.json - app.js merges both arrays (see getMonkeys()).
/// </summary>
public sealed class GetMonkeysFunction(
    IFindingsRepository findingsRepository,
    IDoctorReportsRepository doctorReportsRepository,
    INotificationLogRepository notificationLogRepository,
    IInvestigationPackageRepository investigationPackages,
    IScoutRunRepository scoutRuns,
    IForensicCaseRepository forensicCases,
    IRcaReportRepository rcaReports,
    IAgentWorkQueue agentWorkQueue,
    IAgentPauseResolver pauseResolver,
    IOptions<InspectorOptions> inspectorOptions,
    IOptions<RcaOptions> rcaOptions,
    IMonkeyRunTracker runTracker,
    ILogger<GetMonkeysFunction> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Function("GetMonkeys")]
    public async Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "monkeys")] HttpRequestData req,
        CancellationToken ct)
    {
        try
        {
            var findings = await findingsRepository.GetAllAsync(ct);
            var latencySpikeFindings = findings.Where(f => f.OriginMonkeyKey == MonkeyKey.LatencySpikeMonkey).ToList();
            var sloBurnRateFindings = findings.Where(f => f.OriginMonkeyKey == MonkeyKey.SloBurnRateMonkey).ToList();

            // Every DoctorReport in this system diagnoses a LatencySpikeMonkey finding, so the
            // doctor's activity feed can just read them all in one call instead of looking up
            // each finding's report individually (that was an O(n) GetByFindingIdAsync call per
            // finding, i.e. an N+1 pattern against the in-memory repo's own O(n) linear scan).
            var doctorReports = await doctorReportsRepository.GetAllAsync(ct);

            // One lookup covering every finding, built once - same N+1-avoidance reasoning as
            // doctorReports above (GetBySourceAsync per finding would be one SQL round trip per
            // item in the activity feed instead of a single GetAllAsync).
            var notificationLogsByFindingId = (await notificationLogRepository.GetAllAsync(ct))
                .Where(n => n.SourceType == NotificationSourceType.Finding)
                .GroupBy(n => n.SourceId)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<NotificationLogEntry>)g.OrderByDescending(n => n.SentAt).ToList());

            // One snapshot for all five cards. A paused agent reports status "paused" here, which
            // is the half of the pause feature the user can actually see - the other half is the
            // runners refusing to work (see MonkeyRunners).
            var paused = await pauseResolver.GetAllAsync(ct);
            bool IsPaused(string monkeyKey) => paused.TryGetValue(monkeyKey, out var p) && p;

            // Absent means every company - without it these cards would add two clients' cycles,
            // packages and token totals into one number and call it the agent's work.
            var profileKey = ReadQuery(req, "profileKey");

            var monkeys = new object[]
            {
                BuildScoutMonkey(
                    ScoutBoard.Latency,
                    await investigationPackages.GetRecentAsync(20, ScoutBoard.Latency, profileKey, ct),
                    await scoutRuns.GetRecentAsync(24, ScoutBoard.Latency, profileKey, ct),
                    await scoutRuns.GetStatsAsync(ScoutBoard.Latency, profileKey, ct),
                    await runTracker.GetLastRunAsync(MonkeyKey.LatencyScoutMonkey, ct),
                    IsPaused(MonkeyKey.LatencyScoutMonkey)),
                BuildScoutMonkey(
                    ScoutBoard.Exception,
                    await investigationPackages.GetRecentAsync(20, ScoutBoard.Exception, profileKey, ct),
                    await scoutRuns.GetRecentAsync(24, ScoutBoard.Exception, profileKey, ct),
                    await scoutRuns.GetStatsAsync(ScoutBoard.Exception, profileKey, ct),
                    await runTracker.GetLastRunAsync(MonkeyKey.ExceptionScoutMonkey, ct),
                    IsPaused(MonkeyKey.ExceptionScoutMonkey)),
                await BuildInspectorMonkeyAsync(IsPaused(MonkeyKey.InspectorMonkey), ReadQuery(req, "domain"), ReadQuery(req, "club"), profileKey, ct),
                await BuildRcaMonkeyAsync(IsPaused(MonkeyKey.RcaMonkey), ct),
                BuildLatencySpikeMonkey(latencySpikeFindings, await runTracker.GetLastRunAsync(MonkeyKey.LatencySpikeMonkey, ct), doctorReports, IsPaused(MonkeyKey.LatencySpikeMonkey)),
                BuildSloBurnRateMonkey(sloBurnRateFindings, await runTracker.GetLastRunAsync(MonkeyKey.SloBurnRateMonkey, ct), doctorReports, IsPaused(MonkeyKey.SloBurnRateMonkey)),
                BuildDoctorMonkey(doctorReports, await runTracker.GetLastRunAsync(MonkeyKey.DoctorMonkey, ct), findings, notificationLogsByFindingId, IsPaused(MonkeyKey.DoctorMonkey)),
            };

            return await WriteJsonAsync(req, HttpStatusCode.OK, monkeys);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GetMonkeys failed unexpectedly");
            return await WriteJsonAsync(req, HttpStatusCode.InternalServerError, new { error = "Unexpected server error." });
        }
    }

    /// <summary>
    /// The RCA Agent has no timer: the Inspector hands it a case the moment one closes escalated
    /// with a probable root cause, so the card reports what it has written rather than when it
    /// will next run.
    ///
    /// Its headline number is the share of statements backed by evidence, not a success rate. A
    /// model cannot inflate it: the downgrade rules live in RcaAgentService, so a case that
    /// verified nothing produces a document of zero percent no matter how confident the prose is.
    /// </summary>
    private async Task<object> BuildRcaMonkeyAsync(bool paused, CancellationToken ct)
    {
        IReadOnlyList<RcaReport> reports = [];
        try
        {
            reports = await rcaReports.GetRecentAsync(25, ct);
        }
        catch (Exception ex)
        {
            // Same containment as the Inspector card: one broken repository must not take the
            // whole roster down.
            logger.LogWarning(ex, "Could not read RCA reports; the RCA Agent card will render empty.");
        }

        var statements = reports.SelectMany(r => r.Sections.SelectMany(s => s.Statements)).ToList();
        var factCount = statements.Count(s => s.Confidence == RcaConfidence.Fact);
        var evidenceRate = statements.Count == 0 ? 0d : (double)factCount / statements.Count;

        var lastGenerated = reports.Count > 0 ? reports.Max(r => r.GeneratedAt) : (DateTimeOffset?)null;

        return new
        {
            id = MonkeyKey.RcaMonkey,
            name = "RCA Agent",
            purpose = "Writes the publishable Root Cause Analysis for a finished investigation, in the template this organisation already uses. It does not investigate - it composes from what the Inspector concluded, and labels every statement as measured, derived or inferred so a reader knows what can be quoted.",
            mode = "Observe",
            status = paused ? "paused" : "active",
            paused,
            risk = "low",
            lastRun = lastGenerated?.ToString("O"),
            // No timer by design, so there is no next run to count down to. Null rather than a
            // fabricated schedule the UI would display as if the agent were about to wake up.
            nextRun = (string?)null,
            onDemand = true,
            // Whether the Inspector hands cases over by itself. The pipeline draws the wire from
            // this: a dashed, unfed wire when it is off is the honest picture, and so is a live
            // one when it is on.
            autoCompose = rcaOptions.Value.IsEnabled && rcaOptions.Value.IsAutoComposeEnabled,
            findings = reports.Count,
            actionsProposed = 0,
            actionsExecuted = 0,
            successRate = evidenceRate,
            impactPrevented = "n/a (observe only)",
            guardrails = new
            {
                maxAffectedInstances = 0,
                allowedEnvironments = new[] { "production" },
                maintenanceWindowOnly = false,
                approvalThreshold = "n/a (observe only)",
                rollbackAvailable = false,
                auditLogging = true,
            },
            rcaStats = new
            {
                documents = reports.Count,
                statements = statements.Count,
                factStatements = factCount,
                derivedStatements = statements.Count(s => s.Confidence == RcaConfidence.Derived),
                inferenceStatements = statements.Count(s => s.Confidence == RcaConfidence.Inference),
                failed = reports.Count(r => !r.IsAvailable),
                tokensIn = reports.Sum(r => r.TokensIn),
                tokensOut = reports.Sum(r => r.TokensOut),
            },
            activity = reports.Take(10).Select(r => new
            {
                id = r.Id,
                forensicCaseId = r.ForensicCaseId,
                time = r.GeneratedAt.ToString("O"),
                type = "rca_composed",
                text = r.IsAvailable
                    ? $"Wrote the RCA for {r.Title}"
                    : $"Could not write the RCA for {r.Title}",
                domain = r.Domain,
                stage = r.Stage,
                modelUsed = r.ModelUsed,
                sections = r.Sections.Count,
                requiresHuman = r.RequiresHuman,
                errorMessage = r.Error,
            }),
        };
    }

    /// <summary>
    /// The Inspector reads from three sources the other cards do not touch (cases, the shared
    /// queue, and its own cost settings). A failure in any of them degrades this card to an empty
    /// one rather than taking /api/monkeys - and with it the other four agents - down with it.
    /// </summary>
    private async Task<object> BuildInspectorMonkeyAsync(bool paused, string? domain, string? club, string? profileKey, CancellationToken ct)
    {
        IReadOnlyList<ForensicCase> cases = [];
        var stats = InspectorStats.Empty;
        var queueStats = AgentWorkQueueStats.Empty;
        DateTimeOffset? trackedLastRun = null;

        try
        {
            // 24 is the heartbeat width - one bar per investigation, same as the Scout's cycles.
            cases = await forensicCases.GetRecentAsync(24, domain, club, profileKey, ct);
            stats = await forensicCases.GetStatsAsync(profileKey, ct);
            queueStats = await agentWorkQueue.GetStatsAsync(AgentWorkPayloadType.InvestigationPackage, ct);
            trackedLastRun = await runTracker.GetLastRunAsync(MonkeyKey.InspectorMonkey, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Inspector Monkey card degraded - its repositories could not be read.");
        }

        return BuildInspectorMonkey(cases, stats, queueStats, inspectorOptions.Value, trackedLastRun, paused, domain, club);
    }

    /// <summary>
    /// A detection-phase card - one per scout. Unlike the other cards, its activity feed is not
    /// Findings but InvestigationPackages - the handoffs it has prepared for L3 - and its status
    /// reflects the last cycle honestly: a failed board query (expired credentials, missing RBAC on
    /// one of the three Application Insights resources) shows as "error" instead of looking like a
    /// quiet period, and a scout disabled by configuration shows as "paused" instead of dead.
    /// </summary>
    private static object BuildScoutMonkey(
        string board,
        IReadOnlyList<InvestigationPackage> packages,
        IReadOnlyList<ScoutRun> runs,
        ScoutStats stats,
        DateTimeOffset? trackedLastRun,
        bool paused)
    {
        var isLatency = board == ScoutBoard.Latency;

        // A cycle takes ~50s. Anything still "running" well past that was killed mid-flight (a
        // restart, a crash) and its row will never be closed - treating it as in-flight would
        // leave the card saying "Analyzing now" forever.
        var abandonedBefore = DateTimeOffset.UtcNow.AddMinutes(-10);
        bool IsAbandoned(ScoutRun r) => r.CompletedAt is null && r.StartedAt < abandonedBefore;

        var inFlight = runs.FirstOrDefault(r => r.CompletedAt is null && r.Status == ScoutRunStatus.Running && !IsAbandoned(r));
        var lastCompleted = runs.FirstOrDefault(r => r.CompletedAt is not null);
        var lastRun = trackedLastRun ?? lastCompleted?.StartedAt ?? packages.FirstOrDefault()?.CreatedAt ?? DateTimeOffset.UtcNow;

        // The pause wins over every derived status: it is the one thing a human asserted, and the
        // runner really does nothing while it holds.
        var status = paused ? "paused" : lastCompleted?.Status switch
        {
            ScoutRunStatus.Failed => "error",
            ScoutRunStatus.Disabled => "paused",
            _ => "active",
        };

        return new
        {
            id = ScoutBoard.MonkeyKeyFor(board),
            name = isLatency ? "Latency Scout Monkey" : "Exception Scout Monkey",
            purpose = isLatency
                ? "Watches the per-club latency priority board across Hotels, Cars, Cruise and shared traffic, and hands the L3 team a self-contained investigation package for anything worth a deeper look."
                : "Watches the per-club exception priority board across Hotels, Cars, Cruise and shared traffic, and hands the L3 team a self-contained investigation package for anything worth a deeper look.",
            board,
            mode = "Observe",
            status,
            paused,
            risk = "low",
            lastRun = lastRun.ToString("O"),
            nextRun = lastRun.AddMinutes(5).ToString("O"),
            findings = packages.Count,
            actionsProposed = 0,
            actionsExecuted = 0,
            successRate = 1.0,
            impactPrevented = "n/a (observe only)",
            guardrails = new
            {
                maxAffectedInstances = 0,
                allowedEnvironments = new[] { "production" },
                maintenanceWindowOnly = false,
                approvalThreshold = "n/a (observe only)",
                rollbackAvailable = false,
                auditLogging = true,
            },
            lastCycle = lastCompleted is null ? null : new
            {
                id = lastCompleted.Id,
                status = lastCompleted.Status,
                startedAt = lastCompleted.StartedAt.ToString("O"),
                boardRowsEvaluated = lastCompleted.BoardRowsEvaluated,
                clubsWatched = lastCompleted.ClubsWatched,
                candidatesFound = lastCompleted.CandidatesFound,
                packagesCreated = lastCompleted.PackagesCreated,
                packagesSkippedDuplicate = lastCompleted.PackagesSkippedDuplicate,
                packagesSkippedCap = lastCompleted.PackagesSkippedCap,
                packagesVetoedByScout = lastCompleted.PackagesVetoedByScout,
                durationMs = lastCompleted.DurationMs,
                errorMessage = lastCompleted.ErrorMessage,
            },
            // What the agent is doing right now. Null between cycles - the card then shows the
            // countdown to the next one instead.
            // Only the counters UpdatePhaseAsync actually persists mid-cycle are exposed here -
            // candidate and package counts are written at the end, so they would always read 0.
            currentCycle = inFlight is null ? null : new
            {
                startedAt = inFlight.StartedAt.ToString("O"),
                phase = inFlight.Phase,
                phases = ScoutRunPhase.PhasesFor(board)
                    .Select(p => new { key = p, weight = ScoutRunPhase.WeightsFor(board)[p] })
                    .ToArray(),
                boardRowsEvaluated = inFlight.BoardRowsEvaluated,
                clubsWatched = inFlight.ClubsWatched,
            },
            // One entry per recorded cycle, newest first. This is the heartbeat: a static card has
            // no history, so a periodic run of real timestamps is what proves the agent is alive.
            cycles = runs.Select(r => new
            {
                id = r.Id,
                startedAt = r.StartedAt.ToString("O"),
                status = IsAbandoned(r) ? ScoutRunStatus.Failed : (r.CompletedAt is null ? ScoutRunStatus.Running : r.Status),
                durationMs = r.DurationMs,
                boardRowsEvaluated = r.BoardRowsEvaluated,
                clubsWatched = r.ClubsWatched,
                candidatesFound = r.CandidatesFound,
                packagesCreated = r.PackagesCreated,
                packagesSkippedDuplicate = r.PackagesSkippedDuplicate,
                // Which ones were recognised, not just how many - a cycle that handed off nothing
                // still did the work, and the reader needs to see what it matched against.
                skippedCaseIds = ParseJsonOrNull(r.SkippedCaseIdsJson),
                errorMessage = IsAbandoned(r) ? r.ErrorMessage ?? "The cycle was interrupted and never completed." : r.ErrorMessage,
            }).ToArray(),
            stats = ToStatsDto(stats),
            // The cadence the Timer trigger actually uses (see ScoutMonkeyFunctions) - the UI counts
            // down against it rather than hardcoding 5 minutes of its own.
            cycleMinutes = 5,
            activity = packages
                .OrderByDescending(p => p.CreatedAt)
                .Take(20)
                .Select(p =>
                {
                    var assessment = ParseJsonOrNull(p.AssessmentJson);
                    var symptom = assessment?["symptom"]?.GetValue<string>();
                    var brand = p.ClubName ?? p.BrandName ?? p.EntityKey;
                    return new
                    {
                        time = p.CreatedAt.ToString("O"),
                        stage = "handoff",
                        text = $"{p.Priority} — {brand} · {p.Vertical} · {p.Stage}",
                        packageId = p.Id,
                        // Lets the timeline be filtered down to a single cycle from the heartbeat.
                        scoutRunId = p.ScoutRunId,
                        caseId = p.CaseId,
                        // The Scout's own words when the LLM was available; null (not a fabricated
                        // sentence) when it was not - the UI shows the board's Reason instead.
                        symptom,
                        reason = p.Reason,
                        club = new
                        {
                            entityKey = p.EntityKey,
                            entityKind = p.EntityKind,
                            clubId = p.ClubId,
                            icePartnerId = p.IcePartnerId,
                            brandName = p.BrandName,
                            clubName = p.ClubName,
                            resolvedBy = p.ClubResolvedBy,
                        },
                        impact = new
                        {
                            requests = p.RequestCount,
                            failedRequests = p.FailedRequestCount,
                            exceptions = p.ExceptionCount,
                            affectedUsers = p.AffectedUserCount,
                        },
                        // Measurement and inference stay separately addressable all the way to the UI.
                        evidence = ParseJsonOrNull(p.DeterministicJson),
                        assessment,
                    };
                })
                .ToArray(),
        };
    }

    private static object ToStatsDto(ScoutStats s) => new
    {
        cycles = s.Cycles,
        clearCycles = s.ClearCycles,
        detectedCycles = s.DetectedCycles,
        failedCycles = s.FailedCycles,
        boardRowsEvaluated = s.BoardRowsEvaluated,
        candidatesFound = s.CandidatesFound,
        packagesCreated = s.PackagesCreated,
        duplicatesAvoided = s.DuplicatesAvoided,
        maxClubsWatched = s.MaxClubsWatched,
        llmCalls = s.LlmCalls,
        tokensIn = s.TokensIn,
        tokensOut = s.TokensOut,
        firstCycleAt = s.FirstCycleAt?.ToString("O"),
        lastCycleAt = s.LastCycleAt?.ToString("O"),
    };

    /// <summary>Blank or "all" is treated as absent: an empty filter value must mean "no filter", not "match nothing".</summary>
    private static string? ReadQuery(HttpRequestData req, string key)
    {
        var raw = System.Web.HttpUtility.ParseQueryString(req.Url.Query)[key];
        return string.IsNullOrWhiteSpace(raw) || raw.Trim().Equals("all", StringComparison.OrdinalIgnoreCase)
            ? null
            : raw.Trim();
    }

    /// <summary>
    /// The L3 analysis card. Its headline number is deliberately NOT a success rate: successRate
    /// here is the share of cited artifacts that were verified against the real system, which is
    /// the only figure on this card a confident-sounding model cannot inflate. A "resolved / total"
    /// rate would go up precisely when the agent starts asserting things it cannot back - see the
    /// Doctor Monkey's card, where a 17% "success rate" sits on top of a timeline of
    /// "cannot be determined".
    /// </summary>
    private static object BuildInspectorMonkey(
        IReadOnlyList<ForensicCase> cases,
        InspectorStats stats,
        AgentWorkQueueStats queueStats,
        InspectorOptions limits,
        DateTimeOffset? trackedLastRun,
        bool paused,
        string? filterDomain = null,
        string? filterClub = null)
    {
        // An investigation takes ~2-3 minutes. Anything still open well past that was killed
        // mid-flight (a restart, an expired lease) and its row will never be closed - treating it
        // as in-flight is what left the Scout's card saying "Analyzing now" forever.
        var abandonedBefore = DateTimeOffset.UtcNow.AddMinutes(-10);
        bool IsAbandoned(ForensicCase c) => c.CompletedAt is null && c.StartedAt < abandonedBefore;

        var inFlight = cases.FirstOrDefault(c => c.CompletedAt is null && c.Status == ForensicCaseStatus.Running && !IsAbandoned(c));
        var lastCompleted = cases.FirstOrDefault(c => c.CompletedAt is not null);
        var lastRun = trackedLastRun ?? lastCompleted?.StartedAt ?? DateTimeOffset.UtcNow;

        // Two independent reasons to be stopped: the operator's pause switch, and Inspector:Enabled
        // (a deployment-level kill switch). Both surface as "paused"; only the first is a button.
        var status = paused
            ? "paused"
            : lastCompleted?.Status == ForensicCaseStatus.Failed
                ? "error"
                : limits.ResolvedEnabled ? "active" : "paused";

        return new
        {
            id = MonkeyKey.InspectorMonkey,
            name = "Inspector Monkey",
            purpose = "L3 multi-agent analysis of the evidence packages handed over by the Scout Monkey. A Planner routes five specialists, three models debate the shared evidence, and every cited artifact is verified against the real system before a verdict is issued.",
            mode = "Observe",
            status,
            paused,
            risk = "low",
            lastRun = lastRun.ToString("O"),
            nextRun = lastRun.AddMinutes(1).ToString("O"),
            findings = cases.Count,
            // Echoed so the UI can say "scoped to this club" instead of letting an empty card read
            // as a dead agent. The counters below are NOT scoped - they are cumulative totals, and
            // silently filtering them would make the cost figures wrong.
            appliedFilter = filterDomain is null && filterClub is null
                ? null
                : new { domain = filterDomain, club = filterClub },
            actionsProposed = 0,
            actionsExecuted = 0,
            // Citation verification rate, not a win rate - see this method's summary.
            successRate = stats.CitationVerificationRate,
            impactPrevented = "n/a (observe only)",
            guardrails = new
            {
                maxAffectedInstances = 0,
                allowedEnvironments = new[] { "production" },
                maintenanceWindowOnly = false,
                approvalThreshold = "n/a (observe only)",
                rollbackAvailable = false,
                auditLogging = true,
            },
            // The cadence the Timer trigger actually uses (see InspectorMonkeyFunction) - the UI
            // counts down against it rather than hardcoding a minute of its own.
            cycleMinutes = 1,
            // Backlog plus the ceiling it is being drained under. A heartbeat of past cases cannot
            // answer "is it falling behind, or has it simply hit the daily cap?".
            queue = new
            {
                queued = queueStats.Queued,
                leased = queueStats.Leased,
                doneToday = queueStats.DoneToday,
                failedToday = queueStats.FailedToday,
                oldestQueuedAt = queueStats.OldestQueuedAt?.ToString("O"),
                maxInvestigationsPerDay = limits.ResolvedMaxInvestigationsPerDay,
                investigationsToday = stats.InvestigationsToday,
                concurrency = limits.ResolvedConcurrency,
            },
            inspectorStats = new
            {
                investigations = stats.Investigations,
                resolved = stats.Resolved,
                escalated = stats.Escalated,
                closedNoIssue = stats.ClosedNoIssue,
                failed = stats.Failed,
                citationsTotal = stats.CitationsTotal,
                citationsVerified = stats.CitationsVerified,
                citationVerificationRate = stats.CitationVerificationRate,
                llmCalls = stats.LlmCalls,
                tokensIn = stats.TokensIn,
                tokensOut = stats.TokensOut,
                investigationsToday = stats.InvestigationsToday,
            },
            // What the team is working on right now. Null between investigations - the card then
            // shows the countdown to the next claim attempt instead.
            currentCase = inFlight is null ? null : new
            {
                id = inFlight.Id,
                caseId = inFlight.CaseId,
                brandName = inFlight.BrandName,
                startedAt = inFlight.StartedAt.ToString("O"),
                phase = inFlight.Phase,
                // Equal weights because the per-phase duration has never been measured - unlike
                // ScoutRunPhase.TypicalWeights, which came from real cycles. Inventing a split
                // here would make the progress bar look precise about something unknown.
                phases = ForensicCasePhase.All.Select(p => new { key = p, weight = 1 }).ToArray(),
            },
            // One bar per investigation, newest first. This is the heartbeat: a static card has no
            // history, so a run of real timestamps is what proves the agent is alive.
            cases = cases.Select(c => new
            {
                id = c.Id,
                caseId = c.CaseId,
                brandName = c.BrandName,
                status = IsAbandoned(c) ? ForensicCaseStatus.Failed : (c.CompletedAt is null ? ForensicCaseStatus.Running : c.Status),
                decision = c.Decision,
                confidence = c.Confidence,
                durationMs = c.DurationMs,
                startedAt = c.StartedAt.ToString("O"),
                citationsTotal = c.CitationsTotal,
                citationsVerified = c.CitationsVerified,
                alertLevel = c.AlertLevel,
                errorMessage = IsAbandoned(c) ? c.ErrorMessage ?? "The investigation was interrupted and never completed." : c.ErrorMessage,
            }).ToArray(),
            activity = cases
                // Closed cases only. An in-flight one has no verdict yet, so it would land here as
                // an entry with no text; what it is doing right now is currentCase's job. An
                // abandoned case is included - it is closed in every sense that matters.
                .Where(c => c.CompletedAt is not null || IsAbandoned(c))
                .Take(20)
                .Select(c => new
                {
                    time = (c.CompletedAt ?? c.StartedAt).ToString("O"),
                    // The outcome in one word. Taken from Status rather than Decision because
                    // Decision is null on a failed case and "inconclusive" collapses into an
                    // escalation - Status is the only field that always has the real outcome.
                    stage = ToActivityStage(IsAbandoned(c) ? ForensicCaseStatus.Failed : c.Status),
                    // The verdict in the Inspector's own words; null while the case is still open
                    // or when it broke before producing one - never a fabricated sentence.
                    text = c.Verdict,
                    caseId = c.CaseId,
                    id = c.Id,
                    decision = c.Decision,
                    confidence = c.Confidence,
                    blockedBy = c.BlockedBy,
                    probableRootCause = c.ProbableRootCause,
                    alertLevel = c.AlertLevel,
                    alertReason = c.AlertReason,
                    ownerTeam = c.OwnerTeam,
                    ownerResolvedBy = c.OwnerResolvedBy,
                    citationsTotal = c.CitationsTotal,
                    citationsVerified = c.CitationsVerified,
                    durationMs = c.DurationMs,
                    brandName = c.BrandName,
                })
                .ToArray(),
        };
    }

    /// <summary>Case status as the single upper-case word the timeline badges on.</summary>
    private static string ToActivityStage(string status) => status switch
    {
        ForensicCaseStatus.ClosedNoIssue => "CLOSED",
        _ => status.ToUpperInvariant(),
    };

    private static object BuildLatencySpikeMonkey(IReadOnlyList<Finding> findings, DateTimeOffset? trackedLastRun, IReadOnlyList<DoctorReport> doctorReports, bool paused)
    {
        // trackedLastRun reflects when the timer actually fired, whether or not it found anything
        // this cycle; falling back to the newest finding (or now, on a cold start with no runs
        // recorded yet) avoids ever showing a blank lastRun, but is only a fallback - a monkey
        // that keeps running without producing findings must not look like it "last ran" whenever
        // its last finding happened to be created.
        var lastRun = trackedLastRun ?? findings.OrderByDescending(f => f.CreatedAt).FirstOrDefault()?.CreatedAt ?? DateTimeOffset.UtcNow;

        // Newest report per finding - a finding is only ever diagnosed once today, but this keeps
        // the lookup correct if that ever changes instead of picking an arbitrary duplicate.
        var latestReportByFindingId = doctorReports
            .GroupBy(r => r.FindingId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.GeneratedAt).First());

        return new
        {
            id = MonkeyKey.LatencySpikeMonkey,
            name = "Latency Spike Monkey",
            purpose = "Detects individual latency spikes that repeat within a stage beyond a configurable threshold (distinct from the aggregated P95 regression check) and raises a finding for the Doctor Monkey to diagnose.",
            mode = "Observe",
            status = paused ? "paused" : "active",
            paused,
            risk = "low",
            lastRun = lastRun.ToString("O"),
            nextRun = lastRun.AddMinutes(5).ToString("O"),
            findings = findings.Count,
            actionsProposed = 0,
            actionsExecuted = 0,
            successRate = 1.0,
            impactPrevented = "n/a (observe only)",
            guardrails = new
            {
                maxAffectedInstances = 0,
                allowedEnvironments = new[] { "production" },
                maintenanceWindowOnly = false,
                approvalThreshold = "n/a (observe only)",
                rollbackAvailable = false,
                auditLogging = true,
            },
            activity = findings
                .OrderByDescending(f => f.CreatedAt)
                .Take(20)
                .Select(f =>
                {
                    latestReportByFindingId.TryGetValue(f.Id, out var report);
                    return new
                    {
                        time = f.CreatedAt.ToString("O"),
                        stage = "observation",
                        text = f.Title,
                        findingId = f.Id,
                        // app.js uses this to badge the item - the raw event data below is what
                        // actually expands here now; the Doctor's own analysis lives under the
                        // Doctor Monkey's own evidence instead (see BuildDoctorMonkey).
                        diagnosed = report is not null,
                        // The captured events/data behind this observation (stage, threshold,
                        // observedCount, maxDurationMs, correlationIds, status breakdown, ...) -
                        // see LatencySpikeDetectorService/SqlLatencySpikeDetectorService for the
                        // exact shape per detector.
                        evidence = ParseJsonOrNull(f.EvidenceJson),
                    };
                })
                .ToArray(),
        };
    }

    private static object BuildSloBurnRateMonkey(IReadOnlyList<Finding> findings, DateTimeOffset? trackedLastRun, IReadOnlyList<DoctorReport> doctorReports, bool paused)
    {
        // See BuildLatencySpikeMonkey's comment - same fallback reasoning for lastRun.
        var lastRun = trackedLastRun ?? findings.OrderByDescending(f => f.CreatedAt).FirstOrDefault()?.CreatedAt ?? DateTimeOffset.UtcNow;
        var latestReportByFindingId = doctorReports
            .GroupBy(r => r.FindingId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.GeneratedAt).First());

        return new
        {
            id = MonkeyKey.SloBurnRateMonkey,
            name = "SLO Burn-Rate Monkey",
            purpose = "Applies Google SRE's multiwindow multi-burn-rate method against configured SLO definitions (latency + exception rate golden signals) across every domain/stage, raising page/ticket severity findings for the Doctor Monkey to diagnose and, for page severity, notifying every enabled alert channel (Teams/Email).",
            mode = "Observe",
            status = paused ? "paused" : "active",
            paused,
            risk = "low",
            lastRun = lastRun.ToString("O"),
            nextRun = lastRun.AddMinutes(5).ToString("O"),
            findings = findings.Count,
            actionsProposed = 0,
            actionsExecuted = 0,
            successRate = 1.0,
            impactPrevented = "n/a (observe only)",
            guardrails = new
            {
                maxAffectedInstances = 0,
                allowedEnvironments = new[] { "production" },
                maintenanceWindowOnly = false,
                approvalThreshold = "n/a (observe only)",
                rollbackAvailable = false,
                auditLogging = true,
            },
            activity = findings
                .OrderByDescending(f => f.CreatedAt)
                .Take(20)
                .Select(f =>
                {
                    latestReportByFindingId.TryGetValue(f.Id, out var report);
                    return new
                    {
                        time = f.CreatedAt.ToString("O"),
                        stage = "observation",
                        text = f.Title,
                        findingId = f.Id,
                        diagnosed = report is not null,
                        // Burn-rate evidence shape differs from the Latency Spike Monkey's -
                        // domain/stage/signalType/targetThresholdMs/targetPercentage/windowDays/
                        // alertSeverity/longWindowHours/shortWindowMinutes/burnRateMultiplier/
                        // longWindowBurnRate/shortWindowBurnRate/*SampleCount/note - see
                        // BurnRateDetector.EvaluateAsync for the exact writer.
                        evidence = ParseJsonOrNull(f.EvidenceJson),
                    };
                })
                .ToArray(),
        };
    }

    private static object BuildDoctorMonkey(IReadOnlyList<DoctorReport> reports, DateTimeOffset? trackedLastRun, IReadOnlyList<Finding> findings, IReadOnlyDictionary<Guid, IReadOnlyList<NotificationLogEntry>> notificationLogsByFindingId, bool paused)
    {
        // See BuildLatencySpikeMonkey's comment - trackedLastRun is the honest source, the most
        // recent report is only a fallback for before any run has been recorded.
        var lastRun = trackedLastRun ?? reports.OrderByDescending(r => r.GeneratedAt).FirstOrDefault()?.GeneratedAt ?? DateTimeOffset.UtcNow;
        var successRate = reports.Count == 0 ? 0.0 : reports.Average(r => r.Confidence);
        var findingById = findings.ToDictionary(f => f.Id);
        return new
        {
            id = MonkeyKey.DoctorMonkey,
            name = "Doctor Monkey",
            purpose = "Performs deep root-cause diagnosis on latency spike findings: classifies the error type, retrieves precedents from the knowledge base, and calls an LLM for a cited verdict with confidence and a proposed fix.",
            mode = "Observe",
            status = paused ? "paused" : "active",
            paused,
            risk = "low",
            lastRun = lastRun.ToString("O"),
            nextRun = lastRun.AddMinutes(5).ToString("O"),
            findings = reports.Count,
            actionsProposed = 0,
            actionsExecuted = 0,
            successRate,
            impactPrevented = "n/a (observe only)",
            guardrails = new
            {
                maxAffectedInstances = 0,
                allowedEnvironments = new[] { "production" },
                maintenanceWindowOnly = false,
                approvalThreshold = "n/a (observe only)",
                rollbackAvailable = false,
                auditLogging = true,
            },
            // "evidence" (not "diagnosis") is required here: the "View Evidence" modal in app.js
            // only shows activity entries whose stage is "evidence" or "observation" - any other
            // stage renders an empty list, which is the bug this comment is here to prevent regressing.
            activity = reports
                .OrderByDescending(r => r.GeneratedAt)
                .Take(20)
                .Select(r =>
                {
                    findingById.TryGetValue(r.FindingId, out var finding);
                    notificationLogsByFindingId.TryGetValue(r.FindingId, out var notificationLogs);
                    return new
                    {
                        time = r.GeneratedAt.ToString("O"),
                        stage = "evidence",
                        text = $"{r.Verdict} (category: {r.ErrorCategory}, confidence: {r.Confidence:P0})",
                        findingId = r.FindingId,
                        findingTitle = finding?.Title,
                        // The full analysis - this is what used to render under the Latency Spike
                        // Monkey's own evidence view; that one now shows the raw event data instead
                        // (see BuildLatencySpikeMonkey), this is where the diagnosis detail lives.
                        doctorAnalysis = new
                        {
                            verdict = r.Verdict,
                            errorCategory = r.ErrorCategory,
                            confidence = r.Confidence,
                            rootCauseHypothesis = r.RootCauseHypothesis,
                            citations = ParseJsonOrNull(r.CitationsJson),
                            proposedFix = ParseJsonOrNull(r.ProposedFixJson),
                            modelUsed = r.ModelUsed,
                            generatedAt = r.GeneratedAt.ToString("O"),
                        },
                        // Answers "was this alert already notified (toast/Teams/email), and did it
                        // succeed?" - empty array means either the finding never reached "page"
                        // severity (no notification was ever attempted) or it predates this
                        // feature; never inferred/guessed, only what was actually logged.
                        notifications = (notificationLogs ?? []).Select(n => new
                        {
                            channel = n.Channel,
                            providerUsed = n.ProviderUsed,
                            success = n.Success,
                            errorMessage = n.ErrorMessage,
                            sentAt = n.SentAt.ToString("O"),
                        }).ToArray(),
                    };
                })
                .ToArray(),
        };
    }

    private static async Task<HttpResponseData> WriteJsonAsync(HttpRequestData req, HttpStatusCode statusCode, object payload)
    {
        var response = req.CreateResponse(statusCode);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        response.Headers.Add("Cache-Control", "no-store");
        await response.WriteStringAsync(JsonSerializer.Serialize(payload, JsonOptions));
        return response;
    }

    /// <summary>Citations/ProposedFix are stored as raw JSON strings - parse them so the client gets structured objects, not a nested JSON-in-string. Malformed JSON degrades to null rather than breaking the whole /api/monkeys response. Shared with GetInspectorFunction, which parses the same kind of stored payload.</summary>
    internal static JsonNode? ParseJsonOrNull(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
