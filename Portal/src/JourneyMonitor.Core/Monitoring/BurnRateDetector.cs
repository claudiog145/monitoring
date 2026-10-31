using System.Text.Json;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using Microsoft.Extensions.Logging;

namespace JourneyMonitor.Core.Monitoring;

/// <summary>
/// Google SRE's multiwindow multi-burn-rate method (Alerting on SLOs chapter), evaluated per
/// enabled <see cref="SloDefinition"/>: fetches every <see cref="GoldenSignalSample"/> bucket in
/// the largest window once (3 days - see <see cref="BurnRateWindows.All"/>) and sums in memory
/// for each of the 3 combos, rather than one SQL round trip per combo. An alert only fires when
/// BOTH a combo's long AND short window burn rates exceed its multiplier - this is what stops a
/// single 5-minute transient from paging anyone (it would blow past the multiplier in the short
/// window alone, but not sustain it over the matching long window). Checked in the order defined
/// by <see cref="BurnRateWindows.All"/> (page combos before the ticket combo) so a genuinely fast
/// burn is never under-reported as a mere ticket.
/// </summary>
public sealed class BurnRateDetector(
    ISloDefinitionStore sloDefinitionStore,
    IGoldenSignalStore goldenSignalStore,
    IFindingsRepository findingsRepository,
    ILogger<BurnRateDetector> logger) : IBurnRateDetector
{
    private static readonly TimeSpan LargestWindow = BurnRateWindows.All.Max(w => w.LongWindow);

    public async Task<IReadOnlyList<Finding>> RunAsync(string domain, CancellationToken ct = default)
    {
        IReadOnlyList<SloDefinition> allDefinitions;
        try
        {
            allDefinitions = await sloDefinitionStore.GetAllAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[BurnRateDetector] could not load SLO definitions - skipping domain {Domain} this cycle.", domain);
            return [];
        }

        // Note: an empty enabled set here is authoritative (definition loading succeeded), not a
        // reason to skip reconciliation below - every previously open finding for this domain must
        // still be resolved if nothing is enabled anymore (see the empty `stillBreaching` passed in).
        var domainDefinitions = allDefinitions
            .Where(d => d.Enabled && string.Equals(d.Domain, domain, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var newFindings = new List<Finding>();
        var stillBreaching = new HashSet<string>();
        var now = DateTimeOffset.UtcNow;

        foreach (var def in domainDefinitions)
        {
            var correlationId = $"slo:{domain}:{def.Stage ?? "_domain"}:{def.SignalType}";
            var result = await EvaluateAsync(def, now, ct);

            if (result.Status == BurnRateStatus.Indeterminate)
            {
                // Store failure, no samples yet, or an unsupported 100% target - genuinely unknown
                // whether this SLO is still breaching, so keep any existing open finding open
                // rather than let reconciliation resolve a real incident during an outage.
                stillBreaching.Add(correlationId);
                continue;
            }
            if (result.Status == BurnRateStatus.Healthy) continue; // Confirmed non-breach - let reconciliation resolve it.

            stillBreaching.Add(correlationId);
            var evaluation = result.Evaluation!;

            var (title, severityLabel) = BuildTitleAndSeverity(domain, def, evaluation);
            var evidence = BuildEvidence(domain, def, evaluation);

            var existing = await findingsRepository.GetOpenByCorrelationIdAsync(correlationId, ct);
            if (existing is not null)
            {
                var severityEscalated = existing.Severity != severityLabel;
                existing.Title = title;
                existing.Severity = severityLabel;
                existing.EvidenceJson = evidence;
                await findingsRepository.UpdateAsync(existing, ct);
                if (severityEscalated)
                {
                    // A ticket-severity finding just crossed into page severity (or vice versa) -
                    // must flow through like a new finding so the runner diagnoses/notifies it,
                    // not be silently refreshed-in-place like an unchanged, already-paging one
                    // (which would mean a real escalation never sends a Teams/email alert).
                    newFindings.Add(existing);
                }
                continue; // Otherwise: refreshed in place, not re-diagnosed/re-notified every cycle - same pattern as the latency detectors.
            }

            var finding = new Finding
            {
                Domain = domain,
                FindingType = FindingType.SloBurnRate,
                Stage = def.Stage ?? "(domain-level)",
                Title = title,
                Severity = severityLabel,
                EvidenceJson = evidence,
                OriginMonkeyKey = MonkeyKey.SloBurnRateMonkey,
                CorrelationId = correlationId,
            };

            newFindings.Add(await findingsRepository.AddAsync(finding, ct));
        }

        await StaleFindingReconciler.ResolveStaleFindingsAsync(findingsRepository, domain, stillBreaching, ct, MonkeyKey.SloBurnRateMonkey);

        return newFindings;
    }

    private async Task<BurnRateResult> EvaluateAsync(SloDefinition def, DateTimeOffset now, CancellationToken ct)
    {
        IReadOnlyList<GoldenSignalSample> samples;
        try
        {
            samples = await goldenSignalStore.GetSinceAsync(def.Domain, def.SignalType, def.Stage, now - LargestWindow, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[BurnRateDetector] sample read failed for {Domain}/{Stage}/{SignalType}.", def.Domain, def.Stage ?? "(domain-level)", def.SignalType);
            return BurnRateResult.Indeterminate;
        }

        if (samples.Count == 0) return BurnRateResult.Indeterminate; // No data yet - can't tell healthy from breaching.

        var errorBudget = 1.0 - ((double)def.TargetPercentage / 100.0);
        if (errorBudget <= 0) return BurnRateResult.Indeterminate; // Unsupported 100% target (API should reject this) - defend in depth rather than silently resolving a real finding.

        // The oldest bucket's own width counts towards coverage too (it reports data for its whole
        // [BucketStart, BucketStart+BucketMinutes) span, not just its start instant) - without this,
        // a single fresh bucket could satisfy a window combo's "nonzero total" check on day one and
        // immediately page on one bad 5-minute sample, which is exactly the single-cycle transient
        // this method exists to filter out.
        var earliestSample = samples.OrderBy(s => s.BucketStart).First();
        var historyCoverage = (now - earliestSample.BucketStart) + TimeSpan.FromMinutes(earliestSample.BucketMinutes);
        var anySufficientHistory = false;

        foreach (var spec in BurnRateWindows.All)
        {
            if (historyCoverage < spec.LongWindow) continue; // Not enough history yet to trust this combo's long window - try a shorter combo instead.
            anySufficientHistory = true;

            var longWindowStart = now - spec.LongWindow;
            var shortWindowStart = now - spec.ShortWindow;

            var longSamples = samples.Where(s => s.BucketStart >= longWindowStart).ToList();
            var shortSamples = samples.Where(s => s.BucketStart >= shortWindowStart).ToList();

            var longTotal = longSamples.Sum(s => s.TotalCount);
            var shortTotal = shortSamples.Sum(s => s.TotalCount);
            if (longTotal == 0 || shortTotal == 0) continue; // Not enough data yet for this combo - try the next (e.g. ticket's 3d/6h may still have signal).

            var longBad = longSamples.Sum(s => s.BadCount);
            var shortBad = shortSamples.Sum(s => s.BadCount);

            var longBurnRate = (longBad / (double)longTotal) / errorBudget;
            var shortBurnRate = (shortBad / (double)shortTotal) / errorBudget;

            if (longBurnRate >= spec.BurnRateMultiplier && shortBurnRate >= spec.BurnRateMultiplier)
            {
                return BurnRateResult.Breach(new BurnRateEvaluation(spec, longBurnRate, shortBurnRate, longTotal, shortTotal));
            }
        }

        // If no combo ever had enough history to evaluate, this is still indeterminate (warming
        // up), not a confirmed healthy signal - don't let reconciliation resolve a finding on that.
        return anySufficientHistory ? BurnRateResult.Healthy : BurnRateResult.Indeterminate;
    }

    private static (string Title, string Severity) BuildTitleAndSeverity(string domain, SloDefinition def, BurnRateEvaluation eval)
    {
        var severityLabel = eval.Spec.AlertSeverity == Domain.AlertSeverity.Page ? "critical" : "medium";
        var stageLabel = def.Stage ?? "domain-wide";
        var title = def.SignalType == Domain.SignalType.Latency
            ? $"SLO burn-rate: '{stageLabel}' latency error budget burning {eval.LongBurnRate:0.0}x ({domain})"
            : $"SLO burn-rate: exception rate error budget burning {eval.LongBurnRate:0.0}x ({domain})";
        return (title, severityLabel);
    }

    private static string BuildEvidence(string domain, SloDefinition def, BurnRateEvaluation eval) => JsonSerializer.Serialize(new
    {
        domain,
        stage = def.Stage,
        signalType = def.SignalType,
        targetThresholdMs = def.TargetThresholdMs,
        targetPercentage = def.TargetPercentage,
        windowDays = def.WindowDays,
        alertSeverity = eval.Spec.AlertSeverity,
        longWindowHours = eval.Spec.LongWindow.TotalHours,
        shortWindowMinutes = eval.Spec.ShortWindow.TotalMinutes,
        burnRateMultiplier = eval.Spec.BurnRateMultiplier,
        longWindowBurnRate = Math.Round(eval.LongBurnRate, 2),
        shortWindowBurnRate = Math.Round(eval.ShortBurnRate, 2),
        longWindowSampleCount = eval.LongTotalCount,
        shortWindowSampleCount = eval.ShortTotalCount,
        note = "burnRate = (observed bad fraction) / (1 - targetPercentage/100); fires only when BOTH the long and short window burn rates exceed the combo's multiplier (Google SRE multiwindow multi-burn-rate method).",
    });

    private sealed record BurnRateEvaluation(BurnRateWindowSpec Spec, double LongBurnRate, double ShortBurnRate, int LongTotalCount, int ShortTotalCount);

    /// <summary>
    /// "Not breaching" and "we genuinely don't know" are different outcomes that must never be
    /// conflated into a single `null`: a store failure, a brand-new SLO with no samples yet, and an
    /// unsupported 100% target are all <see cref="BurnRateStatus.Indeterminate"/>, NOT a confirmed
    /// healthy signal - only <see cref="BurnRateStatus.Healthy"/> is safe for the caller to let
    /// reconciliation resolve an existing open finding over.
    /// </summary>
    private enum BurnRateStatus { Indeterminate, Healthy, Breaching }

    private sealed record BurnRateResult(BurnRateStatus Status, BurnRateEvaluation? Evaluation = null)
    {
        internal static readonly BurnRateResult Indeterminate = new(BurnRateStatus.Indeterminate);
        internal static readonly BurnRateResult Healthy = new(BurnRateStatus.Healthy);
        internal static BurnRateResult Breach(BurnRateEvaluation evaluation) => new(BurnRateStatus.Breaching, evaluation);
    }
}
