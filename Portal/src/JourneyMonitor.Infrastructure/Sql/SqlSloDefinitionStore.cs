using System.Data;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Options;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Dapper;

namespace JourneyMonitor.Infrastructure.Sql;

public sealed class SqlSloDefinitionStore(IOptions<JourneyStoreOptions> storeOptions) : ISloDefinitionStore
{
    public async Task<IReadOnlyList<SloDefinition>> GetAllAsync(CancellationToken cancellationToken)
    {
        await using var connection = OpenConnection();
        var rows = await connection.QueryAsync<SloDefinitionRow>(new CommandDefinition(
            "monitoring.usp_GetAllSloDefinitions",
            commandType: CommandType.StoredProcedure,
            cancellationToken: cancellationToken));

        return rows.Select(r => new SloDefinition(r.Domain, r.Stage, r.SignalType, r.TargetThresholdMs, r.TargetPercentage, r.WindowDays, r.Enabled, r.UpdatedAt, r.UpdatedBy)).ToList();
    }

    public async Task UpsertAsync(SloDefinition definition, string changedBy, CancellationToken cancellationToken)
    {
        var parameters = new DynamicParameters();
        parameters.Add("Domain", definition.Domain, DbType.String, size: 32);
        parameters.Add("Stage", definition.Stage, DbType.String, size: 128);
        parameters.Add("SignalType", definition.SignalType, DbType.String, size: 32);
        parameters.Add("TargetThresholdMs", definition.TargetThresholdMs, DbType.Int32);
        parameters.Add("TargetPercentage", definition.TargetPercentage, DbType.Decimal, size: 5, precision: 5, scale: 2);
        parameters.Add("WindowDays", definition.WindowDays, DbType.Int32);
        parameters.Add("Enabled", definition.Enabled, DbType.Boolean);
        parameters.Add("ChangedBy", changedBy, DbType.String, size: 128);

        await using var connection = OpenConnection();
        await connection.ExecuteAsync(new CommandDefinition(
            "monitoring.usp_UpsertSloDefinition",
            parameters,
            commandType: CommandType.StoredProcedure,
            cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<SloDefinitionAuditEntry>> GetAuditAsync(CancellationToken cancellationToken)
    {
        await using var connection = OpenConnection();
        var rows = await connection.QueryAsync<SloDefinitionAuditRow>(new CommandDefinition(
            "monitoring.usp_GetSloDefinitionAudit",
            commandType: CommandType.StoredProcedure,
            cancellationToken: cancellationToken));

        return rows.Select(r => new SloDefinitionAuditEntry(r.Domain, r.Stage, r.SignalType, r.TargetThresholdMs, r.TargetPercentage, r.WindowDays, r.Enabled, r.ChangedAt, r.ChangedBy)).ToList();
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

    private sealed record SloDefinitionRow(string Domain, string? Stage, string SignalType, int? TargetThresholdMs, decimal TargetPercentage, int WindowDays, bool Enabled, DateTimeOffset? UpdatedAt, string? UpdatedBy);

    private sealed record SloDefinitionAuditRow(string Domain, string? Stage, string SignalType, int? TargetThresholdMs, decimal TargetPercentage, int WindowDays, bool Enabled, DateTimeOffset ChangedAt, string ChangedBy);
}
