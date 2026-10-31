using System.Data;
using System.Text.Json;
using Dapper;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Options;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Infrastructure.Sql;
/// <summary>
/// SQL Server implementation of <see cref="IJourneyStore"/>. Dapper rather than EF Core on
/// purpose: both directions are stored-procedure calls with OUTPUT parameters, and the read
/// side projects into the raw row bag (<see cref="TelemetryJourneyRow"/>) instead of entities,
/// so there is nothing for a DbContext to model. The short-lived query cache is
/// <see cref="Caching.InMemoryTelemetryQueryCache"/>; see ARCHITECTURE.md.
///
/// The per-vertical procedures own the KQL-column-to-table mapping (OPENJSON ... WITH), which
/// is why the rows are handed over exactly as Application Insights returned them.
/// </summary>
public sealed class SqlJourneyStore(IOptions<JourneyStoreOptions> storeOptions) : IJourneyStore, IJourneySnapshotStore
{
    /// <summary>Portal domain (plural, as used on the wire) to SQL schema name (singular).</summary>
    private static readonly Dictionary<string, string> SchemaByDomain = new(StringComparer.OrdinalIgnoreCase)
    {
        ["hotels"] = "hotel",
        ["cars"] = "car",
        ["cruises"] = "cruise",
        ["air"] = "air",
    };

    private SqlConnection OpenConnection()
    {
        var options = storeOptions.Value;
        if (!options.IsConfigured)
        {
            throw new InvalidOperationException(
                $"{JourneyStoreOptions.SectionName}:ConnectionString is not configured. Callers must check IsConfigured first.");
        }

        return new SqlConnection(options.ConnectionString);
    }

    public async Task<JourneyUpsertResult> UpsertAsync(JourneyUpsertRequest request, CancellationToken cancellationToken)
    {
        var schema = ResolveSchema(request.Domain);

        var parameters = new DynamicParameters();
        parameters.Add("Window", request.Window, DbType.String, size: 16);
        parameters.Add("Environment", request.Environment, DbType.String, size: 32);
        parameters.Add("ClubId", request.ClubId, DbType.String, size: 64);
        parameters.Add("Host", request.Host, DbType.String, size: 256);
        parameters.Add("Source", request.Source, DbType.String, size: 32);
        parameters.Add("RowsJson", JsonSerializer.Serialize(request.Rows), DbType.String, size: -1);
        parameters.Add("QueryRunId", null, DbType.Guid, ParameterDirection.Output);
        parameters.Add("RecordCount", null, DbType.Int32, ParameterDirection.Output);

        // Every vertical upserts on its own grain, so all four report how the payload split.
        parameters.Add("InsertedCount", null, DbType.Int32, ParameterDirection.Output);
        parameters.Add("UpdatedCount", null, DbType.Int32, ParameterDirection.Output);
        parameters.Add("UnchangedCount", null, DbType.Int32, ParameterDirection.Output);

        await using var connection = OpenConnection();
        var command = new CommandDefinition(
            $"[{schema}].[usp_IngestKqlRows]",
            parameters,
            commandType: CommandType.StoredProcedure,
            cancellationToken: cancellationToken);
        await connection.ExecuteAsync(command);

        return new JourneyUpsertResult(
            parameters.Get<Guid>("QueryRunId"),
            parameters.Get<int?>("RecordCount") ?? 0,
            parameters.Get<int?>("InsertedCount"),
            parameters.Get<int?>("UpdatedCount"),
            parameters.Get<int?>("UnchangedCount"));
    }

    public async Task<StoredJourneys?> GetLatestAsync(string domain, CancellationToken cancellationToken)
    {
        var schema = ResolveSchema(domain);

        var parameters = new DynamicParameters();
        parameters.Add("QueryRunId", null, DbType.Guid, ParameterDirection.Output);
        parameters.Add("RecordCount", null, DbType.Int32, ParameterDirection.Output);
        parameters.Add("FetchedAt", null, DbType.DateTimeOffset, ParameterDirection.Output);
        parameters.Add("Window", null, DbType.String, ParameterDirection.Output, size: 16);

        await using var connection = OpenConnection();
        var command = new CommandDefinition(
            $"[{schema}].[usp_GetLatestJourneys]",
            parameters,
            commandType: CommandType.StoredProcedure,
            cancellationToken: cancellationToken);

        // Materialize before reading OUTPUT parameters: they are only populated once the
        // result set has been fully consumed.
        var records = (await connection.QueryAsync(command)).ToList();

        var queryRunId = parameters.Get<Guid?>("QueryRunId");
        if (queryRunId is null) return null;

        var rows = records
            .Cast<IDictionary<string, object?>>()
            .Select(record => KqlRowAssembler.Assemble(domain, record))
            .ToList();

        return new StoredJourneys(
            queryRunId.Value,
            parameters.Get<int?>("RecordCount") ?? rows.Count,
            parameters.Get<DateTimeOffset?>("FetchedAt"),
            parameters.Get<string?>("Window"),
            rows);
    }

    async Task<JourneySnapshot> IJourneySnapshotStore.GetLatestAsync(
        JourneySnapshotQuery query,
        CancellationToken cancellationToken)
    {
        var schema = ResolveSchema(query.Domain);
        var parameters = new DynamicParameters();
        parameters.Add("QueryRunId", null, DbType.Guid, ParameterDirection.Output);
        parameters.Add("RecordCount", null, DbType.Int32, ParameterDirection.Output);
        parameters.Add("FetchedAt", null, DbType.DateTimeOffset, ParameterDirection.Output);
        parameters.Add("Window", null, DbType.String, ParameterDirection.Output, size: 16);
        parameters.Add("Environment", null, DbType.String, ParameterDirection.Output, size: 32);
        parameters.Add("RequestedClubId", query.ClubId, DbType.String, size: 64);
        parameters.Add("ClubId", null, DbType.String, ParameterDirection.Output, size: 64);
        parameters.Add("Host", null, DbType.String, ParameterDirection.Output, size: 256);
        parameters.Add("SearchId", query.SearchId, DbType.String, size: 256);
        parameters.Add("Status", query.Status, DbType.String, size: 64);
        parameters.Add("RowCap", query.RowCap, DbType.Int32);
        parameters.Add("Since", query.Since, DbType.DateTimeOffset);

        await using var connection = OpenConnection();
        var command = new CommandDefinition(
            $"[{schema}].[usp_GetLatestJourneys]",
            parameters,
            commandType: CommandType.StoredProcedure,
            cancellationToken: cancellationToken);
        var records = (await connection.QueryAsync(command)).ToList();
        var rows = records
            .Cast<IDictionary<string, object?>>()
            .Select(record => KqlRowAssembler.Assemble(query.Domain, record))
            .ToList();

        return new JourneySnapshot(
            query.Domain,
            parameters.Get<string?>("Window") ?? "",
            parameters.Get<string?>("Environment"),
            parameters.Get<string?>("ClubId") ?? query.ClubId,
            parameters.Get<string?>("Host"),
            parameters.Get<DateTimeOffset?>("FetchedAt"),
            rows);
    }

    private static string ResolveSchema(string domain) =>
        SchemaByDomain.TryGetValue(domain, out var schema)
            ? schema
            : throw new ArgumentOutOfRangeException(nameof(domain), domain, "Unknown journey domain.");
}
