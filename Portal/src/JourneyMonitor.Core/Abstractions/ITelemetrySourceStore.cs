namespace JourneyMonitor.Core.Abstractions;

/// <summary>
/// Where telemetry is read from, by alias. KQL text refers to a source as
/// <c>app('{{SOURCE:cruise-core}}')</c> and never carries a subscription id, so pointing the
/// platform at a different subscription is a row change rather than an edit-and-redeploy of
/// eight query files.
/// </summary>
public interface ITelemetrySourceStore
{
    Task<IReadOnlyList<TelemetrySourceEntry>> GetAllAsync(CancellationToken ct = default);

    Task<TelemetrySourceEntry> UpsertAsync(TelemetrySourceEntry source, string changedBy, CancellationToken ct = default);

    Task DeleteAsync(string alias, string environment, string changedBy, CancellationToken ct = default);

    Task<IReadOnlyList<TelemetrySourceAuditEntry>> GetAuditAsync(int top, CancellationToken ct = default);
}

/// <summary>
/// Substitutes <c>{{SOURCE:alias}}</c> placeholders in KQL text with the ARM resource id
/// registered for that alias in the requested environment.
/// </summary>
public interface IKqlSourceResolver
{
    /// <summary>
    /// Replaces every <c>{{SOURCE:alias}}</c> with its resource id. Throws when an alias is
    /// unknown, disabled or holds something that is not an ARM resource id: a query silently
    /// left pointing nowhere returns zero rows, which is indistinguishable from "no problems".
    /// </summary>
    Task<string> ResolveAsync(string kql, string environment, CancellationToken ct = default);

    /// <summary>Drops the cached snapshot so a source edited in the portal takes effect without waiting it out.</summary>
    void Invalidate();
}

/// <summary>One registered telemetry backend for one environment.</summary>
public sealed record TelemetrySourceEntry(
    string Alias,
    string Environment,
    /// <summary><see cref="TelemetrySourceKind"/>. Not an enum: a new backend must be a row, not a deployment.</summary>
    string Kind,
    string ResourceId,
    string? AppId = null,
    string? TenantId = null,
    bool Enabled = true,
    string? Notes = null,
    DateTimeOffset? UpdatedAt = null,
    string? UpdatedBy = null);

public sealed record TelemetrySourceAuditEntry(
    long AuditId,
    string Alias,
    string Environment,
    string? Kind,
    string? ResourceId,
    string? AppId,
    string? TenantId,
    bool? Enabled,
    string? Notes,
    bool Removed,
    DateTimeOffset ChangedAt,
    string ChangedBy);

public static class TelemetrySourceKind
{
    public const string AppInsights = "app-insights";
}
