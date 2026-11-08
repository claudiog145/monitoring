using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring.Daemons;
using JourneyMonitor.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Core.Monitoring.Kpi;

/// <summary>Contract v4 §3: evaluates every entity series of the enabled entity-grain KPIs.</summary>
public interface IEntityDetectionMonitor
{
    Task<EntityDetectionRunResult> RunAsync(CancellationToken ct = default);
}

public sealed record EntityDetectionRunResult(int Definitions, int SeriesEvaluated, int Anomalous, int Triggered);

/// <summary>
/// Runs after the Application pillar monitor (same daemon key). Each entity series is evaluated with the same
/// <see cref="IKpiEvaluator"/> as the domain grain, against the history of that entity, with its own
/// "n of m" state and cooldown. Entity anomalies are recorded with their EntityKey only: they never touch the
/// domain state (pillar status) and never submit a TriageSignal - with one explicit, opt-in exception
/// (contract v4 §5): a triggered anomaly of a partner definition (<see cref="PartnerKpi.IsPartnerDefinition"/>)
/// goes to Triage as pillar Application with stage <c>partner:{key}</c>. Reads are batched per definition.
/// </summary>
public sealed class EntityDetectionMonitor : IEntityDetectionMonitor
{
    private const int MaxEntityKeysInActivity = 20;

    // Process-wide overlap guard, same pattern as PillarHealthMonitorService.
    private static readonly SemaphoreSlim RunLock = new(1, 1);

    private readonly IKpiDefinitionStore _definitions;
    private readonly IKpiBucketStore _buckets;
    private readonly IKpiStateStore _states;
    private readonly IKpiAnomalyStore _anomalies;
    private readonly IIngestionWatermarkStore _watermarks;
    private readonly IKpiEvaluator _evaluator;
    private readonly IOptions<HealthMonitoringOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<EntityDetectionMonitor> _logger;
    private readonly IDaemonActivityRecorder? _activity;
    private readonly ITriageSignalSubmitter? _partnerTriage;

    /// <param name="partnerTriage">Used ONLY for partner definitions (<see cref="PartnerKpi.IsPartnerDefinition"/>); every other entity KPI never submits Triage.</param>
    public EntityDetectionMonitor(
        IKpiDefinitionStore definitions, IKpiBucketStore buckets, IKpiStateStore states, IKpiAnomalyStore anomalies,
        IIngestionWatermarkStore watermarks, IKpiEvaluator evaluator, IOptions<HealthMonitoringOptions> options,
        TimeProvider time, ILogger<EntityDetectionMonitor> logger, IDaemonActivityRecorder? activity = null,
        ITriageSignalSubmitter? partnerTriage = null)
    {
        _partnerTriage = partnerTriage;
        _definitions = definitions;
        _buckets = buckets;
        _states = states;
        _anomalies = anomalies;
        _watermarks = watermarks;
        _evaluator = evaluator;
        _options = options;
        _time = time;
        _logger = logger;
        _activity = activity;
    }

    public static string Summary(KpiDefinition definition, int series, int anomalous, int triggered) =>
        $"{definition.Kpi}/{definition.Domain} [entity]: {series} series evaluated, {anomalous} anomalous, {triggered} triggered";

    public async Task<EntityDetectionRunResult> RunAsync(CancellationToken ct = default)
    {
        var options = _options.Value;
        var applicationName = KpiPillar.Application.ToString();
        if (!options.Enabled || options.Pillars is null
            || !options.Pillars.Any(p => string.Equals(p?.Trim(), applicationName, StringComparison.OrdinalIgnoreCase)))
        {
            return new EntityDetectionRunResult(0, 0, 0, 0);
        }

        if (!await RunLock.WaitAsync(0, ct).ConfigureAwait(false))
        {
            _logger.LogInformation("Entity detection: a run is already in progress on this instance - skipping this overlapping invocation.");
            return new EntityDetectionRunResult(0, 0, 0, 0);
        }

        var run = _activity?.BeginRun(DaemonKeys.ApplicationHealthMonitor) ?? NullDaemonRun.Instance;
        try
        {
            var now = _time.GetUtcNow();
            var all = await _definitions.GetEnabledAsync(null, ct).ConfigureAwait(false);
            var definitions = all.Where(d => d.Enabled && d.Grain == KpiGrain.Entity).ToList();
            var partnerTriage = new PartnerTriageRun(definitions.Where(PartnerKpi.IsPartnerDefinition));
            int processed = 0, series = 0, anomalous = 0, triggered = 0;

            foreach (var definition in definitions)
            {
                ct.ThrowIfCancellationRequested();
                var kpiRun = ProfileTaggedDaemonRun.For(run, definition.ProfileKey);
                try
                {
                    var outcome = await RunDefinitionAsync(definition, now, partnerTriage, ct).ConfigureAwait(false);
                    processed++;
                    series += outcome.Series;
                    anomalous += outcome.Anomalous;
                    triggered += outcome.TriggeredKeys.Count;
                    if (outcome.Idle is not null)
                    {
                        // Same convention as the pillar monitor: one short line when the definition had nothing to evaluate.
                        kpiRun.Info(DaemonActivityKinds.KpiEvaluated, $"{definition.Kpi}/{definition.Domain} [entity]: {outcome.Idle}",
                            new { kpiDefinitionId = definition.KpiDefinitionId, profileKey = definition.ProfileKey, grain = "entity" });
                        continue;
                    }
                    kpiRun.Info(DaemonActivityKinds.KpiEvaluated,
                        Summary(definition, outcome.Series, outcome.Anomalous, outcome.TriggeredKeys.Count),
                        new
                        {
                            kpiDefinitionId = definition.KpiDefinitionId,
                            profileKey = definition.ProfileKey,
                            grain = "entity",
                            series = outcome.Series,
                            anomalous = outcome.Anomalous,
                            triggered = outcome.TriggeredKeys.Count,
                            triggeredEntities = outcome.TriggeredKeys.Take(MaxEntityKeysInActivity).ToList(),
                            triageInvestigations = (outcome.Investigations ?? []).Take(MaxEntityKeysInActivity).ToList(),
                        });
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Entity detection failed for KPI {KpiDefinitionId} ({Kpi}/{Domain}); skipping it this cycle.",
                        definition.KpiDefinitionId, definition.Kpi, definition.Domain);
                    kpiRun.Error(DaemonActivityKinds.Error, $"{definition.Kpi}/{definition.Domain} [entity]: {ex.Message}",
                        new { kpiDefinitionId = definition.KpiDefinitionId, profileKey = definition.ProfileKey });
                }
            }

            _logger.LogInformation("Entity detection: {Definitions} definition(s), {Series} series evaluated, {Anomalous} anomalous, {Triggered} triggered.",
                processed, series, anomalous, triggered);
            return new EntityDetectionRunResult(processed, series, anomalous, triggered);
        }
        finally
        {
            await run.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            RunLock.Release();
        }
    }

    /// <summary>Idle is the activity text when nothing was evaluated (not due / no new bucket); null otherwise.</summary>
    private sealed record DefinitionOutcome(int Series, int Anomalous, IReadOnlyList<string> TriggeredKeys, string? Idle = null,
        IReadOnlyList<Guid>? Investigations = null);

    private const string NoNewBucket = "no new closed and ingested bucket";

    private static DefinitionOutcome Idle(string text) => new(0, 0, [], text);

    private async Task<DefinitionOutcome> RunDefinitionAsync(KpiDefinition definition, DateTimeOffset now, PartnerTriageRun partnerTriage, CancellationToken ct)
    {
        var id = definition.KpiDefinitionId;
        var states = (await _states.GetEntityStatesAsync(id, ct).ConfigureAwait(false))
            .Where(s => !string.IsNullOrEmpty(s.EntityKey))
            .GroupBy(s => s.EntityKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var lastRun = states.Values.Select(s => s.LastEvaluatedAt).Where(t => t is not null).Max();
        if (lastRun is { } last && !KpiSchedule.IsDue(last, now, definition.EvaluationFrequency))
            return Idle($"not due (next evaluation {DaemonActivitySupport.Hm(last + definition.EvaluationFrequency)})");

        var watermark = await _watermarks.GetAsync(id, ct).ConfigureAwait(false);
        if (watermark is null) return Idle(NoNewBucket);

        // A bucket is ready when it is closed (end <= now) and fully ingested (end <= watermark).
        var limit = watermark.Value < now ? watermark.Value : now;
        var size = definition.BucketSize;
        var lastReadyStart = KpiTime.FloorToBucket(limit - size, size);
        var lookback = definition.LookbackWindow > size ? definition.LookbackWindow : size;
        var windowStart = lastReadyStart + size - lookback;
        var required = _evaluator.GetRequiredHistory(definition);
        if (required < TimeSpan.Zero) required = TimeSpan.Zero;

        // Ingestion writes every entity of a bucket at once, so when the newest evaluated bucket already covers the
        // latest ready one there is nothing new for any series and the range is not read.
        var newestEvaluated = states.Values.Select(s => s.LastBucketEvaluated).Where(t => t is not null).Max();
        if (newestEvaluated is { } newest && newest >= lastReadyStart) return Idle(NoNewBucket);

        // Earliest bucket any series can evaluate: new series start at the latest ready bucket, known ones after their last.
        var oldestEvaluated = states.Values.Select(s => s.LastBucketEvaluated).Where(t => t is not null).Min();
        var evaluationStart = oldestEvaluated is { } oldest && oldest + size < lastReadyStart ? oldest + size : lastReadyStart;
        if (evaluationStart < windowStart) evaluationStart = windowStart;

        // One read per definition: every entity series, history included.
        var range = await _buckets.GetEntityRangeAsync(id, evaluationStart - required, lastReadyStart + size, ct).ConfigureAwait(false);

        var evaluated = new List<(string Key, IReadOnlyList<KpiEvaluation> Evaluations, KpiDetectionState State)>();
        foreach (var group in range.Where(b => !string.IsNullOrEmpty(b.EntityKey)).GroupBy(b => b.EntityKey, StringComparer.Ordinal))
        {
            var key = group.Key;
            var state = states.TryGetValue(key, out var stored) ? stored : KpiDetectionState.Initial(id, key);
            var series = group.OrderBy(b => b.BucketStart).ToList();

            var ready = series
                .Where(b => b.BucketStart >= windowStart && b.BucketStart + size <= limit)
                .Where(b => state.LastBucketEvaluated is not { } le || b.BucketStart > le)
                .ToList();
            if (ready.Count == 0) continue;
            if (state.LastBucketEvaluated is null && ready.Count > 1) ready = [ready[^1]];

            var first = ready[0].BucketStart;
            var history = required > TimeSpan.Zero
                ? series.Where(b => b.BucketStart >= first - required && b.BucketStart < first).ToList()
                : [];

            var evaluations = _evaluator.Evaluate(definition, state, history, ready);
            if (evaluations.Count == 0) continue;
            evaluated.Add((key, evaluations, state));
        }

        var anomalous = evaluated.Count(e => e.Evaluations.Any(x => x.Verdict == KpiBucketVerdict.Anomalous));
        var triggeredKeys = new List<string>();
        var investigations = new List<Guid>();
        var anomalyPersisted = false;

        foreach (var (key, evaluations, _) in evaluated)
        {
            var triggers = evaluations.Where(e => e.Outcome == KpiEvaluationOutcome.Triggered).ToList();
            if (triggers.Count == 0) continue;

            // Defense in depth against a concurrent run that already evaluated this series.
            var token = anomalyPersisted ? CancellationToken.None : ct;
            var latest = await _states.GetAsync(id, key, token).ConfigureAwait(false);
            foreach (var evaluation in triggers)
            {
                if (latest.LastBucketEvaluated is { } concurrent && concurrent >= evaluation.Bucket.BucketStart)
                {
                    _logger.LogWarning("Entity detection: KPI {KpiDefinitionId} entity {EntityKey} bucket {BucketStart:O} was already evaluated by a concurrent run; discarding this trigger.",
                        id, key, evaluation.Bucket.BucketStart);
                    continue;
                }

                var observed = evaluation.ObservedValue ?? 0;
                var reference = evaluation.ReferenceValue ?? 0;
                var deviation = evaluation.Deviation ?? 0;
                var detection = KpiDetectionText.Format(definition, definition.ConsecutiveCount, observed, reference, deviation);
                var anomaly = await _anomalies.AddAsync(new KpiAnomaly(0, id, now, evaluation.Bucket.BucketStart, observed, reference, deviation, detection, null)
                {
                    EntityKey = key,
                }, anomalyPersisted ? CancellationToken.None : ct).ConfigureAwait(false);
                anomalyPersisted = true;
                triggeredKeys.Add(key);

                if (PartnerKpi.IsPartnerDefinition(definition))
                {
                    var investigation = await SubmitPartnerTriageAsync(definition, anomaly, key, detection, now, partnerTriage).ConfigureAwait(false);
                    if (investigation is not null) investigations.Add(investigation.Value);
                }
            }
        }

        await SaveStatesAsync(id, evaluated, now, anomalyPersisted ? CancellationToken.None : ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return evaluated.Count == 0 ? Idle(NoNewBucket) : new DefinitionOutcome(evaluated.Count, anomalous, triggeredKeys, Investigations: investigations);
    }

    /// <summary>
    /// Contract v4 §5: a partner anomaly goes to Triage as pillar Application with stage <c>partner:{key}</c>.
    /// Runs once the anomaly exists, so it never uses the run's token; a failed submission keeps the anomaly and
    /// the state (same rule as the pillar monitor) and is not retried in this run.
    /// One partner incident is ONE investigation per (ProfileKey, partner key, bucket): when another dependency
    /// definition of the same profile already sent that partner and bucket to Triage - earlier in this run, or
    /// in an earlier run (its anomaly carries the investigation) - this anomaly is attached to that
    /// investigation instead of opening a new one.
    /// </summary>
    private async Task<Guid?> SubmitPartnerTriageAsync(KpiDefinition definition, KpiAnomaly anomaly, string partnerKey, string detection,
        DateTimeOffset now, PartnerTriageRun run)
    {
        if (_partnerTriage is null)
        {
            _logger.LogWarning("Entity detection: partner anomaly {AnomalyId} ({Kpi}/{Domain} partner {PartnerKey}) was recorded but no Triage submitter is registered.",
                anomaly.AnomalyId, definition.Kpi, definition.Domain, partnerKey);
            return null;
        }

        var incident = PartnerTriageRun.Key(definition.ProfileKey, partnerKey, anomaly.BucketStart);
        try
        {
            var existing = run.Find(incident) ?? await FindPriorPartnerInvestigationAsync(definition, anomaly, partnerKey, run).ConfigureAwait(false);
            if (existing is { } shared)
            {
                run.Remember(incident, shared);
                await _anomalies.AttachInvestigationAsync(anomaly.AnomalyId, shared, CancellationToken.None).ConfigureAwait(false);
                _logger.LogInformation("Entity detection: partner anomaly {AnomalyId} ({PartnerKey}, bucket {BucketStart:O}) joined investigation {InvestigationId} already opened for profile {ProfileKey}.",
                    anomaly.AnomalyId, partnerKey, anomaly.BucketStart, shared, definition.ProfileKey);
                return shared;
            }

            var signal = new TriageSignal(
                now,
                KpiPillarNames.ToDisplay(KpiPillar.Application),
                definition.Kpi,
                detection,
                definition.Domain,
                PartnerKpi.TriageStage(partnerKey),
                anomaly.AnomalyId,
                TriageSignalSources.Daemon,
                definition.ProfileKey);
            var submission = await _partnerTriage.SubmitAsync(signal, CancellationToken.None).ConfigureAwait(false);
            run.Remember(incident, submission.InvestigationId);
            await _anomalies.AttachInvestigationAsync(anomaly.AnomalyId, submission.InvestigationId, CancellationToken.None).ConfigureAwait(false);
            return submission.InvestigationId;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Triage submission failed for partner anomaly {AnomalyId} (KPI {KpiDefinitionId}, partner {PartnerKey}).",
                anomaly.AnomalyId, definition.KpiDefinitionId, partnerKey);
            return null;
        }
    }

    private const int PriorPartnerAnomaliesRead = 50;

    /// <summary>An investigation another dependency definition of the same profile opened for this partner and bucket in an earlier run.</summary>
    private async Task<Guid?> FindPriorPartnerInvestigationAsync(KpiDefinition definition, KpiAnomaly anomaly, string partnerKey, PartnerTriageRun run)
    {
        var siblings = run.SiblingDefinitionIds(definition);
        if (siblings.Count == 0) return null;
        try
        {
            var recent = await _anomalies.GetRecentAsync(null, null, PriorPartnerAnomaliesRead, definition.ProfileKey, partnerKey, CancellationToken.None).ConfigureAwait(false);
            return recent
                .Where(a => a.AnomalyId != anomaly.AnomalyId
                    && siblings.Contains(a.KpiDefinitionId)
                    && string.Equals(a.EntityKey, partnerKey, StringComparison.OrdinalIgnoreCase)
                    && a.BucketStart == anomaly.BucketStart
                    && a.InvestigationId is not null)
                .OrderBy(a => a.DetectedAt)
                .Select(a => a.InvestigationId)
                .FirstOrDefault();
        }
        catch (Exception ex)
        {
            // Not being able to look is not a reason to drop the incident: it is submitted (and may duplicate).
            _logger.LogWarning(ex, "Entity detection: could not read prior partner anomalies for {PartnerKey} [{ProfileKey}]; submitting to Triage.",
                partnerKey, definition.ProfileKey);
            return null;
        }
    }

    /// <summary>Per-run partner Triage bookkeeping: the dependency definitions of each profile and the incidents already sent.</summary>
    private sealed class PartnerTriageRun
    {
        private readonly Dictionary<string, HashSet<int>> _definitionsByProfile = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<(string Profile, string Partner, long Bucket), Guid> _submitted = [];

        public PartnerTriageRun(IEnumerable<KpiDefinition> partnerDefinitions)
        {
            foreach (var definition in partnerDefinitions)
            {
                var profile = definition.ProfileKey ?? "";
                if (!_definitionsByProfile.TryGetValue(profile, out var ids)) _definitionsByProfile[profile] = ids = [];
                ids.Add(definition.KpiDefinitionId);
            }
        }

        public static (string Profile, string Partner, long Bucket) Key(string? profileKey, string partnerKey, DateTimeOffset bucketStart) =>
            ((profileKey ?? "").Trim().ToLowerInvariant(), partnerKey.Trim().ToLowerInvariant(), bucketStart.UtcTicks);

        public Guid? Find((string, string, long) key) => _submitted.TryGetValue(key, out var id) ? id : null;

        public void Remember((string, string, long) key, Guid investigationId) => _submitted.TryAdd(key, investigationId);

        public IReadOnlySet<int> SiblingDefinitionIds(KpiDefinition definition) =>
            _definitionsByProfile.TryGetValue(definition.ProfileKey ?? "", out var ids)
                ? ids.Where(id => id != definition.KpiDefinitionId).ToHashSet()
                : new HashSet<int>();
    }

    /// <summary>One batch re-read, then saves every series state that does not move backwards.</summary>
    private async Task SaveStatesAsync(int id, IReadOnlyList<(string Key, IReadOnlyList<KpiEvaluation> Evaluations, KpiDetectionState State)> evaluated,
        DateTimeOffset now, CancellationToken ct)
    {
        if (evaluated.Count == 0) return;
        var latest = (await _states.GetEntityStatesAsync(id, ct).ConfigureAwait(false))
            .Where(s => !string.IsNullOrEmpty(s.EntityKey))
            .GroupBy(s => s.EntityKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        foreach (var (key, evaluations, _) in evaluated)
        {
            var final = evaluations[^1].StateAfter with { KpiDefinitionId = id, EntityKey = key, LastEvaluatedAt = now };
            if (latest.TryGetValue(key, out var stored) && stored.LastBucketEvaluated is { } storedBucket
                && (final.LastBucketEvaluated is not { } ours || storedBucket > ours))
            {
                _logger.LogWarning("Entity detection: KPI {KpiDefinitionId} entity {EntityKey} state was advanced to {Stored:O} by a concurrent run; not overwriting it.",
                    id, key, storedBucket);
                continue;
            }
            await _states.SaveAsync(final, ct).ConfigureAwait(false);
        }
    }
}
