using System.Text.Json;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring.Kpi;

namespace JourneyMonitor.Core.Monitoring.Evidence;

/// <summary>
/// Entity-grain series for the Application and Business evidence sections. Entity KPIs keep no
/// domain aggregate, so their evidence is a compact view of the top entities of the window.
///
/// Selection, per KPI: the signal's partner entity (stage <c>partner:{key}</c>) first, then the
/// entities with an anomaly in the window (largest |deviation| first), then the rest by their largest
/// deviation from their own window mean (relative when the mean is not 0), then by their peak value
/// (Value, else P95, else Mean), then by key.
///
/// Shape: every chosen entity is a one-line summary (latest, window mean, max, deviation % of the
/// point furthest from the mean, anomaly flag). Only the focus entity - the partner of a partner
/// signal, otherwise the top anomalous entity - also carries its raw series: the last
/// <see cref="MaxFocusPoints"/> values on the bucket grid plus its strongest anomaly.
///
/// Size: Triage cuts every section at 4,000 chars of JSON, so the providers put this part after their
/// domain content and it is held to <see cref="MaxPayloadChars"/> chars. A partner signal's own series
/// is also emitted early as <see cref="PartnerFocus"/>, so it reaches the prompt whatever follows.
/// Limits before the budget: <see cref="MaxEntityKpis"/> KPIs (the signal's own KPI first) and
/// <see cref="DefaultTopEntities"/> entities per KPI. Over budget, the lowest-ranked non-focus entity of
/// the KPI that has the most is dropped first; then whole KPIs from the end. Under heavy load (many
/// anomalous entities, long keys) only the focus entity of each KPI may remain, and the last KPIs may go.
/// The budget is measured before redaction: redaction tokens (<c>[REDACTED:email]</c>, 16 chars) can
/// lengthen short keys, at worst about +200 chars over the budget for 15 short keys plus 3 focus keys.
/// Anomalies are read per series, only for the partner entity and the <see cref="AnomalyCandidatesPerKpi"/>
/// most deviating entities, so a KPI costs at most 1 + 21 reads; a flat anomalous entity ranked lower is not flagged.
///
/// Entity keys go out as values (never as property names), so the Triage redactor (AG6) sees and masks them.
/// </summary>
public static class EntityEvidence
{
    public const int DefaultTopEntities = 5;
    public const int MaxEntityKpis = 3;
    public const int MaxFocusPoints = 12;
    public const int MaxPayloadChars = 1_500;
    public const int AnomalyCandidatesPerKpi = 20;
    private const int AnomalyLookupPerEntity = 20;

    public sealed record EntityKpiEvidence(
        int KpiDefinitionId,
        string Kpi,
        string Domain,
        string Direction,
        int EntitiesInWindow,
        IReadOnlyList<EntitySummary> Entities,
        FocusSeries? Focus);

    public sealed record EntitySummary(
        string EntityKey,
        double? Latest,
        double? Mean,
        double? Max,
        double? DeviationPercent,
        bool Anomalous,
        bool Focus);

    /// <summary>Values on the bucket grid from <see cref="From"/>, one every <see cref="BucketMinutes"/>; null where the entity had no bucket.</summary>
    public sealed record FocusSeries(
        string EntityKey,
        DateTimeOffset From,
        int BucketMinutes,
        IReadOnlyList<double?> Values,
        FocusAnomaly? Anomaly);

    public sealed record FocusAnomaly(DateTimeOffset BucketStart, double Observed, double Reference, string Detection);

    /// <summary>
    /// The partner of a <c>partner:{key}</c> signal and its series, emitted by the providers ahead of
    /// their long lists so it survives Triage's per-section cut. <see cref="Series"/> is null when the
    /// partner had no buckets in the window (or the entity read failed).
    /// </summary>
    public sealed record PartnerFocus(string PartnerKey, int? KpiDefinitionId, string? Kpi, FocusSeries? Series);

    /// <summary>The partner focus of a partner signal from collected entity evidence; null for any other signal.</summary>
    public static PartnerFocus? PartnerFocusOf(TriageSignal signal, IReadOnlyList<EntityKpiEvidence>? evidence)
    {
        var partnerKey = PartnerKeyOf(signal);
        if (partnerKey is null) return null;
        var match = evidence?.FirstOrDefault(k =>
            k.Focus is { } focus && string.Equals(focus.EntityKey, partnerKey, StringComparison.OrdinalIgnoreCase));
        return new PartnerFocus(partnerKey, match?.KpiDefinitionId, match?.Kpi, match?.Focus);
    }

    /// <summary>The partner key of a <c>partner:{key}</c> signal; null for any other stage.</summary>
    public static string? PartnerKeyOf(TriageSignal signal)
    {
        var stage = signal.Stage?.Trim();
        if (string.IsNullOrEmpty(stage) || !stage.StartsWith(PartnerKpi.StagePrefix, StringComparison.OrdinalIgnoreCase)) return null;
        var key = stage[PartnerKpi.StagePrefix.Length..].Trim();
        return key.Length == 0 ? null : key;
    }

    /// <summary>Serialized size, in chars, with the options the evidence is stored and prompted with.</summary>
    public static int SizeOf(IReadOnlyList<EntityKpiEvidence> evidence) =>
        JsonSerializer.Serialize(evidence, TriageJson.Options).Length;

    /// <summary>
    /// One entry per entity KPI that has entity buckets in [windowStart, windowEnd), within
    /// <see cref="MaxPayloadChars"/>. Definitions must already be filtered to the signal's profile and domain.
    /// </summary>
    public static async Task<IReadOnlyList<EntityKpiEvidence>> CollectAsync(
        IEnumerable<KpiDefinition> entityDefinitions,
        TriageSignal signal,
        string profileKey,
        IKpiBucketStore buckets,
        IKpiAnomalyStore? anomalies,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        CancellationToken ct,
        int topEntities = DefaultTopEntities)
    {
        var top = Math.Max(1, topEntities);
        var partnerKey = PartnerKeyOf(signal);
        var ordered = entityDefinitions
            .Where(d => d.Grain == KpiGrain.Entity)
            .OrderByDescending(d => string.Equals(d.Kpi, signal.Kpi, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(d => partnerKey is not null && PartnerKpi.IsPartnerDefinition(d))
            .ThenBy(d => d.KpiDefinitionId)
            .Take(MaxEntityKpis)
            .ToList();

        var result = new List<EntityKpiEvidence>(ordered.Count);
        foreach (var definition in ordered)
        {
            var rows = await buckets.GetEntityRangeAsync(definition.KpiDefinitionId, windowStart, windowEnd, ct).ConfigureAwait(false);
            var series = rows
                .Where(b => !string.IsNullOrEmpty(b.EntityKey))
                .GroupBy(b => b.EntityKey, StringComparer.OrdinalIgnoreCase)
                .Select(g => Series.Of(g.Key, g.OrderBy(b => b.BucketStart).ToList()))
                .OrderByDescending(s => s.Score)
                .ThenByDescending(s => s.Max ?? double.MinValue)
                .ThenBy(s => s.EntityKey, StringComparer.Ordinal)
                .ToList();
            if (series.Count == 0) continue;

            var partner = partnerKey is null
                ? null
                : series.FirstOrDefault(s => string.Equals(s.EntityKey, partnerKey, StringComparison.OrdinalIgnoreCase));

            var anomaliesByEntity = new Dictionary<string, List<KpiAnomaly>>(StringComparer.OrdinalIgnoreCase);
            if (anomalies is not null)
            {
                var candidates = series.Take(AnomalyCandidatesPerKpi).ToList();
                if (partner is not null && !candidates.Contains(partner)) candidates.Insert(0, partner);
                foreach (var candidate in candidates)
                {
                    var recent = await anomalies.GetRecentAsync(
                        definition.Pillar, definition.Domain, AnomalyLookupPerEntity, profileKey, candidate.EntityKey, ct).ConfigureAwait(false);
                    var inWindow = recent
                        .Where(a => a.KpiDefinitionId == definition.KpiDefinitionId)
                        .Where(a => (a.DetectedAt >= windowStart && a.DetectedAt < windowEnd) || (a.BucketStart >= windowStart && a.BucketStart < windowEnd))
                        .OrderByDescending(a => Math.Abs(a.Deviation))
                        .ThenBy(a => a.BucketStart)
                        .ToList();
                    if (inWindow.Count > 0) anomaliesByEntity[candidate.EntityKey] = inWindow;
                }
            }

            var anomalous = series
                .Where(s => !ReferenceEquals(s, partner) && anomaliesByEntity.ContainsKey(s.EntityKey))
                .OrderByDescending(s => anomaliesByEntity[s.EntityKey].Max(a => Math.Abs(a.Deviation)))
                .ThenBy(s => s.EntityKey, StringComparer.Ordinal)
                .ToList();
            var chosen = new List<Series>(top);
            if (partner is not null) chosen.Add(partner);
            chosen.AddRange(anomalous);
            chosen.AddRange(series.Where(s => !ReferenceEquals(s, partner) && !anomaliesByEntity.ContainsKey(s.EntityKey)));
            chosen = chosen.Take(top).ToList();

            var focus = partner ?? anomalous.FirstOrDefault();
            FocusSeries? focusSeries = null;
            if (focus is not null)
            {
                var strongest = anomaliesByEntity.TryGetValue(focus.EntityKey, out var found) ? found[0] : null;
                focusSeries = Grid(focus, definition.BucketSize) with
                {
                    Anomaly = strongest is null
                        ? null
                        : new FocusAnomaly(strongest.BucketStart, Round(strongest.ObservedValue), Round(strongest.ReferenceValue), Truncate(strongest.Detection, 120)),
                };
            }

            result.Add(new EntityKpiEvidence(
                definition.KpiDefinitionId,
                definition.Kpi,
                definition.Domain,
                definition.Direction.ToString(),
                series.Count,
                chosen.Select(s => new EntitySummary(
                    s.EntityKey,
                    Round(s.Latest),
                    Round(s.Mean),
                    Round(s.Max),
                    Round(s.DeviationPercent),
                    anomaliesByEntity.ContainsKey(s.EntityKey),
                    ReferenceEquals(s, focus))).ToList(),
                focusSeries));
        }
        return Fit(result, MaxPayloadChars);
    }

    /// <summary>Trims <paramref name="evidence"/> to <paramref name="budget"/> chars; see the class remarks for the order.</summary>
    public static IReadOnlyList<EntityKpiEvidence> Fit(IReadOnlyList<EntityKpiEvidence> evidence, int budget)
    {
        var current = evidence.ToList();
        while (current.Count > 0 && SizeOf(current) > budget)
        {
            var index = -1;
            var most = 0;
            for (var i = current.Count - 1; i >= 0; i--)
            {
                var removable = current[i].Entities.Count(e => !e.Focus);
                if (removable > most)
                {
                    most = removable;
                    index = i;
                }
            }

            if (index >= 0)
            {
                var entities = current[index].Entities.ToList();
                entities.RemoveAt(entities.FindLastIndex(e => !e.Focus));
                current[index] = current[index] with { Entities = entities };
            }
            else
            {
                current.RemoveAt(current.Count - 1);
            }
        }
        return current;
    }

    private static FocusSeries Grid(Series series, TimeSpan bucketSize)
    {
        var last = series.Buckets[^1].BucketStart;
        if (bucketSize <= TimeSpan.Zero)
        {
            var tail = series.Buckets.TakeLast(MaxFocusPoints).ToList();
            return new FocusSeries(series.EntityKey, tail[0].BucketStart, 0, tail.Select(b => Round(Metric(b))).ToList(), null);
        }

        var from = last - bucketSize * (MaxFocusPoints - 1);
        var firstData = series.Buckets[0].BucketStart;
        if (firstData > from)
        {
            // Fewer points than the grid: start at the entity's first bucket instead of padding with nulls.
            var slots = (int)((last - firstData).Ticks / bucketSize.Ticks);
            from = last - bucketSize * slots;
        }
        var count = (int)((last - from).Ticks / bucketSize.Ticks) + 1;
        var values = new double?[count];
        foreach (var bucket in series.Buckets.Where(b => b.BucketStart >= from))
        {
            var slot = (int)((bucket.BucketStart - from).Ticks / bucketSize.Ticks);
            if (slot >= 0 && slot < count) values[slot] = Round(Metric(bucket));
        }
        return new FocusSeries(series.EntityKey, from, (int)bucketSize.TotalMinutes, values, null);
    }

    private static double? Metric(KpiBucket bucket) => bucket.Value ?? bucket.P95 ?? bucket.Mean;

    private static double? Round(double? value) => value is { } v ? Round(v) : null;

    private static double Round(double value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    private sealed record Series(
        string EntityKey,
        IReadOnlyList<KpiBucket> Buckets,
        double? Latest,
        double? Mean,
        double? Max,
        double? DeviationPercent,
        double Score)
    {
        public static Series Of(string entityKey, IReadOnlyList<KpiBucket> buckets)
        {
            var values = buckets.Select(Metric).Where(v => v.HasValue).Select(v => v!.Value).ToList();
            if (values.Count == 0) return new Series(entityKey, buckets, null, null, null, null, 0);
            var mean = values.Average();
            var extreme = values.OrderByDescending(v => Math.Abs(v - mean)).First();
            var deviation = Math.Abs(extreme - mean);
            var score = mean == 0 ? deviation : deviation / Math.Abs(mean);
            double? percent = mean == 0 ? null : (extreme - mean) / Math.Abs(mean) * 100;
            return new Series(entityKey, buckets, values[^1], mean, values.Max(), percent, score);
        }
    }
}
