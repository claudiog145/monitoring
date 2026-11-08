using JourneyMonitor.Core.Analytics;
using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Domain;

namespace JourneyMonitor.Core.UseCases;

/// <summary>
/// Contract v6 §4.5: turns one company's stored counts, pillars, KPIs and investigations into the
/// overview sections. Pure: everything it is given is already that company's.
/// </summary>
public static class ProfileOverviewComposer
{
    /// <summary>
    /// The incumbent keeps the seven Executive Overview tiles. Another company has no booking funnel, so its
    /// tiles are named with its own flow label and the conversion tile is left out rather than shown empty.
    /// </summary>
    public static IReadOnlyList<OverviewKpi> Tiles(OverviewCounts counts, bool incumbent, OverviewLabels labels)
    {
        var standard = OverviewComposer.Tiles(counts);
        if (incumbent) return standard;

        var flow = string.IsNullOrWhiteSpace(labels.Flow) ? "Journey" : labels.Flow.Trim();
        var tiles = new List<OverviewKpi> { standard[0] };
        if (counts.JourneyTotal is null || counts.SuccessfulJourneys is null || counts.JourneyTotal.Value == 0)
        {
            tiles.Add(new OverviewKpi($"{flow} completion", "No data", "Completed / total in the selected window", "", "unknown"));
        }
        else
        {
            var notCompleted = counts.JourneyTotal.Value - counts.SuccessfulJourneys.Value;
            tiles.Add(new OverviewKpi(
                $"{flow} completion",
                $"{counts.SuccessfulJourneys.Value}/{counts.JourneyTotal.Value}",
                $"{notCompleted} not completed in window",
                notCompleted > 0 ? "down" : "up",
                "healthy"));
        }
        // Duration, critical incidents, open exceptions and Doctor diagnoses carry no company vocabulary.
        tiles.AddRange(standard.Skip(3));
        return tiles;
    }

    public static IReadOnlyList<OverviewWidget> Widgets(bool incumbent) =>
    [
        new(OverviewWidgetKeys.Tiles, true),
        new(OverviewWidgetKeys.Pillars, true),
        new(OverviewWidgetKeys.Kpis, true),
        new(OverviewWidgetKeys.Exceptions, true),
        new(OverviewWidgetKeys.Investigations, true),
        new(OverviewWidgetKeys.TopEntities, true),
        new(OverviewWidgetKeys.HotelBusinessHealth, incumbent,
            incumbent ? null : "This business health widget applies only to the original company."),
        new(OverviewWidgetKeys.ClubInventory, incumbent,
            incumbent ? null : "This inventory widget applies only to the original company."),
    ];

    public static OverviewLabels Labels(CompanyProfile profile) =>
        new(
            Label(profile.Labels?.Service, "Service"),
            Label(profile.Labels?.Flow, "Journey"),
            Label(profile.Labels?.Step, "Step"),
            Label(profile.Labels?.Entity, "Entity"));

    public static IReadOnlyList<OverviewPillar> Pillars(IReadOnlyList<PillarHealth> pillars, DateTimeOffset now) =>
        pillars
            .Select(p => new OverviewPillar(
                p.DisplayName,
                p.Status,
                p.Kpis.Count,
                p.Kpis.Count(k => k.Enabled && IsAnomalous(k, now))))
            .ToList();

    public static IReadOnlyList<OverviewKpiSummary> Kpis(IReadOnlyList<PillarHealth> pillars, string profileKey, DateTimeOffset now) =>
        pillars
            .SelectMany(p => p.Kpis
                .Where(k => string.Equals(k.ProfileKey, profileKey, StringComparison.OrdinalIgnoreCase))
                .Select(k => new OverviewKpiSummary(
                    k.KpiDefinitionId,
                    p.DisplayName,
                    k.Kpi,
                    k.Domain,
                    k.Stage,
                    k.Enabled,
                    k.Enabled && IsAnomalous(k, now),
                    k.LatestBucket?.Value ?? k.LatestBucket?.P95,
                    k.LatestBucket?.BucketStart)))
            .OrderByDescending(k => k.Anomalous)
            .ThenBy(k => k.Pillar, StringComparer.Ordinal)
            .ThenBy(k => k.KpiDefinitionId)
            .ToList();

    public static IReadOnlyList<OverviewInvestigation> Investigations(IReadOnlyList<TriageInvestigation> investigations, string profileKey) =>
        investigations
            .Where(i => string.Equals(i.Signal.ProfileKey ?? KpiProfiles.Default, profileKey, StringComparison.OrdinalIgnoreCase))
            .Select(i => new OverviewInvestigation(
                i.InvestigationId,
                i.Status.ToString(),
                i.AlertLevel,
                i.Signal.Pillar,
                i.Signal.Kpi,
                i.Signal.Domain,
                i.Signal.Stage,
                i.CreatedAt,
                i.Result?.Assessment.Summary))
            .ToList();

    /// <summary>Entities ranked by incidents, then by affected journeys; both sources merged by key.</summary>
    public static IReadOnlyList<OverviewEntity> TopEntities(
        IReadOnlyList<OverviewEntity> incidentEntities,
        IReadOnlyList<OverviewEntity> journeyEntities,
        int top)
    {
        var merged = new Dictionary<string, OverviewEntity>(StringComparer.OrdinalIgnoreCase);
        foreach (var entity in incidentEntities.Concat(journeyEntities))
        {
            if (string.IsNullOrWhiteSpace(entity.Key)) continue;
            var key = entity.Key.Trim();
            merged[key] = merged.TryGetValue(key, out var existing)
                ? new OverviewEntity(
                    existing.Key,
                    existing.Incidents + entity.Incidents,
                    existing.Occurrences + entity.Occurrences,
                    existing.AffectedJourneys + entity.AffectedJourneys)
                : entity with { Key = key };
        }

        return merged.Values
            .Where(e => e.Incidents > 0 || e.Occurrences > 0 || e.AffectedJourneys > 0)
            .OrderByDescending(e => e.Incidents)
            .ThenByDescending(e => e.Occurrences)
            .ThenByDescending(e => e.AffectedJourneys)
            .ThenBy(e => e.Key, StringComparer.Ordinal)
            .Take(Math.Max(0, top))
            .ToList();
    }

    private static bool IsAnomalous(KpiHealth kpi, DateTimeOffset now) =>
        kpi.State.ConsecutiveCount > 0 || kpi.State.CooldownUntil > now;

    private static string Label(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}
