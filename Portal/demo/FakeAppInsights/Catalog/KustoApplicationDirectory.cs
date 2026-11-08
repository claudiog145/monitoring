using System.Text.Json;
using FakeAppInsights.Kusto;
using Microsoft.Extensions.Options;

namespace FakeAppInsights.Catalog;

/// <summary>Where the current <see cref="ApplicationSnapshot"/> comes from.</summary>
public interface IApplicationDirectory
{
    /// <summary>The cached snapshot, reloaded when older than the cache duration.</summary>
    Task<ApplicationSnapshot> GetAsync(CancellationToken cancellationToken);

    /// <summary>Reloads now unless the last load is younger than the forced-refresh interval.</summary>
    Task<ApplicationSnapshot> RefreshAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Reads the applications from Kustainer: <c>.show databases</c> plus the registry table
/// <c>_demo_registry._app(appId, database)</c> written by DemoGenerator. Cached (30 s by default).
/// </summary>
public sealed class KustoApplicationDirectory(
    IKustoExecutor kusto,
    IOptions<FakeAppInsightsOptions> options,
    TimeProvider time,
    ILogger<KustoApplicationDirectory> logger) : IApplicationDirectory, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ApplicationSnapshot? _snapshot;
    private DateTimeOffset _loadedAt;

    public Task<ApplicationSnapshot> GetAsync(CancellationToken cancellationToken) =>
        LoadAsync(options.Value.CatalogCacheDuration, cancellationToken);

    public Task<ApplicationSnapshot> RefreshAsync(CancellationToken cancellationToken) =>
        LoadAsync(options.Value.CatalogForcedRefreshInterval, cancellationToken);

    public void Dispose() => _gate.Dispose();

    private async Task<ApplicationSnapshot> LoadAsync(TimeSpan maxAge, CancellationToken cancellationToken)
    {
        var current = _snapshot;
        if (current is not null && time.GetUtcNow() - _loadedAt < maxAge) return current;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_snapshot is not null && time.GetUtcNow() - _loadedAt < maxAge) return _snapshot;

            var databases = await ReadDatabasesAsync(cancellationToken);
            var registry = databases.Contains(ApplicationSnapshot.RegistryDatabase, StringComparer.OrdinalIgnoreCase)
                ? await ReadRegistryAsync(cancellationToken)
                : [];

            _snapshot = new ApplicationSnapshot(databases, registry);
            _loadedAt = time.GetUtcNow();
            logger.LogDebug("Application catalog loaded: {Count} applications.", _snapshot.Applications.Count);
            return _snapshot;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<List<string>> ReadDatabasesAsync(CancellationToken cancellationToken)
    {
        var tables = await kusto.ManagementAsync("NetDefaultDB", ".show databases | project DatabaseName", cancellationToken);
        return tables.Count == 0
            ? []
            : tables[0].Rows.Select(r => r.Count > 0 && r[0].ValueKind == JsonValueKind.String ? r[0].GetString() : null)
                .OfType<string>()
                .ToList();
    }

    private async Task<List<DemoApplication>> ReadRegistryAsync(CancellationToken cancellationToken)
    {
        try
        {
            var tables = await kusto.QueryAsync(
                ApplicationSnapshot.RegistryDatabase,
                $"{ApplicationSnapshot.RegistryTable} | project appId = tostring(appId), database = tostring(database)",
                cancellationToken);
            if (tables.Count == 0) return [];
            return tables[0].Rows
                .Where(r => r.Count >= 2 && r[0].ValueKind == JsonValueKind.String && r[1].ValueKind == JsonValueKind.String)
                .Select(r => new DemoApplication(r[0].GetString()!, r[1].GetString()!))
                .ToList();
        }
        catch (KustoQueryException ex)
        {
            logger.LogWarning("The appId registry {Database}.{Table} could not be read: {Message}",
                ApplicationSnapshot.RegistryDatabase, ApplicationSnapshot.RegistryTable, ex.Message);
            return [];
        }
    }
}
