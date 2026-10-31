using System.Diagnostics;
using System.Text.Json;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Core.Monitoring.Inspector;

/// <summary>
/// One L3 investigation, end to end: PreProcess -> Scope -> Agents -> Debate -> Synthesis ->
/// Grounding -> Verdict -> Notify.
///
/// Four things here were paid for in earlier bugs and should not be "tidied away":
/// - A case is opened with GetByPackageId BEFORE AddAsync, and AddAsync failing is re-read rather
///   than rethrown. monitoring.ForensicCase has a unique index on InvestigationPackageId, so the
///   second attempt of a retried queue item would otherwise die on the index and burn an attempt
///   without ever investigating anything.
/// - A case never ends ambiguous. Every exit sets Status, CompletedAt and Phase=null; a case left
///   "running" with CompletedAt null forever is the Scout's zombie-cycle bug in a new table.
/// - Phase reporting only ever moves forward, and the phases the happy path skipped are published
///   before the case closes - the Scout used to die on "triage" for exactly this reason.
/// - Nothing throws its way out except cancellation and a case that could not even be opened: the
///   caller has to be able to release its queue lease, and it cannot do that from inside a stack
///   unwind it never sees.
/// </summary>
public sealed class InspectorMonkeyService(
    IForensicCaseRepository cases,
    IClubIdentityResolver clubResolver,
    InspectorPlanner planner,
    IGroundingService grounding,
    IVerdictService verdicts,
    IRedAlertPolicy redAlerts,
    IServiceOwnerResolver owners,
    INotificationService notifications,
    RcaAutoComposer rcaComposer,
    InspectorRecorder recorder,
    IOptions<InspectorOptions> options,
    ILogger<InspectorMonkeyService> logger,
    // Trailing and optional so the composition root and the existing tests keep constructing this
    // the way they already do. Null means the neutral vocabulary, never a failed investigation.
    ICompanyAdapterProvider? companyAdapters = null)
{
    private const int MaxVerdictLength = 1024;
    private const int MaxErrorLength = 1024;
    private const int MaxAlertReasonLength = 1024;

    /// <summary>
    /// Investigates one package and returns the case, failed or not. A caller must check
    /// <see cref="ForensicCase.Status"/> rather than rely on an exception: the only exceptions that
    /// leave here are cancellation and a case that could not be opened at all, because there is
    /// then nowhere to record the failure.
    /// </summary>
    public async Task<ForensicCase> InvestigateAsync(InvestigationPackage package, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        ForensicCase? forensicCase = null;        // Resolved before anything is written, and never again inside the run: the words a case is
        // described with must not change halfway through it.
        var vocabulary = await CompanyVocabulary.ResolveAsync(companyAdapters, logger, ct);
        try
        {
            var club = await clubResolver.ResolveAsync(package.EntityKey, ct);
            forensicCase = await OpenOrResumeAsync(package, club, ct);

            // PreProcess: every telemetry-derived string the agents will see goes through
            // PromptSanitizer here. Detection runs unattended, so there is no human to notice a
            // request path carrying instructions aimed at the model reading it.
            await ReportPhaseAsync(forensicCase, ForensicCasePhase.PreProcess, ct);
            var brief = InspectorCaseBrief.Build(package, club);
            await recorder.RecordDeterministicTurnAsync(
                forensicCase, "PreProcess", AgentKind.Planner,
                promptText: "sanitize every telemetry-derived string of the package before any model sees it",
                rawResponseText: brief.PromptBlock, outcomeJson: null, durationMs: 0, ct);

            await ReportPhaseAsync(forensicCase, ForensicCasePhase.Scope, ct);
            await recorder.RecordDeterministicTurnAsync(
                forensicCase, "Scope", AgentKind.Planner,
                promptText: "which sources apply to this case",
                rawResponseText: planner.DescribeScope(), outcomeJson: null, durationMs: 0, ct);

            var context = new InspectorContext(forensicCase, package, club, []);

            await ReportPhaseAsync(forensicCase, ForensicCasePhase.Agents, ct);
            var blackboard = await planner.GatherAsync(context, ct);
            context = context with { Blackboard = blackboard };
            forensicCase.BlackboardJson = JsonSerializer.Serialize(blackboard, InspectorJson.Options);

            await ReportPhaseAsync(forensicCase, ForensicCasePhase.Debate, ct);
            var proposals = await planner.DebateAsync(context, ct);

            await ReportPhaseAsync(forensicCase, ForensicCasePhase.Synthesis, ct);
            var synthesis = await planner.SynthesizeAsync(context, proposals, ct);
            if (!synthesis.Available)
            {
                return await CloseAsFailedAsync(forensicCase, synthesis.Error ?? "the Planner produced no synthesis.", stopwatch, ct);
            }
            forensicCase.SynthesisJson = synthesis.Json;

            await ReportPhaseAsync(forensicCase, ForensicCasePhase.Grounding, ct);
            var artifacts = await grounding.VerifyAsync(context, synthesis.Json, ct);
            forensicCase.GroundingJson = JsonSerializer.Serialize(artifacts, InspectorJson.Options);
            forensicCase.CitationsTotal = artifacts.Count;
            forensicCase.CitationsVerified = artifacts.Count(a => a.Verification == ArtifactVerification.Verified);

            await ReportPhaseAsync(forensicCase, ForensicCasePhase.Verdict, ct);
            var verdict = verdicts.Decide(context, synthesis.Json, artifacts);
            forensicCase.Decision = verdict.Decision;
            forensicCase.Verdict = InspectorJson.Truncate(verdict.Summary, MaxVerdictLength);
            forensicCase.ProbableRootCause = verdict.ProbableRootCause;
            forensicCase.Confidence = verdict.Confidence;
            forensicCase.BlockedBy = verdict.BlockedBy is null ? null : InspectorJson.Truncate(verdict.BlockedBy, MaxErrorLength);
            forensicCase.RunbookJson = verdict.RunbookJson;
            forensicCase.Status = StatusFor(verdict.Decision);

            // Resolved before the severity is decided, because "is there anyone to page" is one of
            // the conditions. Never throws: an unreadable map leaves the owner null, which the
            // policy reports as not routable rather than inventing a team.
            var owner = await owners.ResolveAsync(forensicCase.Vertical, forensicCase.Stage, ct);
            forensicCase.OwnerTeam = owner.OwnerTeam;
            forensicCase.OwnerPrimary = owner.PrimaryResponder;
            forensicCase.OwnerBackup = owner.Backup;
            forensicCase.OwnerResolvedBy = owner.ResolvedBy;

            // Severity is decided after the status, because every input it reads is final by now.
            var alert = redAlerts.Evaluate(forensicCase);
            forensicCase.AlertLevel = alert.Level;
            forensicCase.AlertReason = InspectorJson.Truncate(alert.Reason, MaxAlertReasonLength);

            // The happy path can skip a phase (nothing to debate with one model, nothing cited to
            // ground); the investigation still went through those steps and the card must show the
            // whole thing instead of stalling on whichever one was last written.
            await ReportRemainingPhasesAsync(forensicCase, ct);

            var saved = await FinalizeAsync(forensicCase, stopwatch, ct);
            await NotifyAsync(saved, vocabulary, ct);

            // After the case is persisted and announced, never before: the RCA describes a closed
            // investigation, and its own failure must not reopen one that finished.
            await rcaComposer.ComposeIfEligibleAsync(saved, ct);
            return saved;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (forensicCase is null)
            {
                // There is no case row to record this on, so the queue item has to carry it.
                logger.LogError(ex, "[InspectorMonkey] could not open a case for package {PackageId}.", package.Id);
                throw;
            }

            logger.LogError(ex, "[InspectorMonkey] investigation of {CaseId} failed.", forensicCase.CaseId);
            return await CloseAsFailedAsync(forensicCase, ex.Message, stopwatch, ct);
        }
    }

    /// <summary>
    /// Opens a case, or picks up the one a previous attempt already opened.
    ///
    /// The read-then-write is checked twice on purpose: the first read handles the normal retry,
    /// and the catch handles the case where the row appeared between the read and the insert. The
    /// unique index surfaces as SqlException 2601/2627 in SQL and as InvalidOperationException in
    /// the in-memory store, so the catch is deliberately broad and decides on what it finds
    /// afterwards rather than on the exception type.
    /// </summary>
    private async Task<ForensicCase> OpenOrResumeAsync(InvestigationPackage package, ClubIdentity club, CancellationToken ct)
    {
        var existing = await cases.GetByPackageIdAsync(package.Id, ct);
        if (existing is not null)
        {
            logger.LogInformation("[InspectorMonkey] resuming the existing case for {CaseId} instead of opening a second one.", existing.CaseId);
            return Resume(existing);
        }

        try
        {
            return await cases.AddAsync(NewCase(package, club), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var raced = await cases.GetByPackageIdAsync(package.Id, ct);
            if (raced is null) throw;

            logger.LogInformation(ex, "[InspectorMonkey] a case for {CaseId} already existed - resuming it.", raced.CaseId);
            return Resume(raced);
        }
    }

    private static ForensicCase NewCase(InvestigationPackage package, ClubIdentity club) => new()
    {
        InvestigationPackageId = package.Id,
        // Copied from the package, not resolved here: the Scout already decided whose work this is.
        ProfileKey = package.ProfileKey,
        CaseId = package.CaseId,
        EntityKey = package.EntityKey,
        BrandName = club.BrandName,
        ClubName = club.ClubName,
        // Carried because these are what every other filter in the portal keys on. The Scout
        // already resolved them; dropping them here left investigations unreachable from the club
        // filter an operator is already using.
        ClubId = club.ClubId,
        IcePartnerId = club.IcePartnerId,
        ClubResolvedBy = club.ResolvedBy,
        Vertical = package.Vertical,
        Stage = package.Stage,
    };

    /// <summary>
    /// Clears what the new attempt is about to recompute, keeps what the previous attempt really
    /// spent. Resetting the token counters would make a retried case look cheaper than it was.
    /// </summary>
    private static ForensicCase Resume(ForensicCase existing)
    {
        existing.Status = ForensicCaseStatus.Running;
        existing.Phase = null;
        existing.CompletedAt = null;
        existing.ErrorMessage = null;
        existing.CitationsTotal = 0;
        existing.CitationsVerified = 0;
        return existing;
    }

    private async Task<ForensicCase> CloseAsFailedAsync(ForensicCase forensicCase, string error, Stopwatch stopwatch, CancellationToken ct)
    {
        forensicCase.Status = ForensicCaseStatus.Failed;
        forensicCase.ErrorMessage = InspectorJson.Truncate(error, MaxErrorLength);
        forensicCase.Decision = null;

        // Run through the same policy rather than defaulting the level: a failed case must carry a
        // stated reason for not alerting, so nobody reads a silent "informational" as an all-clear.
        var alert = redAlerts.Evaluate(forensicCase);
        forensicCase.AlertLevel = alert.Level;
        forensicCase.AlertReason = InspectorJson.Truncate(alert.Reason, MaxAlertReasonLength);

        return await FinalizeAsync(forensicCase, stopwatch, ct);
    }

    private async Task<ForensicCase> FinalizeAsync(ForensicCase forensicCase, Stopwatch stopwatch, CancellationToken ct)
    {
        stopwatch.Stop();
        forensicCase.DurationMs = stopwatch.ElapsedMilliseconds;
        forensicCase.CompletedAt = DateTimeOffset.UtcNow;
        // Null means finished: an in-flight case must stay distinguishable from a finished one.
        forensicCase.Phase = null;

        logger.LogInformation(
            "[InspectorMonkey] {CaseId} -> {Status}/{Decision} ({Verified}/{Total} citations verified, {Calls} LLM calls, {Ms}ms).",
            forensicCase.CaseId, forensicCase.Status, forensicCase.Decision ?? "(none)",
            forensicCase.CitationsVerified, forensicCase.CitationsTotal, forensicCase.LlmCalls, forensicCase.DurationMs);

        return await cases.UpdateAsync(forensicCase, ct);
    }

    /// <summary>
    /// <see cref="InspectorDecision.Inconclusive"/> has no status of its own, and mapping it to
    /// "closed" would hide the one outcome that most needs a human. It escalates with BlockedBy
    /// populated; the Decision column keeps the distinction.
    /// </summary>
    private static string StatusFor(string decision) => decision switch
    {
        InspectorDecision.Resolve => ForensicCaseStatus.Resolved,
        InspectorDecision.CloseNoIssue => ForensicCaseStatus.ClosedNoIssue,
        _ => ForensicCaseStatus.Escalated,
    };

    private async Task NotifyAsync(ForensicCase forensicCase, CompanyVocabulary vocabulary, CancellationToken ct)
    {
        if (!options.Value.ResolvedNotifyOnCompletion) return;

        try
        {
            var request = new NotificationRequest(
                NotificationSourceType.ForensicCase,
                forensicCase.Id,
                // Nothing here pages anyone yet: an L3 analysis that needs a human is a warning,
                // and calling it critical would train people to ignore the word.
                forensicCase.Decision == InspectorDecision.Escalate ? NotificationSeverity.Warning : NotificationSeverity.Info,
                $"Inspector Monkey: {forensicCase.Decision ?? forensicCase.Status} - {forensicCase.CaseId}",
                BuildNotificationBody(forensicCase, vocabulary),
                []);

            await notifications.NotifyAsync(request, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The case is already persisted; a notification fan-out failure must not undo it.
            logger.LogWarning(ex, "[InspectorMonkey] could not notify about {CaseId}.", forensicCase.CaseId);
        }
    }

    private static string BuildNotificationBody(ForensicCase forensicCase, CompanyVocabulary vocabulary)
    {
        var lines = new List<string>
        {
            forensicCase.Verdict ?? "(no verdict was produced)",
            $"{vocabulary.EntityLabel}: {forensicCase.EntityKey} ({forensicCase.BrandName ?? "name unresolved"}) - {forensicCase.Vertical} / {forensicCase.Stage}",
            $"Confidence: {forensicCase.Confidence:0.##}. Citations verified: {forensicCase.CitationsVerified} of {forensicCase.CitationsTotal}.",
        };

        if (!string.IsNullOrWhiteSpace(forensicCase.BlockedBy)) lines.Add($"Blocked by: {forensicCase.BlockedBy}");
        if (!string.IsNullOrWhiteSpace(forensicCase.ErrorMessage)) lines.Add($"Error: {forensicCase.ErrorMessage}");

        return string.Join("\n", lines);
    }

    /// <summary>Progress reporting is cosmetic - it must never be the reason an investigation fails.</summary>
    private async Task ReportPhaseAsync(ForensicCase forensicCase, string phase, CancellationToken ct)
    {
        if (ForensicCasePhase.IndexOf(phase) <= ForensicCasePhase.IndexOf(forensicCase.Phase)) return;

        forensicCase.Phase = phase;
        try
        {
            await cases.UpdatePhaseAsync(forensicCase.Id, phase, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "[InspectorMonkey] could not publish phase {Phase}.", phase);
        }
    }

    private async Task ReportRemainingPhasesAsync(ForensicCase forensicCase, CancellationToken ct)
    {
        foreach (var phase in ForensicCasePhase.All)
        {
            await ReportPhaseAsync(forensicCase, phase, ct);
        }
    }
}
