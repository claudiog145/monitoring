using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring.Kpi.Methods;
using Microsoft.Extensions.Logging;

namespace JourneyMonitor.Core.Monitoring.ScoutShadow;

/// <summary>What one finished Scout cycle hands to the shadow: its board rows, its window and its profile.</summary>
public sealed record ScoutShadowCycle(
    Guid RunId,
    string Board,
    ICompanyAdapter Adapter,
    IReadOnlyList<ScoutBoardRow> Rows,
    DateTimeOffset WindowFrom,
    DateTimeOffset WindowTo,
    int MinPriorityRank);

/// <summary>
/// Contract v4 §4: after a Scout cycle, reads the cycle's entity anomalies (and, for unmatched rules cases, what the
/// method evaluated), compares them with the board through <see cref="ScoutShadowComparator"/> and upserts the rows.
///
/// Board ↔ KPI: same profile, an entity-grain definition of the board's kind (latency board ↔
/// <see cref="LatencySourceKey"/>, exception board ↔ <see cref="ExceptionsSourceKey"/>) whose Domain is the row's
/// service. Entity anomalies are found through the series state: a trigger sets CooldownUntil to the bucket end plus
/// the cooldown and it never moves backwards, so only series with CooldownUntil after the window start can hold an
/// anomaly of the window. Reads are bounded per definition (<see cref="MaxSeriesPerDefinition"/>).
/// Read-only for every KPI store: the shadow never changes detection state.
/// </summary>
public sealed class ScoutShadowRecorder(
    IScoutShadowStore store,
    IKpiDefinitionStore definitions,
    IKpiAnomalyStore anomalies,
    IKpiStateStore? states,
    IKpiBucketStore? buckets,
    ILogger logger)
{
    public const string LatencySourceKey = "appinsights:latency";
    public const string ExceptionsSourceKey = "appinsights:exceptions";
    public const int MaxSeriesPerDefinition = 1000;
    private const int AnomaliesPerSeries = 50;

    public static string SourceKeyFor(string board) =>
        string.Equals(board, ScoutBoard.Exception, StringComparison.OrdinalIgnoreCase) ? ExceptionsSourceKey : LatencySourceKey;

    /// <summary>Builds the comparison input from the stores; exposed so the mapping can be tested without a store write.</summary>
    public async Task<ScoutShadowInput> BuildInputAsync(ScoutShadowCycle cycle, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cycle);
        var profileKey = cycle.Adapter.ProfileKey;
        var sourceKey = SourceKeyFor(cycle.Board);

        var entityDefinitions = (await definitions.GetAllAsync(profileKey, ct).ConfigureAwait(false))
            .Where(d => d.Grain == KpiGrain.Entity
                && string.Equals(d.ProfileKey, profileKey, StringComparison.OrdinalIgnoreCase)
                && string.Equals(d.SourceKey, sourceKey, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var flaggedRows = cycle.Rows.Where(r => r.PriorityRank >= cycle.MinPriorityRank).ToList();
        var ruleCases = flaggedRows
            .Select(r => ToRuleCase(cycle.Adapter, r))
            .OfType<ScoutShadowRuleCase>()
            .ToList();
        if (ruleCases.Count < flaggedRows.Count)
        {
            logger.LogInformation("Scout shadow: {Skipped} flagged {Board} row(s) identify no entity (no identity and no host) and are not compared.",
                flaggedRows.Count - ruleCases.Count, cycle.Board);
        }

        var methodDefinitions = new List<ScoutShadowMethodDefinition>();
        var methodAnomalies = new List<ScoutShadowMethodAnomaly>();
        var evaluated = new List<ScoutShadowEvaluatedBucket>();

        foreach (var definition in entityDefinitions)
        {
            ct.ThrowIfCancellationRequested();
            var size = definition.BucketSize;
            if (size <= TimeSpan.Zero) continue;
            methodDefinitions.Add(new ScoutShadowMethodDefinition(definition.KpiDefinitionId, definition.Domain, definition.Stage ?? "", size));

            var seriesStates = states is null
                ? []
                : (await states.GetEntityStatesAsync(definition.KpiDefinitionId, ct).ConfigureAwait(false))
                    .Where(s => !string.IsNullOrEmpty(s.EntityKey))
                    .ToList();

            var triggered = seriesStates
                .Where(s => s.CooldownUntil is { } until && until > cycle.WindowFrom)
                .OrderByDescending(s => s.CooldownUntil)
                .ToList();
            if (triggered.Count > MaxSeriesPerDefinition)
            {
                logger.LogWarning("Scout shadow: {Count} series of KPI {KpiDefinitionId} may hold anomalies in the window; only the latest {Max} are read.",
                    triggered.Count, definition.KpiDefinitionId, MaxSeriesPerDefinition);
                triggered = triggered.Take(MaxSeriesPerDefinition).ToList();
            }

            foreach (var series in triggered)
            {
                var entity = ScoutShadowEntityKey.FromKpi(series.EntityKey);
                if (entity is null) continue;
                var recent = await anomalies.GetRecentAsync(null, definition.Domain, AnomaliesPerSeries, definition.ProfileKey, series.EntityKey, ct)
                    .ConfigureAwait(false);
                methodAnomalies.AddRange(recent
                    .Where(a => a.KpiDefinitionId == definition.KpiDefinitionId
                        && a.BucketStart < cycle.WindowTo && a.BucketStart + size > cycle.WindowFrom)
                    .Select(a => new ScoutShadowMethodAnomaly(a.AnomalyId, a.KpiDefinitionId, entity, a.BucketStart)));
            }

            // "normal" needs proof the method looked: the entity had a bucket and its series moved past it.
            var flaggedEntities = ruleCases
                .Where(c => string.Equals(c.Service, definition.Domain, StringComparison.OrdinalIgnoreCase))
                .Select(c => c.EntityKey)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (buckets is null || flaggedEntities.Count == 0) continue;

            var lastEvaluated = seriesStates
                .Select(s => (Entity: ScoutShadowEntityKey.FromKpi(s.EntityKey), s.LastBucketEvaluated))
                .Where(s => s.Entity is not null && s.LastBucketEvaluated is not null)
                .GroupBy(s => s.Entity!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Max(s => s.LastBucketEvaluated!.Value), StringComparer.OrdinalIgnoreCase);

            var range = await buckets.GetEntityRangeAsync(definition.KpiDefinitionId, cycle.WindowFrom - size, cycle.WindowTo, ct).ConfigureAwait(false);
            foreach (var bucket in range)
            {
                var entity = ScoutShadowEntityKey.FromKpi(bucket.EntityKey);
                if (entity is null || !flaggedEntities.Contains(entity)) continue;
                if (lastEvaluated.TryGetValue(entity, out var last) && last >= bucket.BucketStart && WasEvaluable(definition, bucket))
                {
                    evaluated.Add(new ScoutShadowEvaluatedBucket(definition.KpiDefinitionId, entity, bucket.BucketStart));
                }
            }
        }

        return new ScoutShadowInput(cycle.RunId, cycle.Board, profileKey, cycle.MinPriorityRank, cycle.WindowFrom, cycle.WindowTo,
            ruleCases, methodDefinitions, methodAnomalies, evaluated);
    }

    /// <summary>Compares and upserts; returns the counts of the rows written by this cycle.</summary>
    public async Task<ScoutShadowCounts> RecordAsync(ScoutShadowCycle cycle, CancellationToken ct = default)
    {
        var input = await BuildInputAsync(cycle, ct).ConfigureAwait(false);
        var rows = ScoutShadowComparator.Compare(input);
        if (rows.Count > 0)
        {
            await store.AddRangeAsync(rows, ct).ConfigureAwait(false);
        }

        return new ScoutShadowCounts(
            rows.Count(r => r.Agreement == ScoutShadowAgreement.Both),
            rows.Count(r => r.Agreement == ScoutShadowAgreement.RulesOnly),
            rows.Count(r => r.Agreement == ScoutShadowAgreement.MethodOnly));
    }

    /// <summary>
    /// The evaluator skips a bucket below MinObservationsPerBucket or without a value for the aggregation, yet still moves
    /// LastBucketEvaluated past it - such a bucket is "not-evaluated", not "normal". (A missing reference, e.g. too little
    /// history, is not visible from the stores and still reads as evaluated.)
    /// </summary>
    public static bool WasEvaluable(KpiDefinition definition, KpiBucket bucket) =>
        bucket.TotalCount >= definition.MinObservationsPerBucket
        && KpiStatistics.Observed(bucket, definition.Aggregation) is not null;

    private static readonly string[] ServiceKeyColumns = ["ServiceKey"];
    private static readonly string[] StepKeyColumns = ["StepKey"];

    /// <summary>
    /// Null when the row identifies no entity. Service and step come from the board's own ServiceKey/StepKey columns
    /// (the company board's KQL classifier, the one the entity KPI template uses) and otherwise from the profile
    /// classification of the row; an unresolved step keeps the board's stage label.
    /// </summary>
    public static ScoutShadowRuleCase? ToRuleCase(ICompanyAdapter adapter, ScoutBoardRow row)
    {
        var entity = ScoutShadowEntityKey.FromBoardRow(row);
        if (entity is null) return null;

        var service = ScoutShadowEntityKey.ReadColumn(row.GroupPayloadJson, ServiceKeyColumns)
            ?? ScoutShadowEntityKey.ReadColumn(row.RawRowJson, ServiceKeyColumns);
        var step = ScoutShadowEntityKey.ReadColumn(row.GroupPayloadJson, StepKeyColumns)
            ?? ScoutShadowEntityKey.ReadColumn(row.RawRowJson, StepKeyColumns);
        if (service is null || step is null)
        {
            var classification = ScoutRowMapper.Classify(adapter, row);
            service ??= classification.Service;
            step ??= classification.StepIsResolved && classification.Step is { Length: > 0 } resolved ? resolved : null;
        }

        return new ScoutShadowRuleCase(entity, service ?? "", step ?? row.Stage ?? "", row.WindowFrom, row.PriorityRank);
    }
}
