using System.Data;
using Dapper;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Options;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Infrastructure.ProfileJourneys;

/// <summary>
/// Contract v6 §4.1 store over monitoring.ProfileJourney / monitoring.ProfileJourneyStep. Every
/// statement is filtered by ProfileKey. An upsert deletes the given journeys (their steps cascade)
/// and bulk-inserts the new rows inside one transaction, so a re-synced journey is replaced whole.
/// Rows are read through private row classes, never straight into the positional records.
/// </summary>
public sealed class SqlProfileJourneyStore(IOptions<JourneyStoreOptions> storeOptions, TimeProvider? clock = null) : IProfileJourneyStore
{
    private const int DeleteChunk = 500;
    private const int CommandTimeoutSeconds = 120;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<int> UpsertAsync(
        string profileKey,
        IReadOnlyList<ProfileJourney> journeys,
        IReadOnlyList<ProfileJourneyStep> steps,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileKey);
        ArgumentNullException.ThrowIfNull(journeys);
        ArgumentNullException.ThrowIfNull(steps);

        var key = profileKey.Trim();
        var mine = ProfileJourneyStoreRules.OwnJourneys(key, journeys);
        if (mine.Count == 0) return 0;
        var mySteps = ProfileJourneyStoreRules.OwnSteps(key, mine, steps);

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        foreach (var chunk in mine.Select(j => j.JourneyId).Chunk(DeleteChunk))
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM monitoring.ProfileJourney WHERE ProfileKey = @ProfileKey AND JourneyId IN @Ids",
                new { ProfileKey = new DbString { Value = key, Length = 128 }, Ids = chunk },
                transaction, CommandTimeoutSeconds, cancellationToken: cancellationToken));
        }

        await BulkInsertAsync(connection, transaction, "monitoring.ProfileJourney", JourneyTable(key, mine), cancellationToken);
        if (mySteps.Count > 0)
        {
            await BulkInsertAsync(connection, transaction, "monitoring.ProfileJourneyStep", StepTable(key, mySteps), cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return mine.Count;
    }

    public async Task<IReadOnlyList<ProfileJourney>> ListAsync(ProfileJourneyQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var status = (query.Status ?? "").Trim();
        var droppedOnly = ProfileJourneyStoreRules.IsDroppedFilter(status);

        const string sql = """
            SELECT TOP (@Top) ProfileKey, JourneyId, ServiceKey, FlowKey, EntityKey, StartedAt, EndedAt, DurationMs,
                   Status, LastStepOrder, LastStepKey, StepCount, Failed, SampleOperationId
            FROM monitoring.ProfileJourney
            WHERE ProfileKey = @ProfileKey
              AND StartedAt >= @Since
              AND (@Service IS NULL OR ServiceKey = @Service)
              AND (@Entity IS NULL OR EntityKey = @Entity)
              AND (@Status IS NULL OR Status = @Status)
              AND (@DroppedOnly = 0 OR Status LIKE N'Dropped at step %')
            ORDER BY StartedAt DESC, JourneyId
            """;

        await using var connection = CreateConnection();
        var rows = await connection.QueryAsync<JourneyRow>(new CommandDefinition(sql, new
        {
            Top = Math.Max(1, query.Top),
            ProfileKey = new DbString { Value = query.ProfileKey.Trim(), Length = 128 },
            Since = _clock.GetUtcNow() - query.Window,
            Service = NullableString(query.Service, 128),
            Entity = NullableString(query.Entity, 256),
            Status = NullableString(droppedOnly ? null : status, 64),
            DroppedOnly = droppedOnly,
        }, commandTimeout: CommandTimeoutSeconds, cancellationToken: cancellationToken));
        return rows.Select(r => r.ToDomain()).ToList();
    }

    public async Task<ProfileJourney?> GetAsync(string profileKey, string journeyId, CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        var row = await connection.QuerySingleOrDefaultAsync<JourneyRow>(new CommandDefinition("""
            SELECT ProfileKey, JourneyId, ServiceKey, FlowKey, EntityKey, StartedAt, EndedAt, DurationMs,
                   Status, LastStepOrder, LastStepKey, StepCount, Failed, SampleOperationId
            FROM monitoring.ProfileJourney
            WHERE ProfileKey = @ProfileKey AND JourneyId = @JourneyId
            """, new
        {
            ProfileKey = new DbString { Value = profileKey.Trim(), Length = 128 },
            JourneyId = new DbString { Value = journeyId.Trim(), Length = 128 },
        }, cancellationToken: cancellationToken));
        return row?.ToDomain();
    }

    public async Task<IReadOnlyList<ProfileJourneyStep>> GetStepsAsync(string profileKey, string journeyId, CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        var rows = await connection.QueryAsync<StepRow>(new CommandDefinition("""
            SELECT ProfileKey, JourneyId, StepOrder, StepKey, Name, [Timestamp], DurationMs, Success, ResultCode
            FROM monitoring.ProfileJourneyStep
            WHERE ProfileKey = @ProfileKey AND JourneyId = @JourneyId
            ORDER BY StepOrder
            """, new
        {
            ProfileKey = new DbString { Value = profileKey.Trim(), Length = 128 },
            JourneyId = new DbString { Value = journeyId.Trim(), Length = 128 },
        }, cancellationToken: cancellationToken));
        return rows.Select(r => r.ToDomain()).ToList();
    }

    public async Task<int> PurgeOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        return await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM monitoring.ProfileJourney WHERE StartedAt < @Cutoff",
            new { Cutoff = cutoff }, commandTimeout: CommandTimeoutSeconds, cancellationToken: cancellationToken));
    }

    private static async Task BulkInsertAsync(SqlConnection connection, SqlTransaction transaction, string table, DataTable data, CancellationToken ct)
    {
        using var bulk = new SqlBulkCopy(connection, SqlBulkCopyOptions.CheckConstraints, transaction)
        {
            DestinationTableName = table,
            BulkCopyTimeout = CommandTimeoutSeconds,
        };
        foreach (DataColumn column in data.Columns) bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);
        await bulk.WriteToServerAsync(data, ct);
    }

    private static DataTable JourneyTable(string key, IEnumerable<ProfileJourney> journeys)
    {
        var table = new DataTable();
        table.Columns.Add("ProfileKey", typeof(string));
        table.Columns.Add("JourneyId", typeof(string));
        table.Columns.Add("ServiceKey", typeof(string));
        table.Columns.Add("FlowKey", typeof(string));
        table.Columns.Add("EntityKey", typeof(string));
        table.Columns.Add("StartedAt", typeof(DateTimeOffset));
        table.Columns.Add("EndedAt", typeof(DateTimeOffset));
        table.Columns.Add("DurationMs", typeof(double));
        table.Columns.Add("Status", typeof(string));
        table.Columns.Add("LastStepOrder", typeof(int));
        table.Columns.Add("LastStepKey", typeof(string));
        table.Columns.Add("StepCount", typeof(int));
        table.Columns.Add("Failed", typeof(bool));
        table.Columns.Add("SampleOperationId", typeof(string));
        foreach (var j in journeys)
        {
            table.Rows.Add(key, j.JourneyId, j.ServiceKey, Db(j.FlowKey), Db(j.EntityKey), j.StartedAt, j.EndedAt,
                Math.Max(0, j.DurationMs), j.Status, j.LastStepOrder.HasValue ? j.LastStepOrder.Value : DBNull.Value,
                Db(j.LastStepKey), Math.Max(0, j.StepCount), j.Failed, Db(j.SampleOperationId));
        }
        return table;
    }

    private static DataTable StepTable(string key, IEnumerable<ProfileJourneyStep> steps)
    {
        var table = new DataTable();
        table.Columns.Add("ProfileKey", typeof(string));
        table.Columns.Add("JourneyId", typeof(string));
        table.Columns.Add("StepOrder", typeof(int));
        table.Columns.Add("StepKey", typeof(string));
        table.Columns.Add("Name", typeof(string));
        table.Columns.Add("Timestamp", typeof(DateTimeOffset));
        table.Columns.Add("DurationMs", typeof(double));
        table.Columns.Add("Success", typeof(bool));
        table.Columns.Add("ResultCode", typeof(string));
        foreach (var s in steps)
        {
            table.Rows.Add(key, s.JourneyId, s.StepOrder, Db(s.StepKey), s.Name ?? "", s.Timestamp, Math.Max(0, s.DurationMs),
                s.Success, Db(s.ResultCode));
        }
        return table;
    }

    private static object Db(string? value) => value is null ? DBNull.Value : value;

    // Dapper rejects a null ICustomQueryParameter; a DbString with a null Value is sent as DBNull.
    private static DbString NullableString(string? value, int length) =>
        new() { Value = string.IsNullOrWhiteSpace(value) ? null : value.Trim(), Length = length };

    private SqlConnection CreateConnection()
    {
        var options = storeOptions.Value;
        if (!options.IsConfigured)
        {
            throw new InvalidOperationException($"{JourneyStoreOptions.SectionName}:ConnectionString is not configured.");
        }
        return new SqlConnection(options.ConnectionString);
    }

    private sealed class JourneyRow
    {
        public string ProfileKey { get; set; } = "";
        public string JourneyId { get; set; } = "";
        public string ServiceKey { get; set; } = "";
        public string? FlowKey { get; set; }
        public string? EntityKey { get; set; }
        public DateTimeOffset StartedAt { get; set; }
        public DateTimeOffset EndedAt { get; set; }
        public double DurationMs { get; set; }
        public string Status { get; set; } = "";
        public int? LastStepOrder { get; set; }
        public string? LastStepKey { get; set; }
        public int StepCount { get; set; }
        public bool Failed { get; set; }
        public string? SampleOperationId { get; set; }

        public ProfileJourney ToDomain() => new(ProfileKey, JourneyId, ServiceKey, FlowKey, EntityKey, StartedAt, EndedAt,
            DurationMs, Status, LastStepOrder, LastStepKey, StepCount, Failed, SampleOperationId);
    }

    private sealed class StepRow
    {
        public string ProfileKey { get; set; } = "";
        public string JourneyId { get; set; } = "";
        public int StepOrder { get; set; }
        public string? StepKey { get; set; }
        public string Name { get; set; } = "";
        public DateTimeOffset Timestamp { get; set; }
        public double DurationMs { get; set; }
        public bool Success { get; set; }
        public string? ResultCode { get; set; }

        public ProfileJourneyStep ToDomain() => new(ProfileKey, JourneyId, StepOrder, StepKey, Name, Timestamp, DurationMs, Success, ResultCode);
    }
}
