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

    /// <summary>
    /// The newest owner the audit history records for <paramref name="alias"/> (the owner a removed
    /// row had when it was deleted, or the owner a write left), or null when none is recorded. Lets
    /// a deleted alias keep its owner, so recreating it for another profile needs an explicit transfer.
    /// </summary>
    async Task<string?> GetLastKnownOwnerAsync(string alias, CancellationToken ct = default) =>
        TelemetrySourceAuditOwners.LastKnownOwner(await GetAuditAsync(10_000, ct).ConfigureAwait(false), alias);
}

public static class TelemetrySourceAuditOwners
{
    /// <summary>Scans newest first; see <see cref="ITelemetrySourceStore.GetLastKnownOwnerAsync"/>.</summary>
    public static string? LastKnownOwner(IEnumerable<TelemetrySourceAuditEntry> audit, string alias) =>
        audit
            .Where(a => string.Equals(a.Alias?.Trim(), alias?.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(a => a.AuditId)
            .Select(a => a.Removed ? a.PreviousOwnerProfileKey : a.OwnerProfileKey)
            .FirstOrDefault(o => !string.IsNullOrWhiteSpace(o))
            ?.Trim();
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
    string? UpdatedBy = null,
    /// <summary>
    /// The company profile allowed to reference this alias. Null means shared: any profile may use it.
    /// Without an owner, anybody who can save a profile could point a service at another company's
    /// telemetry and read it through Scouts, KPIs and kql: queries.
    /// </summary>
    string? OwnerProfileKey = null);

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
    string ChangedBy,
    /// <summary>The owner after this change (null = shared, or the row was removed).</summary>
    string? OwnerProfileKey = null,
    /// <summary>The owner before this change; differs from <see cref="OwnerProfileKey"/> on an ownership transfer.</summary>
    string? PreviousOwnerProfileKey = null);

public static class TelemetrySourceKind
{
    public const string AppInsights = "app-insights";
}
