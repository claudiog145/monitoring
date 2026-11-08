namespace FakeAppInsights.Rewrite;

/// <summary>
/// Which App Insights "applications" exist in the demo. Each one is a Kustainer database; the
/// generator creates them (contract v5 §4) and records the appId they answer to.
/// </summary>
public interface IApplicationCatalog
{
    /// <summary>True when <paramref name="database"/> is the name of an existing database.</summary>
    bool DatabaseExists(string database);

    /// <summary>The database registered for <paramref name="appId"/>, or null.</summary>
    string? FindDatabaseByAppId(string appId);
}

/// <summary>An in-memory <see cref="IApplicationCatalog"/> built from (appId, database) pairs.</summary>
public sealed class StaticApplicationCatalog : IApplicationCatalog
{
    private readonly Dictionary<string, string> _databaseByAppId = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _databases = new(StringComparer.OrdinalIgnoreCase);

    public StaticApplicationCatalog(IEnumerable<(string AppId, string Database)> apps)
    {
        foreach (var (appId, database) in apps)
        {
            _databases.Add(database);
            if (!string.IsNullOrWhiteSpace(appId)) _databaseByAppId[appId] = database;
        }
    }

    public bool DatabaseExists(string database) => _databases.Contains(database);

    public string? FindDatabaseByAppId(string appId) =>
        _databaseByAppId.TryGetValue(appId, out var database) ? database : null;
}
