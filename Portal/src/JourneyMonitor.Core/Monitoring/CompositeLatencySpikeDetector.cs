using System.Text.Json;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Core.Monitoring;

/// <summary>
/// Dispatches to the KQL-based detector or the SQL-journey-store-based detector per domain,
/// per <see cref="MonitoringOptions.SqlDetectorDomains"/> - the gradual, per-domain rollout
/// switch from the KQL-to-DB migration plan
/// (docs/product/latency-spike-doctor-monkey-db-migration-plan-2026-09-21.md). This is the type
/// registered as <see cref="ILatencySpikeDetector"/>; the two concrete detectors are resolved
/// directly by type, not through the interface, so they never end up dispatching to themselves.
///
/// For domains listed in <see cref="MonitoringOptions.ShadowDomains"/>, after the active detector
/// runs for real, the OTHER detector's <c>PreviewAsync</c> (a dry run with no Finding/repository
/// side effects) also runs and the two stage-cluster results are logged side by side - Fase 3's
/// validation step, so the SQL detector can be compared against KQL (or vice versa) without
/// either one interfering with the other's dedup state.
/// </summary>
public sealed class CompositeLatencySpikeDetector(
    LatencySpikeDetectorService kqlDetector,
    SqlLatencySpikeDetectorService sqlDetector,
    IOptions<MonitoringOptions> monitoringOptions,
    ILogger<CompositeLatencySpikeDetector> logger) : ILatencySpikeDetector
{
    public async Task<IReadOnlyList<Finding>> RunAsync(string domain, string environment, CancellationToken ct = default)
    {
        var options = monitoringOptions.Value;
        var useSql = options.UsesSqlDetector(domain);

        var findings = useSql
            ? await sqlDetector.RunAsync(domain, environment, ct)
            : await kqlDetector.RunAsync(domain, environment, ct);

        if (options.IsShadowDomain(domain))
        {
            await LogShadowComparisonAsync(domain, environment, useSql, findings, ct);
        }

        return findings;
    }

    private async Task LogShadowComparisonAsync(string domain, string environment, bool activeIsSql, IReadOnlyList<Finding> activeFindings, CancellationToken ct)
    {
        try
        {
            var shadowPreview = activeIsSql
                ? await kqlDetector.PreviewAsync(domain, environment, ct)
                : await sqlDetector.PreviewAsync(domain, environment, ct);

            var activeLabel = activeIsSql ? "sql" : "kql";
            var shadowLabel = activeIsSql ? "kql" : "sql";
            var activeSummary = activeFindings.Count == 0
                ? "none"
                : string.Join(", ", activeFindings.Select(f => $"{f.Stage}={ObservedCountFromEvidence(f.EvidenceJson)}"));
            var shadowSummary = shadowPreview.Count == 0
                ? "none"
                : string.Join(", ", shadowPreview.Select(p => $"{p.Stage}={p.ObservedCount}"));

            logger.LogInformation(
                "[LatencySpikeMonkey shadow] domain={Domain} active={ActiveLabel} [{ActiveSummary}] shadow={ShadowLabel} [{ShadowSummary}]",
                domain, activeLabel, activeSummary, shadowLabel, shadowSummary);
        }
        catch (Exception ex)
        {
            // The shadow comparison is diagnostics-only - never let it break the real detection run.
            logger.LogWarning(ex, "[LatencySpikeMonkey shadow] comparison failed for domain {Domain}", domain);
        }
    }

    private static string ObservedCountFromEvidence(string evidenceJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(evidenceJson);
            return doc.RootElement.TryGetProperty("observedCount", out var count) ? count.ToString() : "?";
        }
        catch (JsonException)
        {
            return "?";
        }
    }
}
