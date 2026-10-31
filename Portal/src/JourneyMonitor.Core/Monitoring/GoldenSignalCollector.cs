using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Mapping;
using Microsoft.Extensions.Logging;

namespace JourneyMonitor.Core.Monitoring;

/// <summary>
/// Ingests one 5-minute bucket of <see cref="GoldenSignalSample"/> per enabled
/// <see cref="SloDefinition"/> for a domain - the Latency signal reuses the exact journey
/// snapshot/stage-bag data <see cref="SqlLatencySpikeDetectorService"/> already reads (no new
/// query against Application Insights/SQL), the ExceptionRate signal reads
/// <see cref="IExceptionOccurrenceCounter"/>. Never throws - a missing snapshot, an unconfigured
/// store, or a read failure simply means fewer/no samples are written this cycle, same
/// anti-hallucination "degrade, don't break the monkey cycle" pattern as every other Monitoring
/// component here.
/// </summary>
public sealed class GoldenSignalCollector(
    IJourneyStore journeyStore,
    ISloDefinitionStore sloDefinitionStore,
    IExceptionOccurrenceCounter exceptionCounter,
    IGoldenSignalStore goldenSignalStore,
    ILogger<GoldenSignalCollector> logger)
{
    private const int BucketMinutes = 5;

    public async Task CollectAsync(string domain, CancellationToken ct = default)
    {
        IReadOnlyList<SloDefinition> allDefinitions;
        try
        {
            allDefinitions = await sloDefinitionStore.GetAllAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[GoldenSignalCollector] could not load SLO definitions - skipping domain {Domain} this cycle.", domain);
            return;
        }

        var latencyDefinitions = allDefinitions
            .Where(d => d.Enabled && d.SignalType == Domain.SignalType.Latency
                        && string.Equals(d.Domain, domain, StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrEmpty(d.Stage) && d.TargetThresholdMs is > 0)
            .ToList();

        if (latencyDefinitions.Count > 0)
        {
            await CollectLatencyAsync(domain, latencyDefinitions, ct);
        }

        var exceptionDefinition = allDefinitions.FirstOrDefault(d =>
            d.Enabled && d.SignalType == Domain.SignalType.ExceptionRate
            && string.Equals(d.Domain, domain, StringComparison.OrdinalIgnoreCase));

        if (exceptionDefinition is not null)
        {
            await CollectExceptionRateAsync(domain, ct);
        }
    }

    private async Task CollectLatencyAsync(string domain, IReadOnlyList<SloDefinition> definitions, CancellationToken ct)
    {
        Abstractions.StoredJourneys? snapshot;
        try
        {
            snapshot = await journeyStore.GetLatestAsync(domain, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[GoldenSignalCollector] journey snapshot read failed for {Domain} - skipping latency signal this cycle.", domain);
            return;
        }

        if (snapshot is null) return;

        // Anchor the bucket to the snapshot's own fetch time, not "now": a sync that hasn't
        // advanced yet (delayed cron, or a genuinely slow period) returns the SAME snapshot across
        // multiple collector cycles, and AddOrUpdateAsync is idempotent by (domain, stage,
        // signalType, bucketStart) - re-processing the same snapshot must re-upsert the SAME
        // bucket, not manufacture a new one every 5 minutes with duplicated observations.
        var bucketStart = FloorToBucket(snapshot.FetchedAt ?? DateTimeOffset.UtcNow);

        foreach (var def in definitions)
        {
            var good = 0;
            var bad = 0;
            foreach (var row in snapshot.Rows)
            {
                foreach (var stage in row.GetJourneyStages())
                {
                    if (!string.Equals(stage.Str("stage"), def.Stage, StringComparison.OrdinalIgnoreCase)) continue;

                    var measuredMs = stage.Dbl("maxMs") ?? stage.Dbl("stageTotalMs");
                    if (measuredMs is null) continue;

                    if (measuredMs > def.TargetThresholdMs) bad++;
                    else good++;
                }
            }

            var total = good + bad;
            if (total == 0) continue; // Nothing observed for this stage this cycle - write nothing rather than a misleading all-zero bucket.

            await SaveAsync(domain, def.Stage, Domain.SignalType.Latency, bucketStart, good, bad, total, ct);
        }
    }

    private async Task CollectExceptionRateAsync(string domain, CancellationToken ct)
    {
        // Count the most recently COMPLETED 5-minute interval, not the one that just started: at
        // the instant this timer fires, `bucketEnd` is a brand-new boundary, so counting from it
        // onward would almost always see zero occurrences, while the real activity from the prior
        // 5 minutes is never attributed to any bucket. The explicit upper bound also keeps a
        // delayed run from leaking occurrences that belong to the NEXT bucket into this one.
        var bucketEnd = FloorToBucket(DateTimeOffset.UtcNow);
        var bucketStart = bucketEnd - TimeSpan.FromMinutes(BucketMinutes);

        int badCount;
        try
        {
            badCount = await exceptionCounter.CountSinceAsync(domain, bucketStart, bucketEnd, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[GoldenSignalCollector] exception occurrence count failed for {Domain} - skipping exception_rate signal this cycle.", domain);
            return;
        }

        // Known limitation, documented rather than hidden: there is no precise per-bucket
        // "total transactions" figure tracked yet, so the domain's most recently synced journey
        // count is used as a rough request-volume proxy for the SLI denominator. See the
        // research doc (wi-238036-...) for the full reasoning.
        int totalProxy;
        try
        {
            var snapshot = await journeyStore.GetLatestAsync(domain, ct);
            totalProxy = snapshot?.RecordCount ?? 0;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            totalProxy = 0;
        }

        if (totalProxy <= 0 && badCount <= 0) return; // Nothing to report - avoid a division-by-zero-flavored all-zero bucket.

        var total = Math.Max(totalProxy, badCount);
        var good = Math.Max(total - badCount, 0);
        await SaveAsync(domain, stage: null, Domain.SignalType.ExceptionRate, bucketStart, good, badCount, total, ct);
    }

    private async Task SaveAsync(string domain, string? stage, string signalType, DateTimeOffset bucketStart, int good, int bad, int total, CancellationToken ct)
    {
        try
        {
            await goldenSignalStore.AddOrUpdateAsync(
                new GoldenSignalSample(domain, stage, signalType, bucketStart, BucketMinutes, good, bad, total), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[GoldenSignalCollector] failed to persist a {SignalType} sample for {Domain}/{Stage}.", signalType, domain, stage ?? "(domain-level)");
        }
    }

    private static DateTimeOffset FloorToBucket(DateTimeOffset now)
    {
        var minutesSinceEpoch = (long)(now - DateTimeOffset.UnixEpoch).TotalMinutes;
        var flooredMinutes = minutesSinceEpoch - (minutesSinceEpoch % BucketMinutes);
        return DateTimeOffset.UnixEpoch.AddMinutes(flooredMinutes);
    }
}
