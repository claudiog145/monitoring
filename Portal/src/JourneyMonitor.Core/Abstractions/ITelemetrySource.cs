using JourneyMonitor.Core.Domain;

namespace JourneyMonitor.Core.Abstractions;

/// <summary>
/// Represents "where telemetry comes from". The only implementation today is
/// AppInsightsTelemetrySource (Infrastructure), executing the same KQL text the original
/// JavaScript app sent to Application Insights. A future FabricTelemetrySource can replace
/// it without any change to Core use cases, mappers or calculators.
/// </summary>
public interface ITelemetrySource
{
    /// <summary>Executes a fully-injected KQL query and returns the flattened result rows.</summary>
    Task<IReadOnlyList<TelemetryJourneyRow>> QueryAsync(TelemetryQueryRequest request, CancellationToken cancellationToken);
}

public sealed record TelemetryQueryRequest(string Kql, string AppInsightsAppId, string? TenantId);

/// <summary>
/// Represents "where we cache the operational copy of a query result" so repeated UI
/// refreshes do not re-run the same expensive Application Insights query. This is a
/// short-lived, process-local cache (see ARCHITECTURE.md) - not a system of record.
/// Durable snapshots belong on <see cref="IJourneyStore"/>.
/// </summary>
public interface ITelemetryQueryCache
{
    Task<CachedQueryPayload?> GetAsync(string key, CancellationToken cancellationToken);

    Task SetAsync(string key, string payloadJson, TimeSpan timeToLive, CancellationToken cancellationToken);
}

public sealed record CachedQueryPayload(string PayloadJson, DateTimeOffset CachedAt);

/// <summary>
/// Provides the offline fallback data (last exported PROD snapshot, then anonymized demo
/// fixture) used when a live Application Insights query fails. Hotels/club 15639 only,
/// matching the original loadSnapshot/loadDemo behavior in server.js.
/// </summary>
public interface IDemoJourneyFixtureProvider
{
    bool HasSnapshot { get; }

    Task<JourneysResult> LoadSnapshotAsync(string win, CancellationToken cancellationToken);

    Task<JourneysResult> LoadDemoAsync(string win, CancellationToken cancellationToken);
}
