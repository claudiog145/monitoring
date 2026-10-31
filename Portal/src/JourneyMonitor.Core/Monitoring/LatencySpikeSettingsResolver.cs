using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Core.Monitoring;

/// <summary>Effective thresholds for one domain after applying the per-domain override, if any.</summary>
public sealed record ResolvedLatencySettings(int ThresholdMs, int OccurrenceThreshold, int WindowMinutes);

/// <summary>
/// Resolves the effective Latency Spike Monkey thresholds for a domain: a per-domain override in
/// <see cref="ILatencySpikeSettingsStore"/> wins when set (and positive), otherwise falls back to
/// the global <see cref="MonitoringOptions"/> value (which itself falls back to its hardcoded
/// default). Never throws - a store lookup failure (e.g. SQL store not configured) just falls
/// back to the global option, same anti-hallucination "never break the detector" pattern used
/// elsewhere in Monitoring.
/// </summary>
public interface ILatencySpikeSettingsResolver
{
    Task<ResolvedLatencySettings> ResolveAsync(string domain, CancellationToken cancellationToken);
}

public sealed class LatencySpikeSettingsResolver(
    ILatencySpikeSettingsStore settingsStore,
    IOptions<JourneyStoreOptions> storeOptions,
    IOptions<MonitoringOptions> monitoringOptions,
    ILogger<LatencySpikeSettingsResolver> logger) : ILatencySpikeSettingsResolver
{
    public async Task<ResolvedLatencySettings> ResolveAsync(string domain, CancellationToken cancellationToken)
    {
        var options = monitoringOptions.Value;
        var fallback = new ResolvedLatencySettings(
            options.ResolvedSpikeThresholdMs,
            options.ResolvedSpikeOccurrenceThreshold,
            options.ResolvedSpikeWindowMinutes);

        if (!storeOptions.Value.IsConfigured) return fallback;

        try
        {
            var all = await settingsStore.GetAllAsync(cancellationToken);
            var match = all.FirstOrDefault(s => string.Equals(s.Domain, domain, StringComparison.OrdinalIgnoreCase));
            if (match is null) return fallback;

            return new ResolvedLatencySettings(
                match.SpikeThresholdMs is > 0 ? match.SpikeThresholdMs.Value : fallback.ThresholdMs,
                match.SpikeOccurrenceThreshold is > 0 ? match.SpikeOccurrenceThreshold.Value : fallback.OccurrenceThreshold,
                match.SpikeWindowMinutes is > 0 ? match.SpikeWindowMinutes.Value : fallback.WindowMinutes);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A genuine cancellation must propagate, not be swallowed into a "successful" fallback.
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Per-domain latency setting lookup failed for {Domain}; falling back to global Monitoring options.", domain);
            return fallback;
        }
    }
}
