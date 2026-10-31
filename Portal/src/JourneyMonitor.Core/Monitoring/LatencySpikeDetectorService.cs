using System.Text.Json;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Configuration;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Options;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Core.Monitoring;

/// <summary>
/// The Latency Spike Monkey's detector: runs the domain's per-transaction latency-spike KQL,
/// groups requests slower than the domain's resolved threshold (per-domain override from
/// <see cref="ILatencySpikeSettingsResolver"/>, falling back to <see cref="MonitoringOptions"/>)
/// by stage within the rolling window, and raises a Finding (EvidenceJson populated at creation —
/// see FailedCallDetector precedent in the design doc) when the occurrence threshold is met.
/// Dedups against any already-open Finding for the same domain+stage.
/// </summary>
public sealed class LatencySpikeDetectorService(
    ITelemetrySource telemetrySource,
    IKqlQueryTextProvider kqlProvider,
    IOptions<InsightsOptions> insightsOptions,
    IOptions<MonitoringOptions> monitoringOptions,
    ILatencySpikeSettingsResolver settingsResolver,
    IFindingsRepository findingsRepository) : ILatencySpikeDetector
{
    public async Task<IReadOnlyList<Finding>> RunAsync(string domain, string environment, CancellationToken ct = default)
    {
        var options = await settingsResolver.ResolveAsync(domain, ct);
        var groups = await ComputeSpikeGroupsAsync(domain, environment, options, ct);

        var findings = new List<Finding>();
        foreach (var group in groups)
        {
            var items = group.ToList();

            var correlationId = $"{domain}:{group.Key}";

            var statusBreakdown = items
                .GroupBy(i => string.IsNullOrEmpty(i.ResultCode) ? "unknown" : i.ResultCode)
                .ToDictionary(g => g.Key, g => g.Count());

            var evidence = JsonSerializer.Serialize(new
            {
                stage = group.Key,
                spikeThresholdMs = options.ThresholdMs,
                occurrenceThreshold = options.OccurrenceThreshold,
                windowMinutes = options.WindowMinutes,
                observedCount = items.Count,
                maxDurationMs = items.Max(i => i.Ms),
                correlationIds = items.Select(i => i.CorrelationId).Where(c => !string.IsNullOrEmpty(c)).Distinct().Take(10).ToArray(),
                operationIds = items.Select(i => i.OperationId).Where(o => !string.IsNullOrEmpty(o)).Distinct().Take(10).ToArray(),
                statusCodeBreakdown = statusBreakdown,
            });

            var ratio = (double)items.Count / options.OccurrenceThreshold;
            var severity = ratio >= 3 ? "high" : ratio >= 1.5 ? "medium" : "low";
            var title = $"{items.Count} latency spikes (>{options.ThresholdMs}ms) in stage '{group.Key}' ({domain})";

            // Already-open findings are refreshed in place (new threshold/occurrence count) but
            // not re-added to the returned list - they must not be re-diagnosed by the Doctor
            // Monkey every cycle just because a Settings change (or new data) changed the numbers.
            var existing = await findingsRepository.GetOpenByCorrelationIdAsync(correlationId, ct);
            if (existing is not null)
            {
                existing.Title = title;
                existing.Severity = severity;
                existing.EvidenceJson = evidence;
                await findingsRepository.UpdateAsync(existing, ct);
                continue;
            }

            var finding = new Finding
            {
                Domain = domain,
                FindingType = Core.Domain.FindingType.LatencySpikeCluster,
                Stage = group.Key,
                Title = title,
                Severity = severity,
                EvidenceJson = evidence,
                OriginMonkeyKey = MonkeyKey.LatencySpikeMonkey,
                CorrelationId = correlationId,
            };

            findings.Add(await findingsRepository.AddAsync(finding, ct));
        }

        // A stage that used to qualify (e.g. before a Settings threshold/occurrence change) but
        // no longer does is otherwise never revisited - the loop above only reconsiders stages
        // CURRENTLY above threshold, so its finding would stay "open" forever with stale data.
        var stillQualifying = groups.Select(g => $"{domain}:{g.Key}").ToHashSet();
        await StaleFindingReconciler.ResolveStaleFindingsAsync(findingsRepository, domain, stillQualifying, ct);

        return findings;
    }

    /// <summary>
    /// Dry run for the Fase 3 shadow-mode comparison (see the KQL-to-DB migration plan): computes
    /// the same stage groups <see cref="RunAsync"/> would, but never touches
    /// <see cref="IFindingsRepository"/> - no dedup lookup, no Finding created. Safe to call
    /// alongside another detector's real run without interfering with its open-Finding state.
    /// </summary>
    public async Task<IReadOnlyList<SpikeGroupPreview>> PreviewAsync(string domain, string environment, CancellationToken ct = default)
    {
        var options = await settingsResolver.ResolveAsync(domain, ct);
        var groups = await ComputeSpikeGroupsAsync(domain, environment, options, ct);
        return groups
            .Select(g => new SpikeGroupPreview(g.Key, g.Count(), g.Max(i => i.Ms)))
            .ToList();
    }

    private async Task<List<IGrouping<string, SpikeRow>>> ComputeSpikeGroupsAsync(string domain, string environment, ResolvedLatencySettings options, CancellationToken ct)
    {
        var source = InsightsSourceResolver.ResolveForDomain(insightsOptions.Value, domain, environment);

        var kqlTemplate = kqlProvider.GetLatencySpikesKql(domain);
        // The floor is only meant to keep the KQL result volume small before C# applies the real
        // threshold - it must never exceed the domain's effective threshold, or a per-domain
        // override lower than the floor would have its matching rows discarded in KQL itself
        // before the C# grouping ever sees them (see MonitoringOptions.SpikeKqlFloorMs).
        var floorMs = Math.Min(monitoringOptions.Value.ResolvedSpikeKqlFloorMs, options.ThresholdMs);
        var kql = kqlTemplate
            .Replace("{{WINDOW_MINUTES}}", options.WindowMinutes.ToString())
            .Replace("{{FLOOR_MS}}", floorMs.ToString());

        var rows = await telemetrySource.QueryAsync(new TelemetryQueryRequest(kql, source.AppInsightsAppId, source.TenantId), ct);

        var spikeRows = rows
            .Select(r => new SpikeRow(
                Stage: r.GetString("Stage"),
                Ms: r.GetDouble("Ms") ?? 0,
                CorrelationId: r.GetString("CorrelationId"),
                OperationId: r.GetString("OperationId"),
                ResultCode: r.GetString("ResultCode")))
            .Where(r => !string.IsNullOrEmpty(r.Stage) && r.Ms > options.ThresholdMs)
            .ToList();

        return spikeRows
            .GroupBy(r => r.Stage)
            .Where(g => g.Count() >= options.OccurrenceThreshold)
            .ToList();
    }

    private sealed record SpikeRow(string Stage, double Ms, string CorrelationId, string OperationId, string ResultCode);
}
