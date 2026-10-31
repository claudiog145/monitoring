using System.Data;
using Dapper;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Options;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Infrastructure.Sql;

/// <summary>Dapper pass-through over monitoring.usp_*AgentPause* - see CreateMonitoringAgentPause.sql.</summary>
public sealed class SqlAgentPauseStore(IOptions<JourneyStoreOptions> storeOptions) : IAgentPauseStore
{
    public async Task<IReadOnlyList<AgentPauseSetting>> GetAllAsync(CancellationToken cancellationToken)
    {
        await using var connection = OpenConnection();
        var rows = await connection.QueryAsync<AgentPauseRow>(new CommandDefinition(
            "monitoring.usp_GetAllAgentPauses",
            commandType: CommandType.StoredProcedure,
            cancellationToken: cancellationToken));

        return rows.Select(Map).ToList();
    }

    public async Task<AgentPauseSetting?> GetAsync(string monkeyKey, CancellationToken cancellationToken)
    {
        var parameters = new DynamicParameters();
        parameters.Add("MonkeyKey", monkeyKey, DbType.String, size: 64);

        await using var connection = OpenConnection();
        var row = await connection.QuerySingleOrDefaultAsync<AgentPauseRow>(new CommandDefinition(
            "monitoring.usp_GetAgentPause",
            parameters,
            commandType: CommandType.StoredProcedure,
            cancellationToken: cancellationToken));

        return row is null ? null : Map(row);
    }

    public async Task<AgentPauseSetting> UpsertAsync(string monkeyKey, bool isPaused, string changedBy, CancellationToken cancellationToken)
    {
        var parameters = new DynamicParameters();
        parameters.Add("MonkeyKey", monkeyKey, DbType.String, size: 64);
        parameters.Add("IsPaused", isPaused, DbType.Boolean);
        parameters.Add("ChangedBy", changedBy, DbType.String, size: 128);

        await using var connection = OpenConnection();
        var row = await connection.QuerySingleAsync<AgentPauseRow>(new CommandDefinition(
            "monitoring.usp_UpsertAgentPause",
            parameters,
            commandType: CommandType.StoredProcedure,
            cancellationToken: cancellationToken));

        return Map(row);
    }

    private static AgentPauseSetting Map(AgentPauseRow row) =>
        new(row.MonkeyKey, row.IsPaused, row.UpdatedAt, row.UpdatedBy);

    private SqlConnection OpenConnection()
    {
        var options = storeOptions.Value;
        if (!options.IsConfigured)
        {
            throw new InvalidOperationException(
                $"{JourneyStoreOptions.SectionName}:ConnectionString is not configured.");
        }
        return new SqlConnection(options.ConnectionString);
    }

    private sealed record AgentPauseRow(string MonkeyKey, bool IsPaused, DateTimeOffset UpdatedAt, string UpdatedBy);
}
