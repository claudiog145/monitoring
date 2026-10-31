using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Monitoring;
using JourneyMonitor.Core.Options;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Core.UseCases;

/// <summary>Effective + overridden Latency Spike Monkey settings for one domain, ready for the Settings UI.</summary>
public sealed record LatencySpikeSettingView(
    string Domain,
    int? OverrideSpikeThresholdMs,
    int? OverrideSpikeOccurrenceThreshold,
    int? OverrideSpikeWindowMinutes,
    int EffectiveSpikeThresholdMs,
    int EffectiveSpikeOccurrenceThreshold,
    int EffectiveSpikeWindowMinutes,
    DateTimeOffset? UpdatedAt,
    string? UpdatedBy);

/// <summary>
/// Backs the Settings tab: read the per-domain Latency Spike Monkey thresholds (override +
/// effective value) and let any portal user change them, recording who/when in
/// <see cref="ILatencySpikeSettingsStore.GetAuditAsync"/>. All 4 known domains are always
/// returned even when a domain has no stored override yet (effective values then fall back to
/// the global <c>Monitoring</c> config).
/// </summary>
public sealed class LatencySpikeSettingsService(
    ILatencySpikeSettingsStore settingsStore,
    ILatencySpikeSettingsResolver settingsResolver,
    IOptions<JourneyStoreOptions> storeOptions)
{
    public static readonly IReadOnlyList<string> KnownDomains = ["cars", "hotels", "cruises", "air"];

    // Upper bounds, not just "positive": SqlLatencySpikeDetectorService evaluates
    // DateTimeOffset.UtcNow.AddMinutes(-options.WindowMinutes) every run - an unbounded value
    // (e.g. int.MaxValue) throws there (outside DateTimeOffset's supported range), which would
    // otherwise only surface as a broken timer run long after this endpoint accepted the value.
    private const int MaxSpikeThresholdMs = 24 * 60 * 60 * 1000; // 24h - a "spike" beyond that isn't a spike, it's broken.
    private const int MaxSpikeOccurrenceThreshold = 100_000;
    private const int MaxSpikeWindowMinutes = 7 * 24 * 60; // 7 days.

    public async Task<IReadOnlyList<LatencySpikeSettingView>> GetAllAsync(CancellationToken ct)
    {
        // SqlLatencySpikeSettingsStore throws when JourneyStore:ConnectionString isn't set (see
        // its OpenConnection), or when it IS set but the table/procs haven't been provisioned yet
        // on that database (e.g. a QA environment before the migration scripts have run) - either
        // way, degrade to the 4 known domains with their effective (global/default) values instead
        // of a 500, same fallback ILatencySpikeSettingsResolver already applies per domain.
        var stored = storeOptions.Value.IsConfigured
            ? await TryGetStoredAsync(ct)
            : [];
        var views = new List<LatencySpikeSettingView>(KnownDomains.Count);
        foreach (var domain in KnownDomains)
        {
            var match = stored.FirstOrDefault(s => string.Equals(s.Domain, domain, StringComparison.OrdinalIgnoreCase));
            var effective = await settingsResolver.ResolveAsync(domain, ct);
            views.Add(new LatencySpikeSettingView(
                domain,
                match?.SpikeThresholdMs,
                match?.SpikeOccurrenceThreshold,
                match?.SpikeWindowMinutes,
                effective.ThresholdMs,
                effective.OccurrenceThreshold,
                effective.WindowMinutes,
                match?.UpdatedAt,
                match?.UpdatedBy));
        }
        return views;
    }

    private async Task<IReadOnlyList<LatencySpikeSetting>> TryGetStoredAsync(CancellationToken ct)
    {
        try
        {
            return await settingsStore.GetAllAsync(ct);
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

    public async Task<(int StatusCode, object Result)> UpsertAsync(
        string? domain,
        int? spikeThresholdMs,
        int? spikeOccurrenceThreshold,
        int? spikeWindowMinutes,
        string? changedBy,
        CancellationToken ct)
    {
        var normalizedDomain = domain?.Trim().ToLowerInvariant() ?? "";
        if (!KnownDomains.Contains(normalizedDomain))
        {
            return (400, new { error = $"Unknown domain '{domain}'. Expected one of: {string.Join(", ", KnownDomains)}." });
        }

        if (spikeThresholdMs is <= 0 or > MaxSpikeThresholdMs
            || spikeOccurrenceThreshold is <= 0 or > MaxSpikeOccurrenceThreshold
            || spikeWindowMinutes is <= 0 or > MaxSpikeWindowMinutes)
        {
            return (400, new
            {
                error = $"spikeThresholdMs must be between 1 and {MaxSpikeThresholdMs}, spikeOccurrenceThreshold between 1 and {MaxSpikeOccurrenceThreshold}, and spikeWindowMinutes between 1 and {MaxSpikeWindowMinutes}, when provided.",
            });
        }

        var who = string.IsNullOrWhiteSpace(changedBy) ? "anonymous" : changedBy.Trim();
        await settingsStore.UpsertAsync(normalizedDomain, spikeThresholdMs, spikeOccurrenceThreshold, spikeWindowMinutes, who, ct);

        var all = await GetAllAsync(ct);
        return (200, all.First(v => v.Domain == normalizedDomain));
    }

    public async Task<IReadOnlyList<LatencySpikeSettingAuditEntry>> GetAuditAsync(string? domain, CancellationToken ct)
    {
        if (!storeOptions.Value.IsConfigured) return [];
        try
        {
            return await settingsStore.GetAuditAsync(string.IsNullOrWhiteSpace(domain) ? null : domain.Trim().ToLowerInvariant(), ct);
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
