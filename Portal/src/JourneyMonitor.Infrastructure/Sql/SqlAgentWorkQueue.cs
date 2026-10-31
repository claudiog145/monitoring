using System.Data;
using Dapper;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Options;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Infrastructure.Sql;

/// <summary>
/// SQL-backed monkey-to-monkey work queue (monitoring.AgentWorkQueue). Every operation is a single
/// stored procedure call: the claim in particular must stay one atomic statement server-side, so
/// this class never reads a candidate and writes it back from C#.
/// </summary>
public sealed class SqlAgentWorkQueue(IOptions<JourneyStoreOptions> storeOptions) : IAgentWorkQueue
{
    public async Task<bool> EnqueueAsync(AgentWorkItem item, CancellationToken ct = default)
    {
        var parameters = new DynamicParameters();
        parameters.Add("Id", item.Id);
        parameters.Add("SourceMonkey", item.SourceMonkey, DbType.String, size: 64);
        parameters.Add("PayloadType", item.PayloadType, DbType.String, size: 64);
        parameters.Add("PayloadId", item.PayloadId, DbType.String, size: 128);
        parameters.Add("DedupKey", item.DedupKey, DbType.String, size: 512);
        parameters.Add("Priority", item.Priority);
        parameters.Add("Status", item.Status, DbType.String, size: 32);
        parameters.Add("MaxAttempts", item.MaxAttempts);
        parameters.Add("EnqueuedAt", item.EnqueuedAt);

        await using var connection = OpenConnection();
        var inserted = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "monitoring.usp_EnqueueAgentWork", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));
        return inserted > 0;
    }

    public async Task<AgentWorkItem?> TryLeaseAsync(string payloadType, string leasedBy, TimeSpan leaseDuration, CancellationToken ct = default)
    {
        var parameters = new DynamicParameters();
        parameters.Add("PayloadType", payloadType, DbType.String, size: 64);
        parameters.Add("LeasedBy", leasedBy, DbType.String, size: 128);
        parameters.Add("LeasedUntil", DateTimeOffset.UtcNow.Add(leaseDuration));

        await using var connection = OpenConnection();
        var row = await connection.QuerySingleOrDefaultAsync<WorkItemRow>(new CommandDefinition(
            "monitoring.usp_TryLeaseAgentWork", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));
        return row?.ToDomain();
    }

    public async Task CompleteAsync(Guid itemId, string leasedBy, CancellationToken ct = default)
    {
        var parameters = new DynamicParameters();
        parameters.Add("Id", itemId);
        parameters.Add("LeasedBy", leasedBy, DbType.String, size: 128);

        await using var connection = OpenConnection();
        await connection.ExecuteAsync(new CommandDefinition(
            "monitoring.usp_CompleteAgentWork", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));
    }

    public async Task FailAsync(Guid itemId, string leasedBy, string error, CancellationToken ct = default)
    {
        var parameters = new DynamicParameters();
        parameters.Add("Id", itemId);
        parameters.Add("Error", error, DbType.String, size: 1024);
        parameters.Add("LeasedBy", leasedBy, DbType.String, size: 128);

        await using var connection = OpenConnection();
        await connection.ExecuteAsync(new CommandDefinition(
            "monitoring.usp_FailAgentWork", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<AgentWorkItem>> GetQueuedAsync(string payloadType, int top, CancellationToken ct = default)
    {
        var parameters = new DynamicParameters();
        parameters.Add("PayloadType", payloadType, DbType.String, size: 64);
        parameters.Add("Top", top);

        await using var connection = OpenConnection();
        var rows = await connection.QueryAsync<WorkItemRow>(new CommandDefinition(
            "monitoring.usp_GetQueuedAgentWork", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));
        return rows.Select(r => r.ToDomain()).ToList();
    }

    public async Task<AgentWorkQueueStats> GetStatsAsync(string payloadType, CancellationToken ct = default)
    {
        var parameters = new DynamicParameters();
        parameters.Add("PayloadType", payloadType, DbType.String, size: 64);

        await using var connection = OpenConnection();
        // Mapped through a row type rather than straight onto the record: Dapper matches a
        // positional record by finding a constructor for every column it got back, so the moment
        // the procedure grew DiscardedTotal - an init-only property, not a constructor parameter -
        // materialization started throwing at runtime while still compiling cleanly.
        var row = await connection.QuerySingleOrDefaultAsync<StatsRow>(new CommandDefinition(
            "monitoring.usp_GetAgentWorkQueueStats", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));
        return row?.ToDomain() ?? AgentWorkQueueStats.Empty;
    }

    public async Task<int> DiscardQueuedAsync(
        string payloadType,
        IReadOnlyCollection<Guid>? itemIds,
        string reason,
        string discardedBy,
        CancellationToken ct = default)
    {
        var parameters = new DynamicParameters();
        parameters.Add("PayloadType", payloadType, DbType.String, size: 64);
        // NULL and "empty list" mean opposite things to the procedure, so an explicit empty
        // selection must never arrive as NULL - that would clear the whole backlog.
        parameters.Add("Ids", itemIds is null ? null : string.Join(',', itemIds), DbType.String, size: -1);
        parameters.Add("Reason", reason, DbType.String, size: 1024);
        parameters.Add("DiscardedBy", discardedBy, DbType.String, size: 128);

        await using var connection = OpenConnection();
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "monitoring.usp_DiscardAgentWork", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));
    }

    private SqlConnection OpenConnection()
    {
        var options = storeOptions.Value;
        if (!options.IsConfigured)
        {
            throw new InvalidOperationException($"{JourneyStoreOptions.SectionName}:ConnectionString is not configured.");
        }
        return new SqlConnection(options.ConnectionString);
    }

    private sealed record StatsRow(
        int Queued, int Leased, int DoneToday, int FailedToday,
        int AbandonedTotal, int DiscardedTotal, DateTimeOffset? OldestQueuedAt)
    {
        public AgentWorkQueueStats ToDomain() =>
            new(Queued, Leased, DoneToday, FailedToday, AbandonedTotal, OldestQueuedAt)
            {
                DiscardedTotal = DiscardedTotal,
            };
    }

    private sealed record WorkItemRow(
        Guid Id, string SourceMonkey, string PayloadType, string PayloadId, string DedupKey,
        int Priority, string Status, int Attempts, int MaxAttempts,
        DateTimeOffset? LeasedUntil, string? LeasedBy, string? LastError,
        DateTimeOffset EnqueuedAt, DateTimeOffset? CompletedAt)
    {
        public AgentWorkItem ToDomain() => new()
        {
            Id = Id,
            SourceMonkey = SourceMonkey,
            PayloadType = PayloadType,
            PayloadId = PayloadId,
            DedupKey = DedupKey,
            Priority = Priority,
            Status = Status,
            Attempts = Attempts,
            MaxAttempts = MaxAttempts,
            LeasedUntil = LeasedUntil,
            LeasedBy = LeasedBy,
            LastError = LastError,
            EnqueuedAt = EnqueuedAt,
            CompletedAt = CompletedAt,
        };
    }
}
