using System.Text.RegularExpressions;
using Kusto.Language;
using Kusto.Language.Syntax;

namespace FakeAppInsights.Rewrite;

/// <summary>
/// Points App Insights cross-resource references at the demo engine (contract v5 §3.1):
/// <c>app('&lt;ARM id | name | appId&gt;')</c> becomes <c>database('&lt;db&gt;')</c>. Works on the
/// Kusto.Language syntax tree, so text inside string literals and comments is never touched.
/// Tables without a prefix are left alone: they resolve in the URL's application database.
/// </summary>
public static partial class AppReferenceRewriter
{
    [GeneratedRegex(@"^/subscriptions/[^/]+/resourcegroups/[^/]+/providers/microsoft\.insights/components/(?<name>[^/]+)/?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ArmComponentId();

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,260}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeDatabaseName();

    public static string Rewrite(string kql, IApplicationCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(kql);
        ArgumentNullException.ThrowIfNull(catalog);

        var code = KustoCode.Parse(kql);
        var edits = new TextEdits();
        Exception? failure = null;

        code.Syntax.WalkNodes(node =>
        {
            if (failure is not null || node is not FunctionCallExpression call) return;

            var name = call.Name.SimpleName;
            if (name.Equals("workspace", StringComparison.OrdinalIgnoreCase))
            {
                failure = new UnsupportedKqlException("workspace() is not supported by the demo telemetry server; use app().");
                return;
            }
            if (!name.Equals("app", StringComparison.OrdinalIgnoreCase)) return;

            var argument = SingleStringArgument(call);
            if (argument is null)
            {
                failure = new UnsupportedKqlException("app() takes exactly one string literal in the demo telemetry server.");
                return;
            }

            var database = Resolve(argument, catalog);
            if (database is null)
            {
                failure = new UnknownApplicationException(argument);
                return;
            }

            edits.Replace(call.TextStart, call.End - call.TextStart, $"database('{database}')");
        });

        if (failure is not null) throw failure;
        return edits.ApplyTo(kql);
    }

    /// <summary>The database an <c>app()</c> argument names, or null when it names none.</summary>
    public static string? Resolve(string application, IApplicationCatalog catalog)
    {
        var value = application.Trim();
        var arm = ArmComponentId().Match(value);
        var candidate = arm.Success ? arm.Groups["name"].Value : value;

        string? database = null;
        if (catalog.DatabaseExists(candidate)) database = candidate;
        database ??= catalog.FindDatabaseByAppId(candidate);
        if (database is null && arm.Success) database = catalog.FindDatabaseByAppId(value);

        return database is not null && SafeDatabaseName().IsMatch(database) ? database : null;
    }

    private static string? SingleStringArgument(FunctionCallExpression call)
    {
        var arguments = call.ArgumentList.Expressions;
        if (arguments.Count != 1) return null;
        return arguments[0].Element is LiteralExpression literal && literal.LiteralValue is string text ? text : null;
    }
}
