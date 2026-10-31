using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Core.Monitoring;

/// <summary>
/// Decides whether a finished investigation writes its own RCA, and writes it.
///
/// The rule is deliberately narrow: escalated, and the Inspector actually named a probable root
/// cause. An escalated case without one closed as "insufficient_evidence", and composing over that
/// spends a provider call to restate "we do not know" in eight sections.
///
/// Every other condition here exists because composing costs money:
/// - A paused rca-monkey composes nothing. Without this the pause button in the portal would stop
///   the on-demand path and silently leave the automatic one running.
/// - An investigation that already has a report is left alone. The Inspector re-opens the same
///   case when its queue item is retried, so without this each retry would pay again.
/// - A composition that produced nothing is not stored. Unlike the on-demand route, nobody is
///   watching here, and storing the empty result would make the guard above treat a rate limit as
///   a finished document.
/// - Nothing thrown here leaves. The case is already persisted by the time this runs, and a
///   provider outage must not undo an investigation that succeeded.
/// </summary>
public sealed class RcaAutoComposer(
    IRcaAgent rcaAgent,
    IRcaReportRepository rcaReports,
    IAgentPauseResolver pauseResolver,
    IOptions<RcaOptions> options,
    ILogger<RcaAutoComposer> logger)
{
    /// <summary>Returns the report when one was written, null when the case did not qualify.</summary>
    public async Task<RcaReport?> ComposeIfEligibleAsync(ForensicCase forensicCase, CancellationToken ct = default)
    {
        var settings = options.Value;
        if (!settings.IsEnabled || !settings.IsAutoComposeEnabled) return null;

        if (forensicCase.Status != ForensicCaseStatus.Escalated) return null;
        if (string.IsNullOrWhiteSpace(forensicCase.ProbableRootCause)) return null;

        if (await pauseResolver.IsPausedAsync(MonkeyKey.RcaMonkey, ct))
        {
            logger.LogInformation(
                "[RcaAgent] paused - no RCA composed for {CaseId}; it can still be written on demand.",
                forensicCase.CaseId);
            return null;
        }

        try
        {
            // Inside the try with everything else: a failed lookup here must not bubble up and
            // have the caller treat an already-finalized, successful investigation as failed.
            var existing = await rcaReports.GetByForensicCaseIdAsync(forensicCase.Id, ct);
            if (existing is not null) return null;

            var report = await rcaAgent.ComposeAsync(forensicCase, ct);

            // Only a real document is stored. The on-demand route persists its failures - a person
            // asked and has to see what came back - but here nobody is watching, and a rate limit
            // or a provider blip would otherwise become permanent: the "already has a report"
            // guard above would read that empty row as done and never try again.
            if (!report.IsAvailable)
            {
                logger.LogWarning(
                    "[RcaAgent] no RCA written for {CaseId}: {Error} Nothing was stored, so the case can be composed again.",
                    forensicCase.CaseId, report.Error);
                return null;
            }

            await rcaReports.SaveAsync(report, ct);

            logger.LogInformation(
                "[RcaAgent] composed the RCA for {CaseId} ({Sections} sections, {TokensIn}/{TokensOut} tokens).",
                forensicCase.CaseId, report.Sections.Count, report.TokensIn, report.TokensOut);

            return report;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "[RcaAgent] composing the RCA for {CaseId} failed.", forensicCase.CaseId);
            return null;
        }
    }
}
