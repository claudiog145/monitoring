using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Configuration;
using JourneyMonitor.Core.Domain;
using Microsoft.Extensions.Logging;

namespace JourneyMonitor.Core.Monitoring.Evidence;

/// <summary>
/// Application Health evidence for a triage: the signal profile's Application KPI buckets of the
/// domain inside the window plus the domain's incidents seen in it (incumbent only - incidents are
/// not stored per profile). Either half may fail on its own; the section is unavailable only when both did.
/// Entity-grain KPIs go to <c>EntityKpis</c> as a compact view of their top entities (see
/// <see cref="EntityEvidence"/>), last and within its char budget, so the prompt's per-section cut keeps
/// the domain series and incidents first. An entity read failure only adds an <c>Errors</c> entry.
/// Order against Triage's 4,000-char cut: <c>Kpis</c>, then <c>Focus</c> (a partner signal's own
/// series, ~300 chars), then <c>Incidents</c> (most severe and most recent first, capped at
/// <see cref="MaxIncidentChars"/>, the rest counted in <c>IncidentsTruncated</c>), then <c>EntityKpis</c>.
/// </summary>
public sealed class ApplicationEvidenceProvider(
    IKpiDefinitionStore definitions,
    IKpiBucketStore buckets,
    IIncidentReader incidents,
    ILogger<ApplicationEvidenceProvider> logger,
    IKpiAnomalyStore? anomalies = null) : IEvidenceProvider
{
    public const string IncidentEnvironment = InsightsEnvironment.Production;
    private const int MaxKpis = 20;
    private const int MaxBucketsPerKpi = 48;
    private const int MaxIncidents = 10;
    private const int MaxMessageLength = 300;
    /// <summary>Serialized size cap of <c>Incidents</c>; what does not fit is counted in <c>IncidentsTruncated</c>.</summary>
    public const int MaxIncidentChars = 1_500;

    public string Dimension => TriageDimensions.ApplicationHealth;

    public async Task<EvidenceSection> CollectAsync(TriageSignal signal, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken ct = default)
    {
        var domain = string.IsNullOrWhiteSpace(signal.Domain) ? null : signal.Domain.Trim().ToLowerInvariant();
        var profileKey = EvidenceProfile.KeyOf(signal);
        var errors = new List<string>();

        object? kpis = null;
        IReadOnlyList<EntityEvidence.EntityKpiEvidence>? entityKpis = null;
        IReadOnlyList<KpiDefinition> entityDefinitions = [];
        try
        {
            var enabled = await definitions.GetEnabledAsync(KpiPillar.Application, profileKey, ct);
            var own = enabled
                .Where(d => string.Equals(d.ProfileKey, profileKey, StringComparison.OrdinalIgnoreCase))
                .Where(d => domain is null || string.Equals(d.Domain, domain, StringComparison.OrdinalIgnoreCase))
                .ToList();
            entityDefinitions = own.Where(d => d.Grain == KpiGrain.Entity).ToList();
            var selected = own.Where(d => d.Grain == KpiGrain.Domain).Take(MaxKpis).ToList();

            var list = new List<object>(selected.Count);
            foreach (var definition in selected)
            {
                var range = await buckets.GetRangeAsync(definition.KpiDefinitionId, windowStart, windowEnd, ct);
                list.Add(new
                {
                    definition.KpiDefinitionId,
                    definition.Kpi,
                    definition.Domain,
                    definition.Stage,
                    Aggregation = definition.Aggregation.ToString(),
                    Buckets = range.TakeLast(MaxBucketsPerKpi).Select(b => new
                    {
                        b.BucketStart,
                        b.TotalCount,
                        b.P50,
                        b.P95,
                        b.Mean,
                        b.Value,
                    }).ToList(),
                });
            }
            kpis = list;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Triage] application KPI evidence failed.");
            errors.Add("kpis: " + ex.Message);
        }

        if (entityDefinitions.Count > 0)
        {
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
                logger.LogWarning(ex, "[Triage] application entity KPI evidence failed.");
                errors.Add("entityKpis: " + ex.Message);
            }
        }
        else if (kpis is not null)
        {
            entityKpis = [];
        }

        object? recentIncidents = null;
        string? incidentsTruncated = null;
        try
        {
            // Incident rows carry no profile: they come from the incumbent's exception ingestion, so
            // another company's triage must not see them.
            if (IncidentsTracked(profileKey))
            {
                (recentIncidents, incidentsTruncated) = await ReadIncidentsAsync(domain, windowStart, windowEnd, ct);
            }
            else
            {
                recentIncidents = Array.Empty<object>();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Triage] application incident evidence failed.");
            errors.Add("incidents: " + ex.Message);
        }

        if (kpis is null && recentIncidents is null)
        {
            return new EvidenceSection(Dimension, false, null, "error: " + Truncate(string.Join("; ", errors), 200));
        }

        var data = EvidenceJson.ToElement(new
        {
            Window = new { From = windowStart, To = windowEnd },
            ProfileKey = profileKey,
            Domain = domain,
            Kpis = kpis,
            Focus = EntityEvidence.PartnerFocusOf(signal, entityKpis),
            Incidents = recentIncidents,
            IncidentsTruncated = incidentsTruncated,
            Errors = errors,
            EntityKpis = entityKpis,
        });
        return new EvidenceSection(Dimension, true, data, null);
    }

    private async Task<(object Incidents, string? Truncated)> ReadIncidentsAsync(string? domain, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken ct)
    {
        var rows = await incidents.ListAsync(
            new IncidentReadQuery(domain, null, IncidentEnvironment, windowStart, null, MaxIncidents),
            ct);
        var all = rows
            .Where(r => r.FirstSeen <= windowEnd)
            .OrderBy(r => SeverityRank(r.Severity))
            .ThenByDescending(r => r.LastSeen)
            .Select(r => (object)new
            {
                r.ExceptionType,
                r.Component,
                r.Operation,
                Message = r.Message.Length <= MaxMessageLength ? r.Message : r.Message[..MaxMessageLength],
                r.Severity,
                r.Status,
                r.FirstSeen,
                r.LastSeen,
                r.OccurrenceCount,
                r.UsersAffected,
            })
            .ToList();

        // Most severe and most recent first, then as many as fit the budget (always at least one).
        var kept = new List<object>(all.Count);
        foreach (var incident in all)
        {
            kept.Add(incident);
            if (kept.Count > 1 && EvidenceJson.ToElement(kept).GetRawText().Length > MaxIncidentChars)
            {
                kept.RemoveAt(kept.Count - 1);
                break;
            }
        }
        var omitted = all.Count - kept.Count;
        return (kept, omitted > 0 ? $"truncated: {omitted} more" : null);
    }

    private static int SeverityRank(string? severity) => severity?.Trim().ToLowerInvariant() switch
    {
        "critical" or "sev0" or "sev1" => 0,
        "high" or "error" or "sev2" => 1,
        "medium" or "warning" or "sev3" => 2,
        "low" or "info" or "sev4" => 3,
        _ => 4,
    };

    private static bool IncidentsTracked(string profileKey) => EvidenceProfile.IsIncumbent(profileKey);

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
