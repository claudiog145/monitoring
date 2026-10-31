using System.Data;
using Dapper;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Options;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Infrastructure.Sql;

/// <summary>Dapper pass-through over monitoring.usp_*AgentBudget* - see CreateMonitoringAgentBudget.sql.</summary>
public sealed class SqlAgentBudgetStore(IOptions<JourneyStoreOptions> storeOptions) : IAgentBudgetStore
{
    public async Task<IReadOnlyList<AgentBudgetSetting>> GetAllAsync(CancellationToken cancellationToken)
    {
        await using var connection = OpenConnection();
        var rows = await connection.QueryAsync<AgentBudgetRow>(new CommandDefinition(
            "monitoring.usp_GetAllAgentBudgets",
            commandType: CommandType.StoredProcedure,
            cancellationToken: cancellationToken));

        return rows.Select(Map).ToList();
    }

    public async Task<AgentBudgetSetting> UpsertAsync(AgentBudgetSetting setting, string changedBy, CancellationToken cancellationToken)
    {
        var parameters = new DynamicParameters();
        parameters.Add("MonkeyKey", setting.MonkeyKey, DbType.String, size: 64);
        parameters.Add("MaxRunsPerDay", setting.MaxRunsPerDay, DbType.Int32);
        parameters.Add("DebateModels", setting.DebateModels, DbType.String, size: 400);
        parameters.Add("DebateRounds", setting.DebateRounds, DbType.Int32);
        parameters.Add("MaxHelpHops", setting.MaxHelpHops, DbType.Int32);
        parameters.Add("SkipDebateWhenContributorsBelow", setting.SkipDebateWhenContributorsBelow, DbType.Int32);
        parameters.Add("ChangedBy", changedBy, DbType.String, size: 128);

        await using var connection = OpenConnection();
        var row = await connection.QuerySingleAsync<AgentBudgetRow>(new CommandDefinition(
            "monitoring.usp_UpsertAgentBudget",
            parameters,
            commandType: CommandType.StoredProcedure,
            cancellationToken: cancellationToken));

        return Map(row);
    }

    public async Task<IReadOnlyList<AgentBudgetSettingAudit>> GetAuditAsync(string monkeyKey, int top, CancellationToken cancellationToken)
    {
        var parameters = new DynamicParameters();
        parameters.Add("MonkeyKey", monkeyKey, DbType.String, size: 64);
        parameters.Add("Top", top, DbType.Int32);

        await using var connection = OpenConnection();
        var rows = await connection.QueryAsync<AgentBudgetAuditRow>(new CommandDefinition(
            "monitoring.usp_GetAgentBudgetAudit",
            parameters,
            commandType: CommandType.StoredProcedure,
            cancellationToken: cancellationToken));

        return rows.Select(r => new AgentBudgetSettingAudit(
            r.AuditId, r.MonkeyKey, r.MaxRunsPerDay, r.DebateModels, r.DebateRounds,
            r.MaxHelpHops, r.SkipDebateWhenContributorsBelow, r.ChangedAt, r.ChangedBy)).ToList();
    }

    private static AgentBudgetSetting Map(AgentBudgetRow row) =>
        new(row.MonkeyKey, row.MaxRunsPerDay, row.DebateModels, row.DebateRounds,
            row.MaxHelpHops, row.SkipDebateWhenContributorsBelow, row.UpdatedAt, row.UpdatedBy);

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

    private sealed record AgentBudgetRow(
        string MonkeyKey, int? MaxRunsPerDay, string? DebateModels, int? DebateRounds,
        int? MaxHelpHops, int? SkipDebateWhenContributorsBelow,
        DateTimeOffset UpdatedAt, string UpdatedBy);

    private sealed record AgentBudgetAuditRow(
        long AuditId, string MonkeyKey, int? MaxRunsPerDay, string? DebateModels, int? DebateRounds,
        int? MaxHelpHops, int? SkipDebateWhenContributorsBelow,
        DateTimeOffset ChangedAt, string ChangedBy);
}
