using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using Microsoft.Extensions.Logging;

namespace JourneyMonitor.Core.Monitoring.Evidence;

/// <summary>
/// Business Health evidence for a profile without a dedicated business source (contract v4 §5):
/// the profile's own enabled Business-pillar KPIs at domain grain - the signal's domain when it
/// has one, otherwise every domain - with the buckets inside the window, the latest value against
/// a reference (the same window one week earlier) and the domain anomalies detected in the window.
/// The incumbent keeps its Fabric source; see <see cref="AppliesTo"/>. Entity-grain Business KPIs
/// go last to <c>EntityKpis</c>, a compact budgeted view of their top entities (see <see cref="EntityEvidence"/>);
/// an entity read failure only adds an <c>Errors</c> entry.
/// </summary>
public sealed class KpiBusinessEvidenceProvider(
    IKpiDefinitionStore definitions,
    IKpiBucketStore buckets,
    IKpiAnomalyStore anomalies,
    ILogger<KpiBusinessEvidenceProvider> logger) : IEvidenceProvider
{
    public const string SourceName = "kpi-buckets";
    public static readonly TimeSpan ReferenceOffset = TimeSpan.FromDays(7);
    private const int MaxKpis = 20;
    private const int MaxBucketsPerKpi = 48;
    private const int AnomalyLookup = 200;
    private const int MaxAnomalies = 25;

    public string Dimension => TriageDimensions.BusinessHealth;

    /// <summary>True when the signal's business evidence comes from KPI buckets: every profile but the incumbent, which has Fabric.</summary>
    public static bool AppliesTo(TriageSignal signal) => !EvidenceProfile.IsIncumbent(signal);

    public async Task<EvidenceSection> CollectAsync(TriageSignal signal, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken ct = default)
    {
        var profileKey = EvidenceProfile.KeyOf(signal);
        var domain = string.IsNullOrWhiteSpace(signal.Domain) ? null : signal.Domain.Trim().ToLowerInvariant();

        try
        {
            var enabled = await definitions.GetEnabledAsync(KpiPillar.Business, profileKey, ct);
            var own = enabled
                .Where(d => string.Equals(d.ProfileKey, profileKey, StringComparison.OrdinalIgnoreCase))
                .Where(d => domain is null || string.Equals(d.Domain, domain, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var selected = own
                .Where(d => d.Grain == KpiGrain.Domain)
                .OrderBy(d => d.KpiDefinitionId)
                .Take(MaxKpis)
                .ToList();
            var entityDefinitions = own.Where(d => d.Grain == KpiGrain.Entity).ToList();

            if (selected.Count == 0 && entityDefinitions.Count == 0)
            {
                return new EvidenceSection(Dimension, false, null,
                    $"no Business KPIs for profile {profileKey}{(domain is null ? "" : $" in {domain}")}");
            }

            var kpis = new List<object>(selected.Count);
            foreach (var definition in selected)
            {
                var range = await buckets.GetRangeAsync(definition.KpiDefinitionId, windowStart, windowEnd, ct);
                var reference = await buckets.GetRangeAsync(
                    definition.KpiDefinitionId, windowStart - ReferenceOffset, windowEnd - ReferenceOffset, ct);

                var latest = range.LastOrDefault(b => b.Value.HasValue);
                var referenceValue = Average(reference);
                double? deviation = latest?.Value is { } value && referenceValue is { } baseline ? value - baseline : null;
                double? deviationPercent = deviation is { } delta && referenceValue is { } r && r != 0 ? delta / Math.Abs(r) * 100 : null;

                kpis.Add(new
                {
                    definition.KpiDefinitionId,
                    definition.Kpi,
                    definition.Domain,
                    definition.Stage,
                    Aggregation = definition.Aggregation.ToString(),
                    Direction = definition.Direction.ToString(),
                    Latest = latest is null ? null : new { latest.BucketStart, latest.Value, latest.TotalCount },
                    Reference = new
                    {
                        From = windowStart - ReferenceOffset,
                        To = windowEnd - ReferenceOffset,
                        Value = referenceValue,
                        Buckets = reference.Count,
                    },
                    Deviation = deviation,
                    DeviationPercent = deviationPercent,
                    Buckets = range.TakeLast(MaxBucketsPerKpi).Select(b => new
                    {
                        b.BucketStart,
                        b.TotalCount,
                        b.FailedCount,
                        b.Value,
                    }).ToList(),
                });
            }

            var ids = selected.ToDictionary(d => d.KpiDefinitionId);
            var recent = await anomalies.GetRecentAsync(KpiPillar.Business, domain, AnomalyLookup, profileKey, "", ct);
            var inWindow = recent
                .Where(a => ids.ContainsKey(a.KpiDefinitionId))
                .Where(a => (a.DetectedAt >= windowStart && a.DetectedAt < windowEnd) || (a.BucketStart >= windowStart && a.BucketStart < windowEnd))
                .OrderBy(a => a.BucketStart)
                .Take(MaxAnomalies)
                .Select(a => new
                {
                    a.AnomalyId,
                    a.KpiDefinitionId,
                    ids[a.KpiDefinitionId].Kpi,
                    ids[a.KpiDefinitionId].Domain,
                    a.DetectedAt,
                    a.BucketStart,
                    a.ObservedValue,
                    a.ReferenceValue,
                    a.Deviation,
                    a.Detection,
                })
                .ToList();

            var errors = new List<string>();
            IReadOnlyList<EntityEvidence.EntityKpiEvidence>? entityKpis = null;
            try
            {
                entityKpis = await EntityEvidence.CollectAsync(
                    entityDefinitions, signal, profileKey, buckets, anomalies, windowStart, windowEnd, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[Triage] business entity KPI evidence failed for profile {ProfileKey}.", profileKey);
                var message = "entityKpis: " + ex.Message;
                errors.Add(message.Length <= 200 ? message : message[..200]);
            }

            var data = EvidenceJson.ToElement(new
            {
                Source = SourceName,
                ProfileKey = profileKey,
                Domain = domain,
                Window = new { From = windowStart, To = windowEnd },
                Kpis = kpis,
                Focus = EntityEvidence.PartnerFocusOf(signal, entityKpis),
                Anomalies = inWindow,
                Errors = errors,
                EntityKpis = entityKpis,
            });
            return new EvidenceSection(Dimension, true, data, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Triage] business KPI evidence failed for profile {ProfileKey}.", profileKey);
            var message = "error: " + ex.Message;
            return new EvidenceSection(Dimension, false, null, message.Length <= 200 ? message : message[..200]);
        }
    }

    private static double? Average(IReadOnlyList<KpiBucket> range)
    {
        var values = range.Where(b => b.Value.HasValue).Select(b => b.Value!.Value).ToList();
        return values.Count == 0 ? null : values.Average();
    }
}
