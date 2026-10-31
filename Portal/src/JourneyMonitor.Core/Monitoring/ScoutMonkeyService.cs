using System.Diagnostics;
using System.Text.Json;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Core.Monitoring;

/// <summary>
/// The detection phase of one scout, end to end: read its priority board, decide what deserves
/// L3, and hand over an <see cref="InvestigationPackage"/>.
///
/// One instance serves both scouts - the board is a parameter of <see cref="RunAsync"/>, not of
/// the class - because the pipeline is identical and only the board text and the configuration
/// differ. Options are resolved by board through <see cref="IOptionsMonitor{T}"/>.
///
/// Four ordering decisions worth knowing before changing anything here:
/// - The board's deterministic rank gates escalation, not the LLM. The Scout's opinion is
///   recorded on every package but only vetoes when the scout's AllowScoutVeto is on, which is
///   off until the assessment has been evaluated against real cases.
/// - Candidates are ordered by rank and then by request volume, because brand.Information
///   resolves only a fraction of the clubs on the board, so traffic is the only business-impact
///   signal available when the cap bites.
/// - Dedup happens on (CaseId, Board) BEFORE the LLM is called, so a 4h window re-emitting the
///   same buckets every 5 minutes costs nothing after the first sighting. The board is part of
///   the key: the same group and bucket can be flagged by both boards for different reasons, and
///   each scout must decide that case on its own rather than lose it to whichever ran first.
/// - The queue's dedup key carries the board for the same reason, so both scouts can hand the
///   same CaseId to L3 as two separate pieces of work.
///
/// The company profile decides the vocabulary of every package - which service, which step, and
/// who is affected - and WHICH profile decided is recorded on the package itself, because the
/// first question about a wrong mapping is which profile produced it.
/// </summary>
public sealed class ScoutMonkeyService(
    IScoutBoardReader boardReader,
    ICompanyAdapterProvider companyAdapters,
    IClubIdentityResolver clubResolver,
    IScoutAssessor assessor,
    IScoutEvidenceCollector evidenceCollector,
    IInvestigationPackageRepository packages,
    IAgentWorkQueue workQueue,
    IScoutRunRepository runs,
    IScoutLlmCallLogRepository llmCallLogs,
    IOptionsMonitor<ScoutOptions> scoutOptions,
    ILogger<ScoutMonkeyService> logger)
{
    // camelCase, matching every API that reads these payloads back (GetScoutPackagesFunction and
    // the Simian Army card both look up "symptom"/"available"). Serializing with the default
    // PascalCase here would make those lookups silently return null.
    private static readonly JsonSerializerOptions PayloadJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Caps the row: the list is context for a reader, not an audit trail.</summary>
    private const int MaxSkippedCaseIdsRecorded = 50;

    public async Task<ScoutRun> RunAsync(string board, CancellationToken ct = default)
    {
        var options = scoutOptions.Get(board);
        var monkeyKey = ScoutBoard.MonkeyKeyFor(board);
        var now = DateTimeOffset.UtcNow;
        var run = new ScoutRun
        {
            Board = board,
            StartedAt = now,
            WindowFrom = now.AddHours(-options.ResolvedWindowHours),
            WindowTo = now,
        };

        // Persisted before any work so a crash mid-cycle still leaves evidence the cycle started.
        await runs.AddAsync(run, ct);

        if (!options.ResolvedEnabled)
        {
            run.Status = ScoutRunStatus.Disabled;
            run.CompletedAt = DateTimeOffset.UtcNow;
            logger.LogInformation("[{Monkey}] Enabled is false - cycle recorded as disabled without querying the board.", monkeyKey);
            return await runs.UpdateAsync(run, ct);
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var clubs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Resolved before the board is read, and never defaulted away: a cycle that cannot
            // reach its profile has to fail, because a board nothing can classify reads exactly
            // like a board with nothing on it.
            var adapter = await companyAdapters.GetAsync(options.ResolvedCompanyProfileKey, ct);
            run.ProfileKey = adapter.ProfileKey;

            await ReportPhaseAsync(run, ScoutRunPhase.BoardPhaseFor(board), ct);

            var request = new ScoutBoardRequest(
                board,
                run.WindowFrom,
                run.WindowTo,
                options.ResolvedBucketMinutes,
                options.ResolvedTakePerClub,
                options.ResolvedVertical,
                options.ResolvedClubFilter);

            var boardResult = await boardReader.ReadAsync(request, ct);
            run.BoardRowsEvaluated = boardResult.Rows.Count;
            foreach (var row in boardResult.Rows) clubs.Add(row.EntityKey);
            run.ClubsWatched = clubs.Count;
            var candidates = boardResult.Rows
                .Where(r => r.PriorityRank >= options.ResolvedInvestigationMinPriorityRank)
                .ToList();

            // Recorded even on a clear cycle: knowing exactly what was asked is what makes a quiet
            // cycle meaningful rather than just empty. Still a map keyed by board so the payload
            // shape survived the split into one scout per board.
            run.BoardQueriesJson = JsonSerializer.Serialize(
                new Dictionary<string, string> { [board] = boardResult.Kql }, PayloadJsonOptions);
            run.CandidatesFound = candidates.Count;
            await ReportPhaseAsync(run, ScoutRunPhase.Triage, ct);

            var ordered = candidates
                .OrderByDescending(r => r.PriorityRank)
                .ThenByDescending(GetRequestCount)
                .ToList();

            var skipped = new List<string>();

            foreach (var row in ordered)
            {
                ct.ThrowIfCancellationRequested();

                // Scoped to this board AND this company: the same group and bucket can be flagged by
                // both boards for different reasons, and two companies reading the same telemetry
                // produce the same CaseId shape - without the profile in the key, whichever ran first
                // would silently suppress the other's work.
                if (await packages.ExistsByCaseIdAsync(row.CaseId, board, adapter.ProfileKey, ct))
                {
                    run.PackagesSkippedDuplicate++;
                    if (skipped.Count < MaxSkippedCaseIdsRecorded) skipped.Add(row.CaseId);
                    continue;
                }

                if (run.PackagesCreated >= options.ResolvedMaxNewCasesPerRun)
                {
                    run.PackagesSkippedCap++;
                    continue;
                }

                // The profile answers again rather than the row carrying the answer across: the
                // adapter is pure, so the same profile over the same row cannot disagree with what
                // the board reader concluded. The honest end state is a classification field on
                // ScoutBoardRow itself - see the note in the handoff for this change.
                var classification = ScoutRowMapper.Classify(adapter, row);
                var club = await clubResolver.ResolveAsync(classification.EntityKey, ct);

                await ReportPhaseAsync(run, ScoutRunPhase.Analysis, ct);
                var assessed = options.ResolvedUseLlmAssessment
                    ? await assessor.AssessAsync(row, club, ct)
                    : new ScoutAssessmentResult(ScoutAssessment.Unavailable("UseLlmAssessment is false"), null!);
                var assessment = assessed.Assessment;

                if (options.ResolvedAllowScoutVeto && assessment.Available && !assessment.ShouldEscalate)
                {
                    run.PackagesVetoedByScout++;
                    logger.LogInformation(
                        "[{Monkey}] vetoed {CaseId}: {Why}", monkeyKey, row.CaseId, assessment.WhyNotEscalate ?? "(no reason given)");
                    continue;
                }

                // Collected after the veto decision so a vetoed row never pays for an extra query.
                await ReportPhaseAsync(run, ScoutRunPhase.Evidence, ct);
                var rawEvidence = await evidenceCollector.CollectAsync(row, ct);

                var package = await packages.AddAsync(
                    BuildPackage(row, board, adapter.ProfileKey, classification, club, assessment, rawEvidence, run.Id), ct);
                run.PackagesCreated++;

                await TryEnqueueForL3Async(package, row, monkeyKey, ct);
                await TryLogLlmCallAsync(assessed.Call, package.Id, row.CaseId, monkeyKey, ct);
            }

            // Walk whatever is left so the cycle always ends on the last phase. On a cycle where
            // every candidate was already known there is nothing to analyse, but the process still
            // went through those steps and the UI must show the whole thing, not stall on triage.
            await ReportRemainingPhasesAsync(run, ct);

            run.SkippedCaseIdsJson = skipped.Count > 0 ? JsonSerializer.Serialize(skipped, PayloadJsonOptions) : null;
            run.Status = run.PackagesCreated > 0 ? ScoutRunStatus.Detected : ScoutRunStatus.Clear;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A board that cannot run (expired credentials, missing RBAC on one of the three
            // Application Insights resources, a KQL change) must not look like a quiet cycle.
            run.Status = ScoutRunStatus.Failed;
            run.ErrorMessage = Truncate(ex.Message, 1000);
            logger.LogError(ex, "[{Monkey}] cycle failed after evaluating {Rows} board rows.", monkeyKey, run.BoardRowsEvaluated);
        }
        finally
        {
            stopwatch.Stop();
            run.DurationMs = stopwatch.ElapsedMilliseconds;
            run.CompletedAt = DateTimeOffset.UtcNow;
            run.Phase = null;
        }

        logger.LogInformation(
            "[{Monkey}] {Status}: {Rows} rows, {Candidates} candidates, {Created} packaged, {Dup} duplicates, {Cap} over cap, {Veto} vetoed ({Ms}ms) under profile \"{ProfileKey}\".",
            monkeyKey, run.Status, run.BoardRowsEvaluated, run.CandidatesFound, run.PackagesCreated,
            run.PackagesSkippedDuplicate, run.PackagesSkippedCap, run.PackagesVetoedByScout, run.DurationMs,
            options.ResolvedCompanyProfileKey ?? "(default)");

        return await runs.UpdateAsync(run, ct);
    }

    /// <summary>
    /// <paramref name="board"/> comes from the cycle, not from the row: the scout knows which board
    /// it queried, and it is the same value the dedup check used. Trusting the row instead would
    /// let a reader that mislabels a row write a package the dedup key cannot find again.
    ///
    /// <paramref name="classification"/> is what the company profile said about the row, and it is
    /// what names the service, the step and the entity. It travels into the deterministic half of
    /// the package complete with HOW each answer was reached, because an identity found by the
    /// ninth fallback must not read like one the application stamped explicitly.
    /// </summary>
    private static InvestigationPackage BuildPackage(
        ScoutBoardRow row,
        string board,
        string profileKey,
        ScoutRowClassification classification,
        ClubIdentity club,
        ScoutAssessment assessment,
        ScoutRawEvidence rawEvidence,
        Guid runId)
    {
        // The two halves stay separately addressable on purpose: whoever reads this later must be
        // able to tell what was measured from what was inferred.
        var deterministic = JsonSerializer.Serialize(new
        {
            board,
            caseId = row.CaseId,
            groupKey = row.GroupKey,
            source = row.Source,
            vertical = classification.ServiceLabel,
            entityKey = classification.EntityKey,
            stage = classification.StepLabel,
            priority = row.Priority,
            priorityRank = row.PriorityRank,
            reason = row.Reason,
            windowFrom = row.WindowFrom,
            windowTo = row.WindowTo,
            companyClassification = classification,
            boardRow = JsonDocument.Parse(row.RawRowJson).RootElement,
            groupPayload = JsonDocument.Parse(row.GroupPayloadJson).RootElement,
        }, PayloadJsonOptions);

        return new InvestigationPackage
        {
            CaseId = row.CaseId,
            ProfileKey = profileKey,
            Board = board,
            Vertical = classification.ServiceLabel,
            Stage = classification.StepLabel,
            Source = row.Source,
            EntityKey = club.EntityKey,
            EntityKind = club.Kind,
            ClubId = club.ClubId,
            IcePartnerId = club.IcePartnerId,
            BrandName = club.BrandName,
            ClubName = club.ClubName,
            ClubResolvedBy = club.ResolvedBy,
            Priority = row.Priority,
            PriorityRank = row.PriorityRank,
            Reason = row.Reason,
            WindowFrom = row.WindowFrom,
            WindowTo = row.WindowTo,
            DeterministicJson = deterministic,
            AssessmentJson = JsonSerializer.Serialize(assessment, PayloadJsonOptions),
            DrillDownKql = rawEvidence.Kql.Length > 0 ? rawEvidence.Kql : null,
            RawEvidenceJson = JsonSerializer.Serialize(rawEvidence, PayloadJsonOptions),
            RequestCount = GetRequestCount(row),
            FailedRequestCount = ReadInt(row.RawRowJson, "FailedRequests"),
            ExceptionCount = ReadInt(row.RawRowJson, "Exceptions"),
            AffectedUserCount = ReadInt(row.RawRowJson, "UniqueUserIds"),
            ScoutRunId = runId,
        };
    }

    private static int GetRequestCount(ScoutBoardRow row) => ReadInt(row.RawRowJson, "Requests");

    /// <summary>Progress reporting is cosmetic - it must never be the reason a cycle fails.</summary>
    private async Task ReportPhaseAsync(ScoutRun run, string phase, CancellationToken ct)
    {
        // Monotonic on purpose: the per-candidate loop reports analysis then evidence on every
        // iteration, and a bar that jumps backwards reads as a bug rather than as progress.
        if (PhaseIndex(run.Board, phase) <= PhaseIndex(run.Board, run.Phase)) return;

        run.Phase = phase;
        try
        {
            await runs.UpdatePhaseAsync(run.Id, phase, run.BoardRowsEvaluated, run.ClubsWatched, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "[{Monkey}] could not publish phase {Phase}.", ScoutBoard.MonkeyKeyFor(run.Board), phase);
        }
    }

    private async Task ReportRemainingPhasesAsync(ScoutRun run, CancellationToken ct)
    {
        foreach (var phase in ScoutRunPhase.PhasesFor(run.Board))
        {
            await ReportPhaseAsync(run, phase, ct);
        }
    }

    /// <summary>-1 for null or an unknown value, so the first real phase always moves forward.</summary>
    private static int PhaseIndex(string board, string? phase) =>
        phase is null ? -1 : Array.IndexOf(ScoutRunPhase.PhasesFor(board), phase);

    /// <summary>
    /// The handoff itself - this is what makes the L3 team see the package at all. Best-effort on
    /// purpose: the package is already persisted, so a queue write that fails must not throw away a
    /// cycle's work. But it is logged as an error rather than a warning, because the visible symptom
    /// is silent: the package simply sits at pending_handoff and nobody ever investigates it.
    /// Priority is the board's own rank, so the queue drains in the same order the board triaged.
    /// </summary>
    private async Task TryEnqueueForL3Async(InvestigationPackage package, ScoutBoardRow row, string monkeyKey, CancellationToken ct)
    {
        try
        {
            await workQueue.EnqueueAsync(new AgentWorkItem
            {
                SourceMonkey = monkeyKey,
                PayloadType = AgentWorkPayloadType.InvestigationPackage,
                PayloadId = package.Id.ToString(),
                // The board is part of the key because the CaseId is not: both scouts can flag the
                // same group and bucket, and those are two pieces of work, not one.
                DedupKey = $"{package.ProfileKey} | {package.Board} | {package.CaseId}",
                Priority = row.PriorityRank,
            }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "[{Monkey}] package {CaseId} was persisted but could NOT be queued for L3 - it will not be investigated until it is re-queued.", monkeyKey, row.CaseId);
        }
    }

    /// <summary>Best-effort, like the Doctor Monkey's own call log: losing the audit record must never cost the package it documents.</summary>
    private async Task TryLogLlmCallAsync(ScoutLlmCall? call, Guid packageId, string caseId, string monkeyKey, CancellationToken ct)
    {
        if (call is null) return;
        try
        {
            call.InvestigationPackageId = packageId;
            await llmCallLogs.AddAsync(call, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "[{Monkey}] could not persist the LLM call log for {CaseId}.", monkeyKey, caseId);
        }
    }

    private static int ReadInt(string json, string property)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty(property, out var el)) return 0;
            return el.ValueKind switch
            {
                JsonValueKind.Number when el.TryGetDouble(out var d) => (int)Math.Round(d),
                JsonValueKind.String when double.TryParse(el.GetString(), out var s) => (int)Math.Round(s),
                _ => 0,
            };
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…[truncated]";
}
