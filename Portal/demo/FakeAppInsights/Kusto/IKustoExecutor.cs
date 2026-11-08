using System.Text.Json;

namespace FakeAppInsights.Kusto;

/// <summary>A column of a Kusto result table: <c>ColumnType</c> is the KQL type (<c>long</c>, <c>dynamic</c>…), <c>DataType</c> the CLR one.</summary>
public sealed record KustoColumn(string Name, string ColumnType, string DataType);

/// <summary>One data table of a Kusto response; metadata tables are never represented.</summary>
public sealed record KustoTable(string Name, IReadOnlyList<KustoColumn> Columns, IReadOnlyList<IReadOnlyList<JsonElement>> Rows);

/// <summary>The engine rejected the request (syntax, semantics, unknown database…). Maps to 400.</summary>
public sealed class KustoQueryException(string message, string? code = null) : Exception(message)
{
    public string? Code { get; } = code;
}

/// <summary>The engine could not be reached or answered with a server error. Maps to 503.</summary>
public sealed class KustoUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Runs KQL on the demo engine (Kustainer). Only the result tables are returned.</summary>
public interface IKustoExecutor
{
    Task<IReadOnlyList<KustoTable>> QueryAsync(string database, string csl, CancellationToken cancellationToken);

    Task<IReadOnlyList<KustoTable>> ManagementAsync(string database, string csl, CancellationToken cancellationToken);
}
