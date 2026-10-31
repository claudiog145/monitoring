using System.Data;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Options;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Dapper;

namespace JourneyMonitor.Infrastructure.Sql;

/// <summary>SQL-backed IMonkeyRunTracker (monitoring.MonkeyRun) - survives process/container restarts, unlike InMemoryMonkeyRunTracker.</summary>
public sealed class SqlMonkeyRunTracker(IOptions<JourneyStoreOptions> storeOptions) : IMonkeyRunTracker
{
    public async Task RecordRunAsync(string monkeyKey, DateTimeOffset at, CancellationToken ct = default)
    {
        var parameters = new DynamicParameters();
        parameters.Add("MonkeyKey", monkeyKey, DbType.String, size: 64);
        parameters.Add("LastRunAt", at);

        await using var connection = OpenConnection();
        await connection.ExecuteAsync(new CommandDefinition(
            "monitoring.usp_RecordMonkeyRun", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));
    }

    public async Task<DateTimeOffset?> GetLastRunAsync(string monkeyKey, CancellationToken ct = default)
    {
        var parameters = new DynamicParameters();
        parameters.Add("MonkeyKey", monkeyKey, DbType.String, size: 64);

        await using var connection = OpenConnection();
        return await connection.QuerySingleOrDefaultAsync<DateTimeOffset?>(new CommandDefinition(
            "monitoring.usp_GetMonkeyLastRun", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));
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
