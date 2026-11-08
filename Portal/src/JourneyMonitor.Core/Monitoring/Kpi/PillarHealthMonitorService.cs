using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring.Daemons;
using JourneyMonitor.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Core.Monitoring.Kpi;

/// <summary>Runs the rule evaluator for every enabled KPI of one pillar (Specification 1000 §9 "Monitor por pilar").</summary>
public sealed class PillarHealthMonitorService : IPillarHealthMonitor
{
    private readonly IKpiDefinitionStore _definitions;
    private readonly IKpiBucketStore _buckets;
    private readonly IKpiStateStore _states;
    private readonly IKpiAnomalyStore _anomalies;
    private readonly IIngestionWatermarkStore _watermarks;
    private readonly IKpiEvaluator _evaluator;
    private readonly ITriageSignalSubmitter _triage;
    private readonly IOptions<HealthMonitoringOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<PillarHealthMonitorService> _logger;
    private readonly IDaemonActivityRecorder? _activity;
    private readonly IMonkeyRunTracker? _runTracker;
    private readonly ICompanyProfileStore? _companyProfiles;

    // Process-wide per-pillar overlap guard (same pattern as MonkeyRunners/KpiIngestionService) so an overlapping Timer
    // tick and a "Run Now" (RunMonkeyNowFunction) on the same host instance serialize instead of recording the same
    // trigger twice. Does NOT cover true multi-instance scale-out: the timer singleton lease only serializes timer vs
    // timer, so a manual HTTP run on another instance can still overlap a timer run. The state re-reads below only
    // narrow that window; with the frozen SQL there is no uniqueness on (KpiDefinitionId, BucketStart) - see
    // docs/sessions for the distributed-lease follow-up.
    private static readonly IReadOnlyDictionary<KpiPillar, SemaphoreSlim> RunLocks =
        Enum.GetValues<KpiPillar>().ToDictionary(p => p, _ => new SemaphoreSlim(1, 1));

    public PillarHealthMonitorService(
        IKpiDefinitionStore definitions, IKpiBucketStore buckets, IKpiStateStore states, IKpiAnomalyStore anomalies,
        IIngestionWatermarkStore watermarks, IKpiEvaluator evaluator, ITriageSignalSubmitter triage,
        IOptions<HealthMonitoringOptions> options, TimeProvider time, ILogger<PillarHealthMonitorService> logger,
        IDaemonActivityRecorder? activity = null, IMonkeyRunTracker? runTracker = null,
        ICompanyProfileStore? companyProfiles = null)
    {
        _companyProfiles = companyProfiles;
        _definitions = definitions;
        _buckets = buckets;
        _states = states;
        _anomalies = anomalies;
        _watermarks = watermarks;
        _evaluator = evaluator;
        _triage = triage;
        _options = options;
        _time = time;
        _logger = logger;
        _activity = activity;
        _runTracker = runTracker;
    }

    public async Task<PillarMonitorRunResult> RunAsync(KpiPillar pillar, CancellationToken ct = default)
    {
        var options = _options.Value;
        var pillarName = pillar.ToString();
        if (!options.Enabled || options.Pillars is null
            || !options.Pillars.Any(p => string.Equals(p?.Trim(), pillarName, StringComparison.OrdinalIgnoreCase)))
        {
            return new PillarMonitorRunResult(0, 0, 0, 0);
        }

        var daemonKey = DaemonKeys.ForPillar(pillar);
        var run = new ErrorCountingDaemonRun((daemonKey is null ? null : _activity?.BeginRun(daemonKey)) ?? NullDaemonRun.Instance);

        var runLock = RunLocks[pillar];
        if (!await runLock.WaitAsync(0, ct).ConfigureAwait(false))
        {
            _logger.LogInformation("Health monitor {Pillar}: a run is already in progress on this instance - skipping this overlapping invocation.", pillarName);
            run.Info(DaemonActivityKinds.RunSkipped, "Skipped: another run is in progress on this instance");
            await FinishRunAsync(run, daemonKey).ConfigureAwait(false);
            return new PillarMonitorRunResult(0, 0, 0, 0);
        }

        try
        {
            run.Info(DaemonActivityKinds.RunStarted, "Run started");
            await run.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            PillarMonitorRunResult result;
            try
            {
                result = await RunCoreAsync(pillar, options, run, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (ex is not OperationCanceledException) run.Error(DaemonActivityKinds.Error, $"Run failed — {ex.Message}");
                await run.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }

            var summary = $"{result.KpisEvaluated} evaluated, {result.Triggered} triggered, {result.Suppressed} suppressed, {result.Skipped} skipped";
            if (run.Errors > 0) run.Warning(DaemonActivityKinds.RunCompleted, summary, result);
            else run.Info(DaemonActivityKinds.RunCompleted, summary, result);
            await FinishRunAsync(run, daemonKey).ConfigureAwait(false);
            return result;
        }
        finally
        {
            runLock.Release();
        }
    }

    private Task FinishRunAsync(IDaemonRun run, string? daemonKey) =>
        daemonKey is null
            ? run.FlushAsync(CancellationToken.None)
            : DaemonActivitySupport.FinishAsync(run, _runTracker, daemonKey, _time.GetUtcNow(), _logger);

    private static string Scope(KpiDefinition definition) => $"{definition.Kpi}/{definition.Domain}";

    private async Task<PillarMonitorRunResult> RunCoreAsync(KpiPillar pillar, HealthMonitoringOptions options, IDaemonRun activity, CancellationToken ct)
    {
        var pillarName = pillar.ToString();
        var now = _time.GetUtcNow();
        // Contract v4 §3: pillar status comes only from the domain grain; entity series belong to EntityDetectionMonitor.
        var definitions = (await _definitions.GetEnabledAsync(pillar, ct).ConfigureAwait(false))
            .Where(d => d.Grain == KpiGrain.Domain)
            .ToList();
        int evaluated = 0, triggered = 0, suppressed = 0, skipped = 0;
        var domains = new KpiDomainGuard(_companyProfiles, _logger);
        var budget = Math.Max(0, options.MaxKpisPerCycle);
        var taken = 0;

        foreach (var definition in definitions)
        {
            ct.ThrowIfCancellationRequested();
            if (taken >= budget) break;
            var kpiActivity = ProfileTaggedDaemonRun.For(activity, definition.ProfileKey);
            if (!await domains.IsAllowedAsync(definition, ct).ConfigureAwait(false))
            {
                kpiActivity.Warning(DaemonActivityKinds.KpiEvaluated, KpiDomainPolicy.NotAServiceWarning(definition),
                    new { kpiDefinitionId = definition.KpiDefinitionId, profileKey = definition.ProfileKey });
                skipped++;
                continue;
            }
            taken++;
            try
            {
                var run = await RunKpiAsync(pillar, definition, now, kpiActivity, ct).ConfigureAwait(false);
                if (run is null)
                {
                    skipped++;
                    continue;
                }
                evaluated++;
                triggered += run.Value.Triggered;
                suppressed += run.Value.Suppressed;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Health monitor failed for KPI {KpiDefinitionId} ({Pillar}/{Kpi}/{Domain}); skipping it this cycle.",
                    definition.KpiDefinitionId, pillarName, definition.Kpi, definition.Domain);
                kpiActivity.Error(DaemonActivityKinds.Error, $"{Scope(definition)}: {ex.Message}", new { kpiDefinitionId = definition.KpiDefinitionId, profileKey = definition.ProfileKey });
                skipped++;
            }
        }

        _logger.LogInformation("Health monitor {Pillar}: {Evaluated} evaluated, {Triggered} triggered, {Suppressed} suppressed, {Skipped} skipped.",
            pillarName, evaluated, triggered, suppressed, skipped);
        return new PillarMonitorRunResult(evaluated, triggered, suppressed, skipped);
    }

    /// <summary>Returns null when the KPI is skipped (not due, or no closed and ingested bucket to evaluate).</summary>
    private async Task<(int Triggered, int Suppressed)?> RunKpiAsync(KpiPillar pillar, KpiDefinition definition, DateTimeOffset now, IDaemonRun activity, CancellationToken ct)
    {
        var id = definition.KpiDefinitionId;
        var state = await _states.GetAsync(id, ct).ConfigureAwait(false);

        if (state.LastEvaluatedAt is { } last && !KpiSchedule.IsDue(last, now, definition.EvaluationFrequency))
        {
            activity.Info(DaemonActivityKinds.KpiEvaluated,
                $"{Scope(definition)}: not due (next evaluation {DaemonActivitySupport.Hm(last + definition.EvaluationFrequency)})",
                new { kpiDefinitionId = id, profileKey = definition.ProfileKey });
            return null;
        }

        var toEvaluate = await GetBucketsToEvaluateAsync(definition, state, now, ct).ConfigureAwait(false);
        if (toEvaluate.Count == 0)
        {
            await SaveUnlessRegressedAsync(state with { LastEvaluatedAt = now }, ct, mustPersist: false).ConfigureAwait(false);
            activity.Info(DaemonActivityKinds.KpiEvaluated, $"{Scope(definition)}: no new closed and ingested bucket", new { kpiDefinitionId = id, profileKey = definition.ProfileKey });
            return null;
        }

        var firstBucket = toEvaluate[0].BucketStart;
        var required = _evaluator.GetRequiredHistory(definition);
        IReadOnlyList<KpiBucket> history = required > TimeSpan.Zero
            ? await _buckets.GetRangeAsync(id, firstBucket - required, firstBucket, ct).ConfigureAwait(false)
            : [];

        var evaluations = _evaluator.Evaluate(definition, state, history, toEvaluate);
        var stageSuffix = string.IsNullOrEmpty(definition.Stage) ? "" : "/" + definition.Stage;
        foreach (var evaluation in evaluations)
        {
            activity.Info(DaemonActivityKinds.KpiEvaluated,
                $"{Scope(definition)}{stageSuffix} {DaemonActivitySupport.Hm(evaluation.Bucket.BucketStart)}: {evaluation.Explanation}",
                new
                {
                    kpiDefinitionId = id,
                    bucketStart = evaluation.Bucket.BucketStart,
                    verdict = evaluation.Verdict,
                    outcome = evaluation.Outcome,
                    observed = evaluation.ObservedValue,
                    reference = evaluation.ReferenceValue,
                    deviation = evaluation.Deviation,
                    counter = evaluation.StateAfter.ConsecutiveCount,
                });
        }
        int triggered = 0, suppressed = 0;

        // Once an anomaly is persisted, the rest of this KPI (triage, attach, state) must complete even if the run is
        // cancelled or a re-read fails; otherwise the next run re-evaluates the bucket and records a duplicate.
        var anomalyPersisted = false;
        var discarded = false;
        foreach (var evaluation in evaluations)
        {
            if (evaluation.Outcome == KpiEvaluationOutcome.SuppressedByCooldown)
            {
                suppressed++;
                continue;
            }
            if (evaluation.Outcome != KpiEvaluationOutcome.Triggered) continue;

            // Defense in depth against a concurrent run that already evaluated (and possibly recorded) this bucket:
            // the trigger is discarded (counted as suppressed) and no further trigger of this run is recorded.
            var latest = await TryReadStateAsync(id, anomalyPersisted ? CancellationToken.None : ct, tolerateFailure: anomalyPersisted).ConfigureAwait(false);
            if (latest?.LastBucketEvaluated is { } concurrent && concurrent >= evaluation.Bucket.BucketStart)
            {
                _logger.LogWarning("Health monitor: KPI {KpiDefinitionId} bucket {BucketStart:O} was already evaluated by a concurrent run (state at {LastBucketEvaluated:O}); discarding this trigger.",
                    id, evaluation.Bucket.BucketStart, concurrent);
                suppressed++;
                discarded = true;
                break;
            }

            triggered++;
            await RecordTriggerAsync(pillar, definition, evaluation, now, activity, anomalyPersisted ? CancellationToken.None : ct).ConfigureAwait(false);
            anomalyPersisted = true;
        }

        // Nothing of ours was recorded and the concurrent run owns the state: leave it untouched.
        if (discarded && !anomalyPersisted) return (triggered, suppressed);

        var finalState = (evaluations.Count > 0 ? evaluations[^1].StateAfter : state) with { LastEvaluatedAt = now };
        await SaveUnlessRegressedAsync(finalState, anomalyPersisted ? CancellationToken.None : ct, mustPersist: anomalyPersisted).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return (triggered, suppressed);
    }

    /// <summary>Returns null only when the read failed and <paramref name="tolerateFailure"/> is set.</summary>
    private async Task<KpiDetectionState?> TryReadStateAsync(int kpiDefinitionId, CancellationToken ct, bool tolerateFailure)
    {
        try
        {
            return await _states.GetAsync(kpiDefinitionId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (tolerateFailure)
        {
            _logger.LogWarning(ex, "Health monitor: could not re-read state of KPI {KpiDefinitionId} after recording an anomaly; continuing so the state is persisted.",
                kpiDefinitionId);
            return null;
        }
    }

    /// <summary>
    /// Saves unless the stored state already covers a later bucket (written by a newer, concurrent run): state never
    /// moves backwards. With <paramref name="mustPersist"/> a failed re-read does not prevent the save.
    /// </summary>
    private async Task SaveUnlessRegressedAsync(KpiDetectionState state, CancellationToken ct, bool mustPersist)
    {
        var latest = await TryReadStateAsync(state.KpiDefinitionId, ct, tolerateFailure: mustPersist).ConfigureAwait(false);
        if (latest?.LastBucketEvaluated is { } stored && (state.LastBucketEvaluated is not { } ours || stored > ours))
        {
            _logger.LogWarning("Health monitor: KPI {KpiDefinitionId} state was advanced to {Stored:O} by a concurrent run; not overwriting it with {Ours:O}.",
                state.KpiDefinitionId, stored, state.LastBucketEvaluated);
            return;
        }
        await _states.SaveAsync(state, ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<KpiBucket>> GetBucketsToEvaluateAsync(KpiDefinition definition, KpiDetectionState state, DateTimeOffset now, CancellationToken ct)
    {
        var watermark = await _watermarks.GetAsync(definition.KpiDefinitionId, ct).ConfigureAwait(false);
        if (watermark is null) return [];

        // A bucket is ready when it is closed (end <= now) and fully ingested (end <= watermark).
        var limit = watermark.Value < now ? watermark.Value : now;
        var size = definition.BucketSize;
        var lastReadyStart = KpiTime.FloorToBucket(limit - size, size);

        DateTimeOffset fromInclusive;
        if (state.LastBucketEvaluated is { } lastEvaluated)
        {
            fromInclusive = lastEvaluated + size;
            if (fromInclusive > lastReadyStart) return [];
        }
        else
        {
            var lookback = definition.LookbackWindow > size ? definition.LookbackWindow : size;
            fromInclusive = lastReadyStart + size - lookback;
        }

        var range = await _buckets.GetRangeAsync(definition.KpiDefinitionId, fromInclusive, lastReadyStart + size, ct).ConfigureAwait(false);
        var ready = range
            .Where(b => b.BucketStart + size <= limit)
            .Where(b => state.LastBucketEvaluated is not { } le || b.BucketStart > le)
            .OrderBy(b => b.BucketStart)
            .ToList();

        if (state.LastBucketEvaluated is null && ready.Count > 1)
            return [ready[^1]];
        return ready;
    }

    /// <param name="addCt">Token for the anomaly insert only; triage and attach run with CancellationToken.None once the anomaly exists.</param>
    private async Task RecordTriggerAsync(KpiPillar pillar, KpiDefinition definition, KpiEvaluation evaluation, DateTimeOffset now, IDaemonRun activity, CancellationToken addCt)
    {
        var observed = evaluation.ObservedValue ?? 0;
        var reference = evaluation.ReferenceValue ?? 0;
        var deviation = evaluation.Deviation ?? 0;
        var detection = KpiDetectionText.Format(definition, definition.ConsecutiveCount, observed, reference, deviation);

        var anomaly = await _anomalies.AddAsync(new KpiAnomaly(
            0, definition.KpiDefinitionId, now, evaluation.Bucket.BucketStart, observed, reference, deviation, detection, null), addCt).ConfigureAwait(false);
        activity.Warning(DaemonActivityKinds.Anomaly, $"{Scope(definition)}: {detection}",
            new { kpiDefinitionId = definition.KpiDefinitionId, profileKey = definition.ProfileKey, anomalyId = anomaly.AnomalyId, bucketStart = evaluation.Bucket.BucketStart });

        try
        {
            var signal = new TriageSignal(
                now,
                KpiPillarNames.ToDisplay(pillar),
                definition.Kpi,
                detection,
                definition.Domain,
                definition.Stage == "" ? null : definition.Stage,
                anomaly.AnomalyId,
                TriageSignalSources.Daemon,
                definition.ProfileKey);
            var submission = await _triage.SubmitAsync(signal, CancellationToken.None).ConfigureAwait(false);
            activity.Info(DaemonActivityKinds.TriageSubmitted,
                $"Investigation {submission.InvestigationId} queued{(submission.Duplicate ? " (duplicate)" : "")}",
                new { anomalyId = anomaly.AnomalyId, investigationId = submission.InvestigationId, duplicate = submission.Duplicate });
            await _anomalies.AttachInvestigationAsync(anomaly.AnomalyId, submission.InvestigationId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The anomaly and the detection state are kept; triage is not retried in this run.
            _logger.LogError(ex, "Triage submission failed for anomaly {AnomalyId} (KPI {KpiDefinitionId}).",
                anomaly.AnomalyId, definition.KpiDefinitionId);
            activity.Error(DaemonActivityKinds.Error, $"{Scope(definition)}: {ex.Message}", new { kpiDefinitionId = definition.KpiDefinitionId, profileKey = definition.ProfileKey });
        }
    }
}
