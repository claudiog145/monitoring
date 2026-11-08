using FakeAppInsights.Rewrite;
using Kusto.Language;
using Kusto.Language.Syntax;

namespace FakeAppInsights.Query;

/// <summary>
/// Finds the constructs <see cref="TimespanFilter"/> cannot filter because they read tables without
/// naming them as a table expression: <c>union *</c> (any wildcard table name), <c>find</c> and a
/// <c>search</c> that is not piped from a table (their <c>in (…)</c> clause only takes table names, so
/// a filtered sub-query cannot go there). The server rejects them with 400 when a timespan is present
/// (documented in the project README). <c>T | search …</c> is fine: <c>T</c> is filtered.
/// </summary>
public static class UnfilterableQueryDetector
{
    /// <summary>A description of the first unfilterable construct, or null when the query can be filtered.</summary>
    public static string? Find(string kql)
    {
        ArgumentNullException.ThrowIfNull(kql);
        string? reason = null;

        KustoCode.Parse(kql).Syntax.WalkNodes(node =>
        {
            if (reason is not null) return;
            reason = node switch
            {
                UnionOperator union when union.Expressions.Any(e => HasWildcardName(e.Element)) => "'union' with a wildcard table name",
                FindOperator => "'find'",
                SearchOperator search when !HasPipeInput(search) => "a 'search' that is not piped from a table",
                _ => null,
            };
        });

        return reason is null
            ? null
            : $"The demo telemetry server cannot apply 'timespan' to {reason}; name the tables explicitly or omit 'timespan'.";
    }

    private static bool HasPipeInput(SyntaxNode node) =>
        node.Parent is PipeExpression pipe && ReferenceEquals(pipe.Operator, node);

    private static bool HasWildcardName(SyntaxNode? node)
    {
        if (node is null) return false;
        var found = false;
        node.WalkNodes(n =>
        {
            if (n is NameReference { Name: WildcardedName or BracketedWildcardedName }) found = true;
        });
        return found;
    }
}
