using System.Globalization;
using Kusto.Language;
using Kusto.Language.Symbols;
using Kusto.Language.Syntax;

namespace FakeAppInsights.Rewrite;

/// <summary>
/// Applies the REST <c>timespan</c> the way App Insights does (contract v5 §3.1): every reference
/// to one of the six App Insights tables, with or without a <c>database()</c>/<c>app()</c> prefix,
/// is replaced by <c>(T | where timestamp between (from .. to))</c>.
///
/// Unqualified names are resolved with Kusto.Language semantic analysis, so a <c>let</c> variable
/// or a column that happens to be called <c>requests</c> is not mistaken for the table.
/// </summary>
public static class TimespanFilter
{
    private const string DefaultDatabase = "__demo_app__";

    public static string Apply(string kql, DateTimeOffset from, DateTimeOffset to)
    {
        ArgumentNullException.ThrowIfNull(kql);
        if (from > to) throw new ArgumentException("from must not be later than to.", nameof(from));

        var predicate = $"timestamp between ({Datetime(from)} .. {Datetime(to)})";
        var code = KustoCode.ParseAndAnalyze(kql, BuildGlobals(kql));
        var edits = new TextEdits();
        var qualifiedSelectors = new HashSet<SyntaxNode>();

        code.Syntax.WalkNodes(node =>
        {
            if (node is PathExpression path && IsQualifiedTable(path))
            {
                qualifiedSelectors.Add(path.Selector);
                Wrap(edits, kql, path, predicate);
            }
        });

        code.Syntax.WalkNodes(node =>
        {
            if (node is NameReference name &&
                !qualifiedSelectors.Contains(name) &&
                name.ReferencedSymbol is TableSymbol table &&
                AppInsightsSchema.IsTable(table.Name) &&
                code.Globals.IsDatabaseTable(table))
            {
                Wrap(edits, kql, name, predicate);
            }
        });

        return edits.ApplyTo(kql);
    }

    public static string Datetime(DateTimeOffset value) =>
        "datetime(" + value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture) + ")";

    private static void Wrap(TextEdits edits, string kql, SyntaxNode node, string predicate)
    {
        var text = kql.Substring(node.TextStart, node.End - node.TextStart);
        edits.Replace(node.TextStart, node.End - node.TextStart, $"({text} | where {predicate})");
    }

    /// <summary><c>database('x').requests</c>, <c>app('x').requests</c>, <c>cluster('c').database('x').requests</c>.</summary>
    private static bool IsQualifiedTable(PathExpression path)
    {
        if (path.Selector is not NameReference selector || !AppInsightsSchema.IsTable(selector.SimpleName)) return false;

        var qualifier = path.Expression is PathExpression inner ? inner.Selector : path.Expression;
        return qualifier is FunctionCallExpression call &&
               (call.Name.SimpleName.Equals("database", StringComparison.OrdinalIgnoreCase) ||
                call.Name.SimpleName.Equals("app", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// One database holding the six tables for unqualified names, plus one per <c>database('x')</c>
    /// the query names, so the binder resolves those too instead of reporting them as unknown.
    /// </summary>
    private static GlobalState BuildGlobals(string kql)
    {
        var tables = AppInsightsSchema.ToTableSymbols();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { DefaultDatabase };
        KustoCode.Parse(kql).Syntax.WalkNodes(node =>
        {
            if (node is FunctionCallExpression call &&
                call.Name.SimpleName.Equals("database", StringComparison.OrdinalIgnoreCase) &&
                call.ArgumentList.Expressions.Count == 1 &&
                call.ArgumentList.Expressions[0].Element is LiteralExpression { LiteralValue: string db })
            {
                names.Add(db);
            }
        });

        var databases = names.Select(n => new DatabaseSymbol(n, tables)).ToList();
        var cluster = new ClusterSymbol("demo", databases);
        return GlobalState.Default
            .WithClusterList(cluster)
            .WithCluster(cluster)
            .WithDatabase(databases.First(d => d.Name == DefaultDatabase));
    }
}
