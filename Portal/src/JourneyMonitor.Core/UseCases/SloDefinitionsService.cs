using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Options;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Core.UseCases;

/// <summary>
/// Backs the Settings tab's "SLO Definitions" section - read/write the per-(domain, stage,
/// signal type) SLO targets that replaced the old flat per-domain latency threshold (see
/// docs/product/wi-238036-threshold-research-and-wi-package-2026-09-29.md). Any portal user may
/// change it (no authorization check, same product decision already made for the Latency Spike
/// Monkey thresholds) - who/when is recorded via <see cref="ISloDefinitionStore.GetAuditAsync"/>.
/// </summary>
public sealed class SloDefinitionsService(ISloDefinitionStore store, IOptions<JourneyStoreOptions> storeOptions)
{
    private static readonly HashSet<string> KnownDomains = new(StringComparer.OrdinalIgnoreCase) { "hotels", "cars", "cruises", "air" };
    private static readonly HashSet<string> KnownSignalTypes = new(StringComparer.Ordinal) { Domain.SignalType.Latency, Domain.SignalType.ExceptionRate };

    public async Task<IReadOnlyList<SloDefinition>> GetAllAsync(CancellationToken ct)
    {
        if (!storeOptions.Value.IsConfigured) return [];
        try
        {
            return await store.GetAllAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Same resilience pattern as LatencySpikeSettingsService: a connection string being
            // set is not proof the monitoring.SloDefinition table/procs are provisioned yet on
            // that database (e.g. a fresh QA environment) - degrade to "no overrides" instead of
            // a 500 for a plain read.
            return [];
        }
    }

    public async Task<(int StatusCode, object Result)> UpsertAsync(
        string domain, string? stage, string signalType, int? targetThresholdMs, decimal targetPercentage, int windowDays, bool enabled, string? changedBy, CancellationToken ct)
    {
        if (!KnownDomains.Contains(domain))
        {
            return (400, new { error = $"Unknown domain '{domain}'. Must be one of: {string.Join(", ", KnownDomains)}." });
        }
        if (!KnownSignalTypes.Contains(signalType))
        {
            return (400, new { error = $"Unknown signalType '{signalType}'. Must be one of: {string.Join(", ", KnownSignalTypes)}." });
        }
        if (signalType == Domain.SignalType.Latency && string.IsNullOrWhiteSpace(stage))
        {
            return (400, new { error = "stage is required when signalType is 'latency'." });
        }
        if (signalType == Domain.SignalType.ExceptionRate && !string.IsNullOrWhiteSpace(stage))
        {
            return (400, new { error = "stage must be blank when signalType is 'exception_rate' (the signal is domain-level)." });
        }
        if (signalType == Domain.SignalType.Latency && targetThresholdMs is not > 0)
        {
            return (400, new { error = "targetThresholdMs must be a positive number when signalType is 'latency'." });
        }
        if (targetPercentage <= 0 || targetPercentage >= 100)
        {
            // A 100% target has a zero error budget, which BurnRateDetector cannot evaluate (nothing
            // to burn) - rejecting it here means a saved, enabled SLO can never silently do nothing.
            return (400, new { error = "targetPercentage must be between 0 and 100, exclusive on both ends." });
        }
        if (windowDays < 28 || windowDays > 31)
        {
            // The fixed 14.4x/6x/1x burn-rate multipliers (BurnRateWindows.All) are Google SRE's
            // table for a ~30-day error-budget period - they don't scale with WindowDays, so a
            // 1-day or 90-day definition would alert identically to a 30-day one. Constrain to a
            // calendar month until/unless the thresholds themselves are made period-aware.
            return (400, new { error = "windowDays must be between 28 and 31 (the fixed alert thresholds are calibrated for a ~30-day error-budget period)." });
        }

        var who = string.IsNullOrWhiteSpace(changedBy) ? "anonymous" : changedBy.Trim();
        var normalizedStage = string.IsNullOrWhiteSpace(stage) ? null : stage.Trim();

        await store.UpsertAsync(new SloDefinition(domain, normalizedStage, signalType, targetThresholdMs, targetPercentage, windowDays, enabled, null, who), who, ct);

        return (200, await GetAllAsync(ct));
    }

    public async Task<IReadOnlyList<SloDefinitionAuditEntry>> GetAuditAsync(CancellationToken ct)
    {
        if (!storeOptions.Value.IsConfigured) return [];
        try
        {
            return await store.GetAuditAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return [];
        }
    }
}
