using System.Text.RegularExpressions;
using FakeAppInsights.Rewrite;

namespace FakeAppInsights.Catalog;

/// <summary>An application the server answers to: its appId and the Kustainer database holding its tables.</summary>
public sealed record DemoApplication(string AppId, string Database);

/// <summary>
/// An immutable view of the demo applications at one point in time. Implements the appId → database
/// rule documented in <c>Portal/demo/FakeAppInsights/README.md</c> (DemoGenerator follows it):
/// registry row in <c>_demo_registry._app</c>, then a database with the same name, then the name
/// without the <c>demo-</c> prefix.
/// </summary>
public sealed partial class ApplicationSnapshot : IApplicationCatalog
{
    public const string RegistryDatabase = "_demo_registry";
    public const string RegistryTable = "_app";
    public const string AppIdPrefix = "demo-";
    private const string EngineDefaultDatabase = "NetDefaultDB";

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,260}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeDatabaseName();

    private readonly HashSet<string> _databases = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _databaseByAppId = new(StringComparer.OrdinalIgnoreCase);

    public ApplicationSnapshot(IEnumerable<string> databases, IEnumerable<DemoApplication> registry)
    {
        foreach (var database in databases)
        {
            if (IsApplicationDatabase(database)) _databases.Add(database);
        }

        foreach (var entry in registry
                     .Where(e => !string.IsNullOrWhiteSpace(e.AppId) && _databases.Contains(e.Database))
                     .OrderBy(e => e.Database, StringComparer.Ordinal))
        {
            _databaseByAppId.TryAdd(entry.AppId.Trim(), _databases.First(d => d.Equals(entry.Database, StringComparison.OrdinalIgnoreCase)));
        }
    }

    public static ApplicationSnapshot Empty { get; } = new([], []);

    /// <summary>True for a database that can hold an application: not the registry, not the engine's own.</summary>
    public static bool IsApplicationDatabase(string database) =>
        !string.IsNullOrWhiteSpace(database) &&
        !database.StartsWith('_') &&
        !database.Equals(EngineDefaultDatabase, StringComparison.OrdinalIgnoreCase) &&
        SafeDatabaseName().IsMatch(database);

    public bool DatabaseExists(string database) => _databases.Contains(database);

    public string? FindDatabaseByAppId(string appId)
    {
        if (_databaseByAppId.TryGetValue(appId, out var registered)) return registered;
        return StripPrefix(appId);
    }

    /// <summary>The database for the <c>{appId}</c> of a request URL, or null (→ 404).</summary>
    public string? ResolveApp(string appId)
    {
        if (string.IsNullOrWhiteSpace(appId)) return null;
        if (_databaseByAppId.TryGetValue(appId, out var registered)) return registered;
        if (_databases.TryGetValue(appId, out var sameName)) return sameName;
        return StripPrefix(appId);
    }

    /// <summary>Registered appIds, then every database no appId points to (answering to its own name).</summary>
    public IReadOnlyList<DemoApplication> Applications
    {
        get
        {
            var registered = _databaseByAppId.Select(p => new DemoApplication(p.Key, p.Value)).ToList();
            var covered = new HashSet<string>(registered.Select(a => a.Database), StringComparer.OrdinalIgnoreCase);
            return registered
                .Concat(_databases.Where(d => !covered.Contains(d)).Select(d => new DemoApplication(d, d)))
                .OrderBy(a => a.Database, StringComparer.OrdinalIgnoreCase)
                .ThenBy(a => a.AppId, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    private string? StripPrefix(string appId) =>
        appId.Length > AppIdPrefix.Length &&
        appId.StartsWith(AppIdPrefix, StringComparison.OrdinalIgnoreCase) &&
        _databases.TryGetValue(appId[AppIdPrefix.Length..], out var database)
            ? database
            : null;
}
