using System.Data;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Options;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Dapper;

namespace JourneyMonitor.Infrastructure.Sql;

/// <summary>SQL-backed INotificationLogRepository (monitoring.NotificationLog) - survives process/container restarts, unlike InMemoryNotificationLogRepository.</summary>
public sealed class SqlNotificationLogRepository(IOptions<JourneyStoreOptions> storeOptions) : INotificationLogRepository
{
    public async Task<NotificationLogEntry> AddAsync(NotificationLogEntry entry, CancellationToken ct = default)
    {
        var parameters = new DynamicParameters();
        parameters.Add("Id", entry.Id);
        parameters.Add("SourceType", entry.SourceType, DbType.String, size: 32);
        parameters.Add("SourceId", entry.SourceId);
        parameters.Add("Channel", entry.Channel, DbType.String, size: 16);
        parameters.Add("ProviderUsed", entry.ProviderUsed, DbType.String, size: 32);
        parameters.Add("Success", entry.Success, DbType.Boolean);
        parameters.Add("ErrorMessage", entry.ErrorMessage, DbType.String, size: 1024);
        parameters.Add("SentAt", entry.SentAt);
        parameters.Add("Subject", entry.Subject, DbType.String, size: 512);

        await using var connection = OpenConnection();
        await connection.ExecuteAsync(new CommandDefinition(
            "monitoring.usp_AddNotificationLog", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));
        return entry;
    }

    public async Task<IReadOnlyList<NotificationLogEntry>> GetBySourceAsync(string sourceType, Guid sourceId, CancellationToken ct = default)
    {
        var parameters = new DynamicParameters();
        parameters.Add("SourceType", sourceType, DbType.String, size: 32);
        parameters.Add("SourceId", sourceId);

        await using var connection = OpenConnection();
        var rows = await connection.QueryAsync<NotificationLogEntry>(new CommandDefinition(
            "monitoring.usp_GetNotificationLogBySource", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<IReadOnlyList<NotificationLogEntry>> GetAllAsync(CancellationToken ct = default)
    {
        await using var connection = OpenConnection();
        var rows = await connection.QueryAsync<NotificationLogEntry>(new CommandDefinition(
            "monitoring.usp_GetAllNotificationLogs", commandType: CommandType.StoredProcedure, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<IReadOnlyList<NotificationLogEntry>> GetUndeliveredToastsAsync(DateTimeOffset since, CancellationToken ct = default)
    {
        var parameters = new DynamicParameters();
        parameters.Add("Since", since);

        await using var connection = OpenConnection();
        var rows = await connection.QueryAsync<NotificationLogEntry>(new CommandDefinition(
            "monitoring.usp_GetUndeliveredToasts", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));
        return rows.ToList();
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
}
