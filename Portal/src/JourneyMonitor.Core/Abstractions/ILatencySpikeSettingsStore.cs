namespace JourneyMonitor.Core.Abstractions;

/// <summary>
/// One domain's per-domain override of the Latency Spike Monkey's thresholds. Any field left
/// null (or non-positive) means "no override for this domain" - callers fall back to
/// <see cref="Options.MonitoringOptions"/>'s global/default values, same nullable-fallback
/// pattern used there.
/// </summary>
public sealed record LatencySpikeSetting(
    string Domain,
    int? SpikeThresholdMs,
    int? SpikeOccurrenceThreshold,
    int? SpikeWindowMinutes,
    DateTimeOffset? UpdatedAt,
    string? UpdatedBy);

/// <summary>One historical change row, kept forever for audit purposes (never updated/deleted).</summary>
public sealed record LatencySpikeSettingAuditEntry(
    string Domain,
    int? SpikeThresholdMs,
    int? SpikeOccurrenceThreshold,
    int? SpikeWindowMinutes,
    DateTimeOffset ChangedAt,
    string ChangedBy);

/// <summary>
/// Durable, per-domain override store for the Latency Spike Monkey's thresholds (see
/// <c>monitoring.LatencySpikeSetting</c> / <c>monitoring.LatencySpikeSettingAudit</c>). Backed by
/// the same SQL database as <see cref="IJourneyStore"/> - requires <c>JourneyStore:ConnectionString</c>
/// to be configured, otherwise callers must fall back to the global <c>Monitoring</c> config
/// section instead of throwing.
/// </summary>
public interface ILatencySpikeSettingsStore
{
    /// <summary>Every domain with a stored override (domains never overridden are absent, not zeroed-out rows).</summary>
    Task<IReadOnlyList<LatencySpikeSetting>> GetAllAsync(CancellationToken cancellationToken);

    /// <summary>Upserts one domain's override and appends an audit row in the same transaction.</summary>
    Task UpsertAsync(
        string domain,
        int? spikeThresholdMs,
        int? spikeOccurrenceThreshold,
        int? spikeWindowMinutes,
        string changedBy,
        CancellationToken cancellationToken);

    /// <summary>Change history, newest first. <paramref name="domain"/> null returns every domain's history.</summary>
    Task<IReadOnlyList<LatencySpikeSettingAuditEntry>> GetAuditAsync(string? domain, CancellationToken cancellationToken);
}
