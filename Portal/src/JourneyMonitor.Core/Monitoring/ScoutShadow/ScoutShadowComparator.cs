using JourneyMonitor.Core.Domain;

namespace JourneyMonitor.Core.Monitoring.ScoutShadow;

/// <summary>One board row as the shadow sees it: normalised entity, the service and step keys, the 5-min interval and the rank.</summary>
/// <param name="EntityKey">Normalised (<see cref="ScoutShadowEntityKey"/>). Rows without an entity never reach the comparator.</param>
/// <param name="Service">The service key of the row ("" when unresolved).</param>
/// <param name="Stage">The step key of the row, or the board's own stage label when the step is unresolved.</param>
/// <param name="BucketStart">Start of the board's interval (the row's WindowFrom).</param>
public sealed record ScoutShadowRuleCase(string EntityKey, string Service, string Stage, DateTimeOffset BucketStart, int Rank);

/// <summary>An entity-grain KPI definition the board is compared with. Stage "" aggregates every stage (A1).</summary>
public sealed record ScoutShadowMethodDefinition(int KpiDefinitionId, string Domain, string Stage, TimeSpan BucketSize);

/// <summary>An entity anomaly; EntityKey is normalised (<see cref="ScoutShadowEntityKey.FromKpi"/>).</summary>
public sealed record ScoutShadowMethodAnomaly(long AnomalyId, int KpiDefinitionId, string EntityKey, DateTimeOffset BucketStart);

/// <summary>A bucket of one entity series the method actually evaluated (enough observations, a value, state moved past it).</summary>
public sealed record ScoutShadowEvaluatedBucket(int KpiDefinitionId, string EntityKey, DateTimeOffset BucketStart);

public sealed record ScoutShadowInput(
    Guid RunId,
    string Board,
    string ProfileKey,
    int MinPriorityRank,
    DateTimeOffset WindowFrom,
    DateTimeOffset WindowTo,
    IReadOnlyList<ScoutShadowRuleCase> RuleCases,
    IReadOnlyList<ScoutShadowMethodDefinition> Definitions,
    IReadOnlyList<ScoutShadowMethodAnomaly> Anomalies,
    IReadOnlyList<ScoutShadowEvaluatedBucket> Evaluated);

/// <summary>
/// Contract v4 §4 / §6 A1: pure comparison of the Scout's fixed rules with the entity detection method for one cycle.
///
/// - Rules flag a (EntityKey, Stage, BucketStart) when the board rank is at least the scout's InvestigationMinPriorityRank.
/// - A rules case is matched by a method anomaly of the same entity in a definition whose Domain is the row's service
///   and whose Stage is the row's stage or empty, when the anomaly bucket [BucketStart, BucketStart + BucketSize)
///   covers the start of the rules interval.
/// - Every key the window touches is emitted with its FINAL agreement, so the store's upsert on
///   (Board, ProfileKey, EntityKey, Stage, BucketStart) replaces what an earlier cycle wrote for it:
///   - each flagged rules interval: <c>both</c> when matched, else <c>rules-only</c> with MethodVerdict
///     <c>normal</c> (a matching definition evaluated the covering bucket) or <c>not-evaluated</c>;
///   - each window anomaly whose bucket starts inside the window, at its own key (entity, definition Stage, anomaly
///     bucket): <c>both</c> when a flagged rules interval matched it (RuleRank = the highest such rank), else
///     <c>method-only</c>. Without this an earlier <c>method-only</c> for the same anomaly would survive next to the
///     later <c>both</c>. A bucket that started before the window is not re-written: its earlier rules intervals are
///     no longer visible and the row would be downgraded (the store also never downgrades a <c>both</c>).
/// - Rows where neither side flags anything are never produced. Counts are per entity-hour incident (store).
/// </summary>
public static class ScoutShadowComparator
{
    private const int MaxEntityKeyLength = 256;
    private const int MaxStageLength = 64;

    public static IReadOnlyList<ScoutShadowComparison> Compare(ScoutShadowInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var definitions = input.Definitions
            .Where(d => d.BucketSize > TimeSpan.Zero)
            .GroupBy(d => d.KpiDefinitionId)
            .ToDictionary(g => g.Key, g => g.First());

        var anomalies = input.Anomalies
            .Where(a => !string.IsNullOrWhiteSpace(a.EntityKey)
                && definitions.TryGetValue(a.KpiDefinitionId, out var d)
                && a.BucketStart < input.WindowTo && a.BucketStart + d.BucketSize > input.WindowFrom)
            .OrderBy(a => a.AnomalyId)
            .ToList();

        var flagged = input.RuleCases
            .Where(c => c.Rank >= input.MinPriorityRank && !string.IsNullOrWhiteSpace(c.EntityKey))
            .Select(c => c with
            {
                EntityKey = Truncate(c.EntityKey, MaxEntityKeyLength),
                Service = c.Service ?? "",
                Stage = Truncate(c.Stage ?? "", MaxStageLength),
            })
            .GroupBy(c => Key(c.EntityKey, c.Stage, c.BucketStart))
            .Select(g => g.OrderByDescending(c => c.Rank).First())
            .OrderBy(c => c.BucketStart)
            .ThenBy(c => c.EntityKey, StringComparer.Ordinal)
            .ToList();

        var rows = new Dictionary<(string, string, DateTimeOffset), ScoutShadowComparison>();
        var matchedRank = new Dictionary<long, int>();

        foreach (var rule in flagged)
        {
            var matching = definitions.Values.Where(d => Matches(d, rule)).ToList();

            var anomaly = anomalies
                .Where(a => matching.Any(d => d.KpiDefinitionId == a.KpiDefinitionId)
                    && Same(a.EntityKey, rule.EntityKey)
                    && Covers(a.BucketStart, definitions[a.KpiDefinitionId].BucketSize, rule.BucketStart))
                // The definition of the row's own stage is the closer match than the all-stages one.
                .OrderBy(a => string.IsNullOrEmpty(definitions[a.KpiDefinitionId].Stage) ? 1 : 0)
                .ThenBy(a => a.AnomalyId)
                .FirstOrDefault();

            ScoutShadowComparison row;
            if (anomaly is not null)
            {
                matchedRank[anomaly.AnomalyId] = Math.Max(rule.Rank, matchedRank.GetValueOrDefault(anomaly.AnomalyId));
                row = Row(input, rule.EntityKey, rule.Stage, rule.BucketStart, rule.Rank,
                    ScoutShadowMethodVerdict.Anomalous, anomaly.AnomalyId, ScoutShadowAgreement.Both);
            }
            else
            {
                var evaluated = input.Evaluated.Any(e => matching.Any(d => d.KpiDefinitionId == e.KpiDefinitionId
                    && Same(e.EntityKey, rule.EntityKey)
                    && Covers(e.BucketStart, d.BucketSize, rule.BucketStart)));
                row = Row(input, rule.EntityKey, rule.Stage, rule.BucketStart, rule.Rank,
                    evaluated ? ScoutShadowMethodVerdict.Normal : ScoutShadowMethodVerdict.NotEvaluated, null, ScoutShadowAgreement.RulesOnly);
            }
            Merge(rows, row);
        }

        foreach (var anomaly in anomalies)
        {
            // Only a bucket wholly inside the window has its rules side fully observable: an older one still matches
            // rules intervals above, but re-writing its own row could only lose the rules side (never downgrade).
            if (anomaly.BucketStart < input.WindowFrom) continue;
            var entity = Truncate(anomaly.EntityKey, MaxEntityKeyLength);
            var stage = Truncate(definitions[anomaly.KpiDefinitionId].Stage ?? "", MaxStageLength);
            var row = matchedRank.TryGetValue(anomaly.AnomalyId, out var rank)
                ? Row(input, entity, stage, anomaly.BucketStart, rank, ScoutShadowMethodVerdict.Anomalous, anomaly.AnomalyId, ScoutShadowAgreement.Both)
                : Row(input, entity, stage, anomaly.BucketStart, null, ScoutShadowMethodVerdict.Anomalous, anomaly.AnomalyId, ScoutShadowAgreement.MethodOnly);
            Merge(rows, row);
        }

        return rows.Values.ToList();
    }

    /// <summary>One row per key; <c>both</c> wins, otherwise the first row written for the key stands.</summary>
    private static void Merge(Dictionary<(string, string, DateTimeOffset), ScoutShadowComparison> rows, ScoutShadowComparison row)
    {
        var key = Key(row.EntityKey, row.Stage, row.BucketStart);
        if (!rows.TryGetValue(key, out var existing)
            || (row.Agreement == ScoutShadowAgreement.Both && existing.Agreement != ScoutShadowAgreement.Both))
        {
            rows[key] = row;
        }
    }

    private static bool Matches(ScoutShadowMethodDefinition definition, ScoutShadowRuleCase rule) =>
        rule.Service.Length > 0
        && string.Equals(definition.Domain, rule.Service, StringComparison.OrdinalIgnoreCase)
        && (string.IsNullOrEmpty(definition.Stage) || string.Equals(definition.Stage, rule.Stage, StringComparison.OrdinalIgnoreCase));

    private static bool Covers(DateTimeOffset bucketStart, TimeSpan bucketSize, DateTimeOffset intervalStart) =>
        bucketStart <= intervalStart && intervalStart < bucketStart + bucketSize;

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static (string, string, DateTimeOffset) Key(string entity, string stage, DateTimeOffset bucketStart) =>
        (entity.ToLowerInvariant(), stage.ToLowerInvariant(), bucketStart);

    private static ScoutShadowComparison Row(
        ScoutShadowInput input, string entity, string stage, DateTimeOffset bucketStart, int? rank,
        string verdict, long? anomalyId, string agreement) =>
        new(input.RunId, input.Board, input.ProfileKey, entity, stage, bucketStart, rank, verdict, anomalyId, agreement);

    private static string Truncate(string value, int max)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}
