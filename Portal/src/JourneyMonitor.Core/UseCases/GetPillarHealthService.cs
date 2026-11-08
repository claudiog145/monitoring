using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;

namespace JourneyMonitor.Core.UseCases;

/// <summary>
/// Read model behind GET /api/health/pillars: always the three pillars, in Business, Application,
/// Infrastructure order, so the portal can render a fixed layout even when a pillar has no KPIs.
/// </summary>
public sealed class GetPillarHealthService(
    IKpiDefinitionStore definitions,
    IKpiBucketStore buckets,
    IKpiStateStore states,
    IKpiAnomalyStore anomalies,
    TimeProvider time) : IPillarHealthReader
{
    public const string StatusOffline = "offline";
    public const string StatusAnomalous = "anomalous";
    public const string StatusOk = "ok";

    public static readonly TimeSpan LatestBucketWindow = TimeSpan.FromHours(48);
    // LastAnomaly is looked up in this many newest anomalies per pillar; a KPI whose last anomaly is
    // older than that window reports null, which is acceptable for a "what is happening now" view.
    private const int RecentAnomaliesPerPillar = 500;

    private static readonly KpiPillar[] PillarOrder = [KpiPillar.Business, KpiPillar.Application, KpiPillar.Infrastructure];

    public async Task<IReadOnlyList<PillarHealth>> GetAsync(string? domain, string? profileKey = null, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        var domainFilter = string.IsNullOrWhiteSpace(domain) ? null : domain.Trim();
        var all = await definitions.GetAllAsync(profileKey, ct);

        var result = new List<PillarHealth>(PillarOrder.Length);
        foreach (var pillar in PillarOrder)
        {
            // Contract v4 §3: entity-grain KPIs never change pillar status, so they are not pillar rows either.
            var kpis = all
                .Where(d => d.Pillar == pillar && d.Grain == KpiGrain.Domain)
                .Where(d => domainFilter is null || string.Equals(d.Domain, domainFilter, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var lastAnomalyByKpi = new Dictionary<int, KpiAnomaly>();
            if (kpis.Count > 0)
            {
                var recent = await anomalies.GetRecentAsync(pillar, domainFilter, RecentAnomaliesPerPillar, profileKey, ct);
                foreach (var anomaly in recent)
                {
                    if (!lastAnomalyByKpi.TryGetValue(anomaly.KpiDefinitionId, out var existing) || anomaly.DetectedAt > existing.DetectedAt)
                        lastAnomalyByKpi[anomaly.KpiDefinitionId] = anomaly;
                }
            }

            var health = new List<KpiHealth>(kpis.Count);
            foreach (var kpi in kpis)
            {
                var range = await buckets.GetRangeAsync(kpi.KpiDefinitionId, now - LatestBucketWindow, now, ct);
                var latest = range.Count == 0 ? null : range.MaxBy(b => b.BucketStart);
                var state = await states.GetAsync(kpi.KpiDefinitionId, ct);
                lastAnomalyByKpi.TryGetValue(kpi.KpiDefinitionId, out var lastAnomaly);
                health.Add(new KpiHealth(kpi.KpiDefinitionId, kpi.Kpi, kpi.Domain, kpi.Stage, kpi.Direction, kpi.Enabled, latest, state, lastAnomaly)
                {
                    ProfileKey = kpi.ProfileKey,
                });
            }

            result.Add(new PillarHealth(pillar, KpiPillarNames.ToDisplay(pillar), StatusFor(health, now), health)
            {
                ProfileKey = string.IsNullOrWhiteSpace(profileKey) ? null : profileKey.Trim(),
            });
        }

        return result;
    }

    private static string StatusFor(IReadOnlyList<KpiHealth> kpis, DateTimeOffset now)
    {
        var enabled = kpis.Where(k => k.Enabled).ToList();
        if (enabled.Count == 0) return StatusOffline;
        return enabled.Any(k => k.State.ConsecutiveCount > 0 || k.State.CooldownUntil > now) ? StatusAnomalous : StatusOk;
    }
}
