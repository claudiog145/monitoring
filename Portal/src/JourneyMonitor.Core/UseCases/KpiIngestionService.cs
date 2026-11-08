using System.Collections.Concurrent;
using System.Globalization;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring.Daemons;
using JourneyMonitor.Core.Monitoring.Ingestion;
using JourneyMonitor.Core.Monitoring.Kpi;
using JourneyMonitor.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Core.UseCases;

/// <summary>
/// One ingestion cycle: for every enabled KPI, fetches the buckets closed at least
/// <see cref="KpiIngestionOptions.LagMinutes"/> ago since its watermark
/// (or, on the first run, the history its detection method needs - at least the lookback window)
/// with a sample spread evenly across buckets, stores buckets and samples, then advances the watermark.
/// When the oldest required history bucket is missing, that range is backfilled without moving the watermark.
/// Stops early once the samples written exceed <see cref="HealthMonitoringOptions.MaxRowsPerCycle"/>; the rest go in the next cycle.
/// Entity-grain KPIs (contract v4 §1–§2) store one bucket per (entity, bucket) and no samples; their rows count against the same budget.
/// </summary>
public sealed class KpiIngestionService : IKpiIngestionRunner
{
    private readonly IKpiDefinitionStore _definitions;
    private readonly IReadOnlyList<IKpiSource> _sources;
    private readonly IKpiBucketStore _buckets;
    private readonly IKpiSampleStore _samples;
    private readonly IIngestionWatermarkStore _watermarks;
    private readonly IOptions<HealthMonitoringOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<KpiIngestionService> _logger;
    private readonly TimeSpan _lag;
    private readonly IDaemonActivityRecorder? _activity;
    private readonly IMonkeyRunTracker? _runTracker;
    private readonly IDaemonActivityLog? _activityLog;
    private readonly IOptions<DaemonMonitoringOptions>? _daemonOptions;
    private readonly IKpiEvaluator? _evaluator;
    private readonly ICompanyProfileStore? _companyProfiles;

    // Single-instance overlap guard (same pattern as MonkeyRunners): a timer tick and a manual run
    // reading the same watermark would insert the same samples twice.
    private static readonly SemaphoreSlim RunLock = new(1, 1);

    // Entity-grain backfill gaps the source returned nothing for, per KpiDefinitionId: the last closed bucket at the time
    // of the attempt and the gap end. Process-local (no SQL marker rows per entity); losing it on restart costs one retry.
    private static readonly ConcurrentDictionary<int, EmptyEntityBackfill> EmptyEntityBackfills = new();

    private readonly record struct EmptyEntityBackfill(DateTimeOffset AttemptedTo, DateTimeOffset GapEnd);

    public KpiIngestionService(IKpiDefinitionStore definitions, IEnumerable<IKpiSource> sources, IKpiBucketStore buckets,
        IKpiSampleStore samples, IIngestionWatermarkStore watermarks, IOptions<HealthMonitoringOptions> options,
        TimeProvider time, ILogger<KpiIngestionService> logger, IOptions<KpiIngestionOptions>? ingestionOptions = null,
        IDaemonActivityRecorder? activity = null, IMonkeyRunTracker? runTracker = null,
        IDaemonActivityLog? activityLog = null, IOptions<DaemonMonitoringOptions>? daemonOptions = null,
        IKpiEvaluator? evaluator = null, ICompanyProfileStore? companyProfiles = null)
    {
        _companyProfiles = companyProfiles;
        _lag = TimeSpan.FromMinutes(Math.Max(0, (ingestionOptions?.Value ?? new KpiIngestionOptions()).LagMinutes));
        _definitions = definitions;
        _sources = sources.ToList();
        _buckets = buckets;
        _samples = samples;
        _watermarks = watermarks;
        _options = options;
        _time = time;
        _logger = logger;
        _activity = activity;
        _runTracker = runTracker;
        _activityLog = activityLog;
        _daemonOptions = daemonOptions;
        _evaluator = evaluator;
    }

    public async Task<KpiIngestionRunResult> RunAsync(CancellationToken ct = default)
    {
        var options = _options.Value;
        if (!options.Enabled) return new KpiIngestionRunResult(0, 0, 0, 0);

        var run = new ErrorCountingDaemonRun(_activity?.BeginRun(DaemonKeys.KpiIngestion) ?? NullDaemonRun.Instance);
        if (!await RunLock.WaitAsync(0, ct))
        {
            _logger.LogInformation("KPI ingestion: a run is already in progress on this instance - skipping this overlapping invocation.");
            run.Info(DaemonActivityKinds.RunSkipped, "Skipped: another run is in progress on this instance");
            await DaemonActivitySupport.FinishAsync(run, _runTracker, DaemonKeys.KpiIngestion, _time.GetUtcNow(), _logger);
            return new KpiIngestionRunResult(0, 0, 0, 0);
        }

        try
        {
            run.Info(DaemonActivityKinds.RunStarted, "Run started");
            await run.FlushAsync(CancellationToken.None);
            KpiIngestionRunResult result;
            try
            {
                result = await RunCoreAsync(options, run, ct);
            }
            catch (Exception ex)
            {
                if (ex is not OperationCanceledException) run.Error(DaemonActivityKinds.Error, $"Run failed — {ex.Message}");
                await run.FlushAsync(CancellationToken.None);
                throw;
            }

            var summary = result.KpisProcessed == 0 && result.Skipped == 0
                ? "Up to date — nothing to ingest"
                : $"{result.KpisProcessed} KPI(s) processed, {result.BucketsWritten} bucket(s), {result.SamplesWritten} sample(s) written, {result.Skipped} skipped";
            if (run.Errors > 0) run.Warning(DaemonActivityKinds.RunCompleted, summary, result);
            else run.Info(DaemonActivityKinds.RunCompleted, summary, result);
            await DaemonActivitySupport.FinishAsync(run, _runTracker, DaemonKeys.KpiIngestion, _time.GetUtcNow(), _logger);
            await PurgeActivityAsync();
            return result;
        }
        finally
        {
            RunLock.Release();
        }
    }

    private async Task PurgeActivityAsync()
    {
        if (_activityLog is null) return;
        try
        {
            var retentionDays = (_daemonOptions?.Value ?? new DaemonMonitoringOptions()).RetentionDays;
            await _activityLog.PurgeOlderThanAsync(_time.GetUtcNow() - TimeSpan.FromDays(retentionDays), CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "KPI ingestion: could not purge old daemon activity.");
        }
    }

    private async Task<KpiIngestionRunResult> RunCoreAsync(HealthMonitoringOptions options, IDaemonRun run, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        var processed = 0;
        var bucketsWritten = 0;
        var samplesWritten = 0;
        // Budget for MaxRowsPerCycle: raw samples (domain grain) plus entity bucket rows (entity grain stores no samples).
        var rowsWritten = 0;
        var skipped = 0;
        var warnedSourceKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var definitions = (await _definitions.GetEnabledAsync(null, ct))
            .Where(d => d.Enabled)
            .OrderBy(d => d.KpiDefinitionId)
            .ToList();

        var domains = new KpiDomainGuard(_companyProfiles, _logger);

        foreach (var definition in definitions)
        {
            ct.ThrowIfCancellationRequested();
            // Everything written about this KPI belongs to its company's console (contract v6 §5).
            var kpiRun = ProfileTaggedDaemonRun.For(run, definition.ProfileKey);

            if (!await domains.IsAllowedAsync(definition, ct))
            {
                _logger.LogWarning("KPI ingestion: KPI {KpiDefinitionId} domain '{Domain}' is not a service of profile {ProfileKey}; skipped.",
                    definition.KpiDefinitionId, definition.Domain, definition.ProfileKey);
                kpiRun.Warning(DaemonActivityKinds.IngestionKpi, KpiDomainPolicy.NotAServiceWarning(definition),
                    new { kpiDefinitionId = definition.KpiDefinitionId, profileKey = definition.ProfileKey });
                skipped++;
                continue;
            }

            var source = _sources.FirstOrDefault(s => s.CanHandle(definition.SourceKey));
            if (source is null)
            {
                if (warnedSourceKeys.Add(definition.SourceKey))
                {
                    _logger.LogWarning("KPI ingestion: no source handles '{SourceKey}'; KPIs using it are skipped.", definition.SourceKey);
                }
                var text = IsIncumbent(definition)
                    ? $"{definition.Kpi}/{definition.Domain}: no source for '{definition.SourceKey}'"
                    : NoProfileSourceText(definition);
                kpiRun.Warning(DaemonActivityKinds.IngestionKpi, text,
                    new { kpiDefinitionId = definition.KpiDefinitionId, profileKey = definition.ProfileKey });
                skipped++;
                continue;
            }

            var outcome = definition.Grain == KpiGrain.Entity
                ? await IngestEntityKpiAsync(definition, source, now, options, rowsWritten, kpiRun, ct)
                : await IngestKpiAsync(definition, source, now, options, rowsWritten, kpiRun, ct);
            if (outcome.Processed) processed++;
            if (outcome.Skipped) skipped++;
            bucketsWritten += outcome.Buckets;
            samplesWritten += outcome.Samples;
            rowsWritten += outcome.Samples + outcome.EntityRows;

            if (rowsWritten > options.MaxRowsPerCycle)
            {
                _logger.LogInformation("KPI ingestion: {Rows} rows (samples + entity buckets) written, above MaxRowsPerCycle {Max}; remaining KPIs wait for the next cycle.",
                    rowsWritten, options.MaxRowsPerCycle);
                break;
            }
        }

        // Backfilled buckets older than the retention keep their statistics; only their raw samples are purged here.
        await _samples.PurgeOlderThanAsync(now - TimeSpan.FromDays(options.SampleRetentionDays), ct);

        return new KpiIngestionRunResult(processed, bucketsWritten, samplesWritten, skipped);
    }

    private sealed record KpiOutcome(bool Processed, bool Skipped, int Buckets, int Samples, int EntityRows = 0)
    {
        public static readonly KpiOutcome None = new(false, false, 0, 0);
    }

    private enum FetchStatus { Ok, Unavailable, Failed }

    private async Task<KpiOutcome> IngestKpiAsync(KpiDefinition definition, IKpiSource source, DateTimeOffset now,
        HealthMonitoringOptions options, int samplesWrittenInRun, IDaemonRun run, CancellationToken ct)
    {
        var to = KpiTime.FloorToBucket(now - _lag, definition.BucketSize);
        var watermark = await _watermarks.GetAsync(definition.KpiDefinitionId, ct);
        var historyStart = HistoryStart(definition, to, now);
        var perBucket = KpiSampling.SamplesPerBucket(definition);

        var backfill = KpiOutcome.None;
        if (_evaluator is not null && watermark is not null && historyStart < watermark.Value)
        {
            backfill = await BackfillHistoryAsync(definition, source, historyStart, watermark.Value, perBucket, options, samplesWrittenInRun, run, ct);
            if (backfill.Skipped) return backfill;
        }

        // Known limitation: a very old watermark is caught up in one query, not clamped to the lookback window.
        var from = watermark ?? historyStart;
        if (from >= to)
        {
            run.Info(DaemonActivityKinds.IngestionKpi,
                $"{definition.Kpi}/{definition.Domain}: up to date (watermark {DaemonActivitySupport.Hm(from)}, next bucket closes {DaemonActivitySupport.Hm(from + definition.BucketSize)})",
                new { kpiDefinitionId = definition.KpiDefinitionId, profileKey = definition.ProfileKey, watermark = from });
            return backfill;
        }

        var (status, result) = await FetchAsync(definition, source, from, to, perBucket, "fetch failed", run, ct);
        if (status != FetchStatus.Ok) return backfill with { Processed = false, Skipped = true };

        var batch = KpiBucketAssembler.Assemble(definition, result!, from, to, perBucket);
        if (batch.Buckets.Count > 0) await _buckets.UpsertAsync(batch.Buckets, ct);
        if (batch.Samples.Count > 0) await _samples.AddAsync(batch.Samples, ct);
        if (watermark is null || to > watermark.Value)
        {
            await _watermarks.SetAsync(definition.KpiDefinitionId, to, ct);
        }

        run.Info(DaemonActivityKinds.IngestionKpi,
            $"{definition.Kpi}/{definition.Domain}: {batch.Buckets.Count} bucket(s) [{DaemonActivitySupport.Hm(from)}–{DaemonActivitySupport.Hm(to)}), {batch.Samples.Count} sample(s), watermark → {DaemonActivitySupport.Hm(to)}",
            new { kpiDefinitionId = definition.KpiDefinitionId, profileKey = definition.ProfileKey, from, to, buckets = batch.Buckets.Count, samples = batch.Samples.Count });

        return new KpiOutcome(true, false, backfill.Buckets + batch.Buckets.Count, backfill.Samples + batch.Samples.Count);
    }

    /// <summary>
    /// Contract v4 §1–§2, entity grain: one KpiBucket per (entity, bucket) with the source's aggregated statistics and no
    /// samples, same watermark and H1 history backfill as the domain grain. Entity rows count against MaxRowsPerCycle;
    /// a first run (no watermark) is clamped to what fits in the remaining budget and the backfill fills the rest later.
    /// </summary>
    private async Task<KpiOutcome> IngestEntityKpiAsync(KpiDefinition definition, IKpiSource source, DateTimeOffset now,
        HealthMonitoringOptions options, int rowsWrittenInRun, IDaemonRun run, CancellationToken ct)
    {
        var id = definition.KpiDefinitionId;
        var label = EntityLabel(definition);
        var to = KpiTime.FloorToBucket(now - _lag, definition.BucketSize);
        var watermark = await _watermarks.GetAsync(id, ct);
        var historyStart = HistoryStart(definition, to, now);
        var cap = Math.Max(1, definition.MaxEntitiesPerBucket);

        var backfill = KpiOutcome.None;
        if (_evaluator is not null && watermark is not null && historyStart < watermark.Value)
        {
            backfill = await BackfillEntityHistoryAsync(definition, source, historyStart, watermark.Value, to, options, rowsWrittenInRun, run, ct);
            if (backfill.Skipped) return backfill;
        }

        var from = watermark ?? historyStart;
        if (watermark is null && _evaluator is not null)
        {
            var budget = Math.Max(cap, options.MaxRowsPerCycle - rowsWrittenInRun);
            if ((to - from).Ticks / definition.BucketSize.Ticks * cap > budget)
            {
                from = Max(historyStart, to - BackfillChunk(definition, cap, budget));
            }
        }

        if (from >= to)
        {
            run.Info(DaemonActivityKinds.IngestionKpi,
                $"{label}: up to date (watermark {DaemonActivitySupport.Hm(from)}, next bucket closes {DaemonActivitySupport.Hm(from + definition.BucketSize)})",
                new { kpiDefinitionId = id, profileKey = definition.ProfileKey, grain = "entity", watermark = from });
            return backfill;
        }

        var (status, result) = await FetchAsync(definition, source, from, to, 0, "fetch failed", run, ct);
        if (status != FetchStatus.Ok) return backfill with { Processed = false, Skipped = true };

        var batch = KpiEntityBucketAssembler.Assemble(definition, result!, from, to);
        if (batch.Buckets.Count > 0) await _buckets.UpsertAsync(batch.Buckets, ct);
        if (watermark is null || to > watermark.Value)
        {
            await _watermarks.SetAsync(id, to, ct);
        }
        LogDroppedEntityRows(definition, batch);

        // Quiet entity KPIs (e.g. exceptions per entity) would otherwise log "0 bucket(s)" on every cycle.
        if (batch.Buckets.Count > 0)
        {
            run.Info(DaemonActivityKinds.IngestionKpi,
                $"{label}: {batch.Buckets.Count} bucket(s) for {batch.Entities} entit(ies) [{DaemonActivitySupport.Hm(from)}–{DaemonActivitySupport.Hm(to)})",
                new
                {
                    kpiDefinitionId = id, profileKey = definition.ProfileKey, grain = "entity", from, to, buckets = batch.Buckets.Count,
                    entities = batch.Entities, invalidEntityRows = batch.InvalidEntityRows, cappedEntityRows = batch.CappedEntityRows,
                });
        }

        return new KpiOutcome(true, false, backfill.Buckets + batch.Buckets.Count, 0, backfill.EntityRows + batch.Buckets.Count);
    }

    /// <summary>
    /// Entity-grain H1 backfill of [historyStart, oldest stored entity bucket), newest chunk first, without touching the
    /// watermark. The chunk size assumes the most entities seen in one stored bucket (or MaxEntitiesPerBucket when none is
    /// stored) per bucket against the remaining MaxRowsPerCycle. No empty rows are written per entity, so a chunk the
    /// source returned nothing for cannot be marked as done in the store: the walk continues further back in the same run,
    /// and a gap that came back completely empty is remembered in process (<see cref="EmptyEntityBackfills"/>) so it is
    /// retried at most once per bucket - only after a new bucket has closed - instead of on every cycle.
    /// </summary>
    private async Task<KpiOutcome> BackfillEntityHistoryAsync(KpiDefinition definition, IKpiSource source, DateTimeOffset historyStart,
        DateTimeOffset watermark, DateTimeOffset to, HealthMonitoringOptions options, int rowsWrittenInRun, IDaemonRun run, CancellationToken ct)
    {
        var id = definition.KpiDefinitionId;
        var oldestRequired = await _buckets.GetEntityRangeAsync(id, historyStart, historyStart + definition.BucketSize, ct);
        if (oldestRequired.Count > 0) return KpiOutcome.None;

        var existing = await _buckets.GetEntityRangeAsync(id, historyStart, watermark, ct);
        var gapEnd = existing.Count > 0 ? existing.Min(b => b.BucketStart) : watermark;
        if (gapEnd <= historyStart) return KpiOutcome.None;
        // The gap end only moves newer when the main fetch has just covered that part, so a gap already answered empty in
        // this bucket stays answered until the next bucket closes.
        if (EmptyEntityBackfills.TryGetValue(id, out var attempted) && attempted.AttemptedTo == to && gapEnd >= attempted.GapEnd)
        {
            return KpiOutcome.None;
        }

        var cap = Math.Max(1, definition.MaxEntitiesPerBucket);
        var perBucket = existing.Count > 0
            ? Math.Clamp(existing.GroupBy(b => b.BucketStart.UtcTicks).Max(g => g.Count()), 1, cap)
            : cap;
        var budget = Math.Max(perBucket, options.MaxRowsPerCycle - rowsWrittenInRun);

        var written = 0;
        var entities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fetched = false;
        var failed = false;
        var walkedFrom = gapEnd;
        var end = gapEnd;
        while (end > historyStart)
        {
            var remaining = Math.Max(perBucket, budget - written);
            var from = historyStart;
            if ((end - historyStart).Ticks / definition.BucketSize.Ticks * perBucket > remaining)
            {
                from = Max(historyStart, end - BackfillChunk(definition, perBucket, remaining));
            }

            var (status, result) = await FetchAsync(definition, source, from, end, 0, "history backfill failed", run, ct);
            if (status == FetchStatus.Unavailable) return new KpiOutcome(false, true, written, 0, written);
            if (status == FetchStatus.Failed)
            {
                failed = true;
                break;
            }
            fetched = true;
            walkedFrom = from;

            var batch = KpiEntityBucketAssembler.Assemble(definition, result!, from, end);
            LogDroppedEntityRows(definition, batch);
            if (batch.Buckets.Count > 0)
            {
                await _buckets.UpsertAsync(batch.Buckets, ct);
                written += batch.Buckets.Count;
                entities.UnionWith(batch.Buckets.Select(b => b.EntityKey));
                break;
            }
            end = from;
        }

        if (!fetched) return KpiOutcome.None;
        if (written == 0)
        {
            // A failed chunk is retried next cycle like the domain grain; a fully answered empty gap waits for a new bucket.
            if (!failed) EmptyEntityBackfills[id] = new EmptyEntityBackfill(to, gapEnd);
            return KpiOutcome.None;
        }
        EmptyEntityBackfills.TryRemove(id, out _);

        run.Info(DaemonActivityKinds.IngestionKpi,
            $"{EntityLabel(definition)}: backfilled history {written} bucket(s) for {entities.Count} entit(ies) [{Ymdhm(walkedFrom)}–{Ymdhm(gapEnd)})",
            new { kpiDefinitionId = id, profileKey = definition.ProfileKey, grain = "entity", from = walkedFrom, to = gapEnd, buckets = written, entities = entities.Count });

        return new KpiOutcome(true, false, written, 0, written);
    }

    private void LogDroppedEntityRows(KpiDefinition definition, KpiEntityIngestionBatch batch)
    {
        if (batch.InvalidEntityRows == 0 && batch.CappedEntityRows == 0) return;
        _logger.LogInformation(
            "KPI ingestion: KPI {KpiDefinitionId} ({Kpi}/{Domain}) dropped {Invalid} source row(s) with an empty or over-long EntityKey and {Capped} row(s) above MaxEntitiesPerBucket {Cap}.",
            definition.KpiDefinitionId, definition.Kpi, definition.Domain, batch.InvalidEntityRows, batch.CappedEntityRows, definition.MaxEntitiesPerBucket);
    }

    private static string EntityLabel(KpiDefinition definition) => $"{definition.Kpi}/{definition.Domain} [entity]";

    /// <summary>
    /// Oldest bucket the detection method needs so the latest closed bucket can be evaluated:
    /// max(LookbackWindow, required history + one bucket), whole buckets. Without an evaluator, the lookback window.
    /// Entity grain is capped at <see cref="KpiEntityRetention"/>: never older than the retention minus one bucket, nor
    /// than the oldest bucket the store still keeps at <paramref name="now"/>, so the backfill never refetches buckets the
    /// next entity upsert purges again.
    /// </summary>
    private DateTimeOffset HistoryStart(KpiDefinition definition, DateTimeOffset to, DateTimeOffset now)
    {
        var start = UncappedHistoryStart(definition, to);
        if (definition.Grain != KpiGrain.Entity) return start;

        var capped = Max(start, KpiTime.FloorToBucket(to - KpiEntityRetention.MaxHistorySpan(definition.BucketSize), definition.BucketSize));
        return Max(capped, KpiEntityRetention.OldestRetainedBucket(now, definition.BucketSize));
    }

    private DateTimeOffset UncappedHistoryStart(KpiDefinition definition, DateTimeOffset to)
    {
        if (_evaluator is null) return to - definition.LookbackWindow;

        var required = _evaluator.GetRequiredHistory(definition) + definition.BucketSize;
        var bucketTicks = definition.BucketSize.Ticks;
        var requiredBuckets = (required.Ticks + bucketTicks - 1) / bucketTicks;
        var span = TimeSpan.FromTicks(Math.Max(definition.LookbackWindow.Ticks, requiredBuckets * bucketTicks));
        return KpiTime.FloorToBucket(to - span, definition.BucketSize);
    }

    /// <summary>
    /// Fills the missing oldest part of the required history, [historyStart, oldest stored bucket), without touching the
    /// watermark. When that range would exceed the run's remaining MaxRowsPerCycle budget it is split into whole-day chunks
    /// fetched newest first, one chunk per run, so successive runs walk back until the history is complete.
    /// </summary>
    private async Task<KpiOutcome> BackfillHistoryAsync(KpiDefinition definition, IKpiSource source, DateTimeOffset historyStart,
        DateTimeOffset watermark, int perBucket, HealthMonitoringOptions options, int samplesWrittenInRun, IDaemonRun run, CancellationToken ct)
    {
        var id = definition.KpiDefinitionId;
        var oldestRequired = await _buckets.GetRangeAsync(id, historyStart, historyStart + definition.BucketSize, ct);
        if (oldestRequired.Count > 0) return KpiOutcome.None;

        var existing = await _buckets.GetRangeAsync(id, historyStart, watermark, ct);
        var gapEnd = existing.Count > 0 ? existing.Min(b => b.BucketStart) : watermark;
        if (gapEnd <= historyStart) return KpiOutcome.None;

        var from = historyStart;
        var budget = Math.Max(perBucket, options.MaxRowsPerCycle - samplesWrittenInRun);
        var gapBuckets = (gapEnd - historyStart).Ticks / definition.BucketSize.Ticks;
        if (perBucket > 0 && gapBuckets * perBucket > budget)
        {
            from = Max(historyStart, gapEnd - BackfillChunk(definition, perBucket, budget));
        }

        var (status, result) = await FetchAsync(definition, source, from, gapEnd, perBucket, "history backfill failed", run, ct);
        if (status == FetchStatus.Unavailable) return new KpiOutcome(false, true, 0, 0);
        if (status == FetchStatus.Failed) return KpiOutcome.None;

        var batch = KpiBucketAssembler.Assemble(definition, result!, from, gapEnd, perBucket);

        // Hours the source returned nothing for had no traffic: storing them as empty buckets records that, so the gap
        // check finds the oldest bucket and the next chunk moves further back instead of re-querying the same range.
        var returned = batch.Buckets.Select(b => b.BucketStart.UtcTicks).ToHashSet();
        var empty = new List<KpiBucket>();
        for (var start = from; start < gapEnd; start += definition.BucketSize)
        {
            if (!returned.Contains(start.UtcTicks)) empty.Add(new KpiBucket(id, start, 0, 0, null, null, null, null, null));
        }

        var toStore = batch.Buckets.Concat(empty).OrderBy(b => b.BucketStart).ToList();
        if (toStore.Count > 0) await _buckets.UpsertAsync(toStore, ct);
        if (batch.Samples.Count > 0) await _samples.AddAsync(batch.Samples, ct);

        var emptyText = empty.Count > 0 ? $" +{empty.Count} empty" : "";
        run.Info(DaemonActivityKinds.IngestionKpi,
            $"{definition.Kpi}/{definition.Domain}: backfilled history {batch.Buckets.Count} bucket(s){emptyText} [{Ymdhm(from)}–{Ymdhm(gapEnd)}), {batch.Samples.Count} sample(s)",
            new { kpiDefinitionId = id, profileKey = definition.ProfileKey, from, to = gapEnd, buckets = batch.Buckets.Count, emptyBuckets = empty.Count, samples = batch.Samples.Count });

        return new KpiOutcome(true, false, toStore.Count, batch.Samples.Count);
    }

    /// <summary>Largest span whose sample fits in maxRows (at least one bucket), in whole days when at least one day fits.</summary>
    private static TimeSpan BackfillChunk(KpiDefinition definition, int perBucket, int maxRows)
    {
        var buckets = Math.Max(1, maxRows / perBucket);
        var span = TimeSpan.FromTicks(definition.BucketSize.Ticks * buckets);
        var day = TimeSpan.FromDays(1);
        if (span >= day && day.Ticks % definition.BucketSize.Ticks == 0)
        {
            span = TimeSpan.FromDays(Math.Floor(span / day));
        }
        return span;
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    private async Task<(FetchStatus Status, KpiSourceResult? Result)> FetchAsync(KpiDefinition definition, IKpiSource source,
        DateTimeOffset from, DateTimeOffset to, int perBucket, string failure, IDaemonRun run, CancellationToken ct)
    {
        try
        {
            return (FetchStatus.Ok, await source.FetchAsync(definition, from, to, perBucket, ct));
        }
        catch (KpiSourceUnavailableException ex)
        {
            _logger.LogWarning("KPI ingestion: source unavailable for KPI {KpiDefinitionId} ({Kpi}/{Domain}, profile {ProfileKey}): {Reason}",
                definition.KpiDefinitionId, definition.Kpi, definition.Domain, definition.ProfileKey, ex.Message);
            var text = IsIncumbent(definition)
                ? $"{definition.Kpi}/{definition.Domain}: source unavailable — {ex.Message}"
                : NoProfileSourceText(definition);
            run.Warning(DaemonActivityKinds.IngestionKpi, text,
                new { kpiDefinitionId = definition.KpiDefinitionId, profileKey = definition.ProfileKey });
            return (FetchStatus.Unavailable, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // One failing query must not stop the other KPIs; the watermark stays so the range is retried.
            _logger.LogError(ex, "KPI ingestion: {Failure} for KPI {KpiDefinitionId} ({Kpi}/{Domain}).",
                failure, definition.KpiDefinitionId, definition.Kpi, definition.Domain);
            run.Error(DaemonActivityKinds.Error, $"{definition.Kpi}/{definition.Domain}: {failure} — {ex.Message}",
                new { kpiDefinitionId = definition.KpiDefinitionId, profileKey = definition.ProfileKey });
            return (FetchStatus.Failed, null);
        }
    }

    private static bool IsIncumbent(KpiDefinition definition) =>
        string.Equals(definition.ProfileKey, KpiProfiles.Default, StringComparison.OrdinalIgnoreCase);

    /// <summary>Contract v3 §1.7: a non-incumbent profile whose domain has no telemetry source is skipped with this Warning.</summary>
    public static string NoProfileSourceText(KpiDefinition definition) =>
        $"{definition.Kpi}/{definition.Domain} [{definition.ProfileKey}]: no telemetry source configured for this profile";

    private static string Ymdhm(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
}
