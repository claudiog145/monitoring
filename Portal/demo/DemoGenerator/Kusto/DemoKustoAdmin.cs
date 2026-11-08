using System.Reflection;
using DemoGenerator.Synthesis;

namespace DemoGenerator.Kusto;

public sealed record AppRegistration(string AppId, string Database);

public sealed record CompanyRegistration(string CompanyKey, DateTimeOffset ResetAt, string Scale, int Days);

/// <summary>
/// Databases, schema and the appId registry. The registry follows the rule FakeAppInsights documents in
/// <c>Portal/demo/FakeAppInsights/README.md</c>: database <c>_demo_registry</c>, table
/// <c>_app(appId:string, database:string)</c>, one row per archetype app, rewritten with
/// <c>.set-or-replace</c> so a repeated reset never duplicates an appId. The same database also keeps
/// <c>_company</c> (reset time and scale per company) so a restarted generator can resume feeding.
/// </summary>
public sealed class DemoKustoAdmin(IKustoClient kusto)
{
    public const string RegistryDatabase = "_demo_registry";
    public const string AppTable = "_app";
    public const string CompanyTable = "_company";
    public const string DefaultDatabase = "NetDefaultDB";

    private static readonly Lazy<IReadOnlyList<string>> SchemaCommands = new(LoadSchema);

    /// <summary>The management commands of <c>Portal/demo/kusto/schema.kql</c> (embedded), one per non-comment line.</summary>
    public static IReadOnlyList<string> Schema => SchemaCommands.Value;

    public static string CreateDatabaseCommand(string database, string persistence, string dataRoot) =>
        string.Equals(persistence, "volatile", StringComparison.OrdinalIgnoreCase)
            ? $".create database {Name(database)} volatile ifnotexists"
            : $".create database {Name(database)} persist (@'{dataRoot.TrimEnd('/')}/{database}/md', @'{dataRoot.TrimEnd('/')}/{database}/data') ifnotexists";

    /// <summary>Creates the database if needed, drops the six tables and recreates them empty.</summary>
    public async Task RecreateAppDatabaseAsync(string database, string persistence, string dataRoot, CancellationToken ct)
    {
        await EnsureDatabaseAsync(database, persistence, dataRoot, ct);
        await kusto.ExecuteCommandAsync(database, $".drop tables ({string.Join(", ", KqlSynthesisBuilder.Tables)}) ifexists", ct);
        foreach (var command in Schema) await kusto.ExecuteCommandAsync(database, command, ct);
    }

    /// <summary>
    /// A persistent database dropped by hand leaves its folder behind, and Kustainer refuses to create a database
    /// over it; in that case the database is created in a new folder next to the old one.
    /// </summary>
    private async Task EnsureDatabaseAsync(string database, string persistence, string dataRoot, CancellationToken ct)
    {
        try
        {
            await kusto.ExecuteCommandAsync(DefaultDatabase, CreateDatabaseCommand(database, persistence, dataRoot), ct);
        }
        catch (KustoCommandException) when (!string.Equals(persistence, "volatile", StringComparison.OrdinalIgnoreCase))
        {
            if ((await ListDatabasesAsync(ct)).Contains(database)) throw;
            var folder = $"{database}-{DateTime.UtcNow:yyyyMMddHHmmss}";
            await kusto.ExecuteCommandAsync(DefaultDatabase,
                $".create database {Name(database)} persist (@'{dataRoot.TrimEnd('/')}/{folder}/md', @'{dataRoot.TrimEnd('/')}/{folder}/data') ifnotexists", ct);
        }
    }

    public async Task EnsureRegistryAsync(string persistence, string dataRoot, CancellationToken ct)
    {
        await EnsureDatabaseAsync(RegistryDatabase, persistence, dataRoot, ct);
        await kusto.ExecuteCommandAsync(RegistryDatabase, $".create-merge table {AppTable} (appId:string, database:string)", ct);
        await kusto.ExecuteCommandAsync(RegistryDatabase, $".create-merge table {CompanyTable} (companyKey:string, resetAt:datetime, scale:string, backfillDays:int)", ct);
    }

    public static string RegisterAppsCommand(IReadOnlyCollection<AppRegistration> apps)
    {
        var ids = string.Join(", ", apps.Select(a => KqlSynthesisBuilder.Str(a.AppId)));
        var rows = string.Join(", ", apps.Select(a => $"{KqlSynthesisBuilder.Str(a.AppId)}, {KqlSynthesisBuilder.Str(a.Database)}"));
        return $".set-or-replace {AppTable} <| {AppTable} | where appId !in ({ids}) | union (datatable(appId:string, database:string) [{rows}])";
    }

    public static string RegisterCompanyCommand(CompanyRegistration company) =>
        $".set-or-replace {CompanyTable} <| {CompanyTable} | where companyKey != {KqlSynthesisBuilder.Str(company.CompanyKey)} | union (datatable(companyKey:string, resetAt:datetime, scale:string, backfillDays:int) " +
        $"[{KqlSynthesisBuilder.Str(company.CompanyKey)}, {KqlSynthesisBuilder.Date(company.ResetAt)}, {KqlSynthesisBuilder.Str(company.Scale)}, {company.Days}])";

    public Task RegisterAppsAsync(IReadOnlyCollection<AppRegistration> apps, CancellationToken ct) =>
        apps.Count == 0 ? Task.CompletedTask : kusto.ExecuteCommandAsync(RegistryDatabase, RegisterAppsCommand(apps), ct);

    public Task RegisterCompanyAsync(CompanyRegistration company, CancellationToken ct) =>
        kusto.ExecuteCommandAsync(RegistryDatabase, RegisterCompanyCommand(company), ct);

    public async Task<IReadOnlyList<CompanyRegistration>> ReadCompaniesAsync(CancellationToken ct)
    {
        var databases = await ListDatabasesAsync(ct);
        if (!databases.Contains(RegistryDatabase)) return [];
        var result = await kusto.QueryAsync(RegistryDatabase, $"{CompanyTable} | project companyKey, resetAt, scale, backfillDays", ct);
        return result.Rows.Select(r => new CompanyRegistration(
            r[0].GetString() ?? "",
            DateTimeOffset.Parse(r[1].GetString()!, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal),
            r[2].GetString() ?? Scales.Laptop,
            r[3].ValueKind == System.Text.Json.JsonValueKind.Number ? r[3].GetInt32() : 8)).ToList();
    }

    public async Task<HashSet<string>> ListDatabasesAsync(CancellationToken ct)
    {
        var result = await kusto.ExecuteCommandAsync(DefaultDatabase, ".show databases | project DatabaseName", ct);
        return result.Rows.Select(r => r[0].GetString() ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Latest request timestamp of a database, or null when it has none.</summary>
    public async Task<DateTimeOffset?> LatestRequestAsync(string database, CancellationToken ct)
    {
        var result = await kusto.QueryAsync(database, "requests | summarize t = max(timestamp)", ct);
        if (result.Rows.Count == 0 || result.Rows[0][0].ValueKind != System.Text.Json.JsonValueKind.String) return null;
        return DateTimeOffset.Parse(result.Rows[0][0].GetString()!, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal);
    }

    public async Task<IReadOnlyDictionary<string, long>> CountRowsAsync(string database, CancellationToken ct)
    {
        var query = string.Join(" | union ", KqlSynthesisBuilder.Tables.Select(t => $"({t} | summarize c = count() | extend t = \"{t}\")"));
        var result = await kusto.QueryAsync(database, query, ct);
        var counts = KqlSynthesisBuilder.Tables.ToDictionary(t => t, _ => 0L, StringComparer.Ordinal);
        foreach (var row in result.Rows) counts[row[1].GetString()!] = row[0].GetInt64();
        return counts;
    }

    private static string Name(string database) => $"['{database}']";

    private static IReadOnlyList<string> LoadSchema()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("DemoGenerator.schema.kql")
            ?? throw new InvalidOperationException("schema.kql is not embedded in DemoGenerator.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd()
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith('.'))
            .ToList();
    }
}
