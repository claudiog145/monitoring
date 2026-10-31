using System.Text.Json;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Mapping;
using JourneyMonitor.Core.Options;

namespace JourneyMonitor.Core.Monitoring;

/// <summary>
/// Latency Spike Monkey detector variant that reads the durable SQL journey-store snapshot
/// (<see cref="IJourneyStore"/>, populated by the per-vertical journey sync timers) instead of
/// running the per-transaction latency-spikes KQL against Application Insights. See the
/// migration plan (docs/product/latency-spike-doctor-monkey-db-migration-plan-2026-09-21.md) for
/// why this is not a drop-in replacement: the SQL snapshot is per-journey-stage-aggregated
/// (MaxMs/percentiles across the calls one member/session made at that stage), not per-HTTP-call,
/// so "occurrence" here means "distinct journeys showing this stage above threshold in the
/// latest synced snapshot", not "individual slow requests in a rolling window". Enabled per
/// domain via <see cref="MonitoringOptions.SqlDetectorDomains"/>.
/// </summary>
public sealed class SqlLatencySpikeDetectorService(
    IJourneyStore journeyStore,
    ILatencySpikeSettingsResolver settingsResolver,
    IFindingsRepository findingsRepository) : ILatencySpikeDetector
{
    public async Task<IReadOnlyList<Finding>> RunAsync(string domain, string environment, CancellationToken ct = default)
    {
        var options = await settingsResolver.ResolveAsync(domain, ct);
        var groups = await ComputeSpikeGroupsAsync(domain, environment, options, ct);
        if (groups is null) return [];

        var findings = new List<Finding>();
        foreach (var group in groups)
        {
            var items = group.ToList();

            var correlationId = $"{domain}:{group.Key}";

            var healthBreakdown = items
                .GroupBy(i => i.HealthStatus)
                .ToDictionary(g => g.Key, g => g.Count());

            var evidence = JsonSerializer.Serialize(new
            {
                stage = group.Key,
                spikeThresholdMs = options.ThresholdMs,
                occurrenceThreshold = options.OccurrenceThreshold,
                windowMinutes = options.WindowMinutes,
                source = "sql-journey-store",
                note = "observedCount is distinct journeys last synced within windowMinutes whose snapshot shows this stage above threshold - a per-journey rolling window on LastSeen, not per-transaction events like the KQL detector. See docs/product/latency-spike-doctor-monkey-db-migration-plan-2026-09-21.md",
                observedCount = items.Count,
                maxDurationMs = items.Max(i => i.MaxMs),
                correlationIds = items.Select(i => i.CorrelationId).Where(c => !string.IsNullOrEmpty(c)).Distinct().Take(10).ToArray(),
                healthStatusBreakdown = healthBreakdown,
                avgFailureRatePct = Math.Round(items.Average(i => i.FailureRatePct), 2),
            });

            var ratio = (double)items.Count / options.OccurrenceThreshold;
            var severity = ratio >= 3 ? "high" : ratio >= 1.5 ? "medium" : "low";
            // "degraded", not "spikes": observedCount here is distinct journeys stuck above
            // threshold in the latest synced snapshot, not transient per-transaction spikes
            // in a rolling window (see the "note" field above) - calling it a "spike" would
            // be misleading for this detector.
            var title = $"{items.Count} journeys degraded (>{options.ThresholdMs}ms) at stage '{group.Key}' ({domain}, from stored journeys)";

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
                FindingType = FindingType.LatencySpikeCluster,
                Stage = group.Key,
                Title = title,
                Severity = severity,
                EvidenceJson = evidence,
                OriginMonkeyKey = MonkeyKey.LatencySpikeMonkey,
                CorrelationId = correlationId,
            };

            findings.Add(await findingsRepository.AddAsync(finding, ct));
        }

        // Same reconciliation as LatencySpikeDetectorService - only runs when a real snapshot was
        // read (groups is non-null here), never when the snapshot itself was simply unavailable
        // this cycle (see the null-return guard above) - absence of data must never be treated as
        // "nothing qualifies anymore".
        var stillQualifying = groups.Select(g => $"{domain}:{g.Key}").ToHashSet();
        await StaleFindingReconciler.ResolveStaleFindingsAsync(findingsRepository, domain, stillQualifying, ct);

        return findings;
    }

    /// <summary>
    /// Dry run for the Fase 3 shadow-mode comparison (see the KQL-to-DB migration plan): computes
    /// the same stage groups <see cref="RunAsync"/> would, but never touches
    /// <see cref="IFindingsRepository"/> - no dedup lookup, no Finding created.
    /// </summary>
    public async Task<IReadOnlyList<SpikeGroupPreview>> PreviewAsync(string domain, string environment, CancellationToken ct = default)
    {
        var options = await settingsResolver.ResolveAsync(domain, ct);
        var groups = await ComputeSpikeGroupsAsync(domain, environment, options, ct);
        if (groups is null) return [];

        return groups
            .Select(g => new SpikeGroupPreview(g.Key, g.Count(), g.Max(i => i.MaxMs)))
            .ToList();
    }

    private async Task<List<IGrouping<string, SpikeRow>>?> ComputeSpikeGroupsAsync(string domain, string environment, ResolvedLatencySettings options, CancellationToken ct)
    {
        _ = environment; // The journey snapshot is not environment-scoped today; JourneySyncOptions.Environment picks one at sync time.

        var snapshot = await journeyStore.GetLatestAsync(domain, ct);
        if (snapshot is null) return null;

        // Gives WindowMinutes real effect for this detector: only journeys the sync timer has
        // touched within the window count, same "rolling window" intent as the KQL detector's own
        // WINDOW_MINUTES substitution - a journey the last sync run didn't re-see is stale data,
        // not a currently-observed spike, regardless of how slow it was when last synced.
        var windowStart = DateTimeOffset.UtcNow.AddMinutes(-options.WindowMinutes);

        var spikeRows = new List<SpikeRow>();
        foreach (var row in snapshot.Rows)
        {
            var lastSeen = row.GetDateTimeOffset("LastSeen");
            if (lastSeen is not null && lastSeen < windowStart) continue;

            var correlationId = row.GetString("CorrelationId");
            if (string.IsNullOrEmpty(correlationId)) correlationId = row.GetString("UserId");

            foreach (var stage in row.GetJourneyStages())
            {
                var stageName = stage.Str("stage");
                if (string.IsNullOrEmpty(stageName)) continue;

                // MaxMs is the slowest call this journey made at this stage; fall back to the
                // stage total when a row predates MaxMs being captured.
                var maxMs = stage.Dbl("maxMs") ?? stage.Dbl("stageTotalMs") ?? 0;
                if (maxMs <= options.ThresholdMs) continue;

                spikeRows.Add(new SpikeRow(
                    Stage: stageName,
                    MaxMs: maxMs,
                    CorrelationId: correlationId,
                    HealthStatus: string.IsNullOrEmpty(stage.Str("status")) ? "N/A" : stage.Str("status"),
                    FailureRatePct: stage.Dbl("failureRatePct") ?? 0));
            }
        }

        return spikeRows
            .GroupBy(r => r.Stage)
            .Where(g => g.Count() >= options.OccurrenceThreshold)
            .ToList();
    }

    private sealed record SpikeRow(string Stage, double MaxMs, string CorrelationId, string HealthStatus, double FailureRatePct);
}
