using System.Data;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Options;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Dapper;

namespace JourneyMonitor.Infrastructure.Sql;

public sealed class SqlLatencySpikeSettingsStore(IOptions<JourneyStoreOptions> storeOptions) : ILatencySpikeSettingsStore
{
    public async Task<IReadOnlyList<LatencySpikeSetting>> GetAllAsync(CancellationToken cancellationToken)
    {
        await using var connection = OpenConnection();
        var rows = await connection.QueryAsync<LatencySpikeSettingRow>(new CommandDefinition(
            "monitoring.usp_GetLatencySpikeSettings",
            commandType: CommandType.StoredProcedure,
            cancellationToken: cancellationToken));

        return rows.Select(r => new LatencySpikeSetting(
            r.Domain,
            r.SpikeThresholdMs,
            r.SpikeOccurrenceThreshold,
            r.SpikeWindowMinutes,
            r.UpdatedAt,
            r.UpdatedBy)).ToList();
    }

    public async Task UpsertAsync(
        string domain,
        int? spikeThresholdMs,
        int? spikeOccurrenceThreshold,
        int? spikeWindowMinutes,
        string changedBy,
        CancellationToken cancellationToken)
    {
        var parameters = new DynamicParameters();
        parameters.Add("Domain", domain, DbType.String, size: 32);
        parameters.Add("SpikeThresholdMs", spikeThresholdMs, DbType.Int32);
        parameters.Add("SpikeOccurrenceThreshold", spikeOccurrenceThreshold, DbType.Int32);
        parameters.Add("SpikeWindowMinutes", spikeWindowMinutes, DbType.Int32);
        parameters.Add("ChangedBy", changedBy, DbType.String, size: 128);

        await using var connection = OpenConnection();
        await connection.ExecuteAsync(new CommandDefinition(
            "monitoring.usp_UpsertLatencySpikeSetting",
            parameters,
            commandType: CommandType.StoredProcedure,
            cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<LatencySpikeSettingAuditEntry>> GetAuditAsync(string? domain, CancellationToken cancellationToken)
    {
        var parameters = new DynamicParameters();
        parameters.Add("Domain", domain, DbType.String, size: 32);

        await using var connection = OpenConnection();
        var rows = await connection.QueryAsync<LatencySpikeSettingAuditRow>(new CommandDefinition(
            "monitoring.usp_GetLatencySpikeSettingAudit",
            parameters,
            commandType: CommandType.StoredProcedure,
            cancellationToken: cancellationToken));

        return rows.Select(r => new LatencySpikeSettingAuditEntry(
            r.Domain,
            r.SpikeThresholdMs,
            r.SpikeOccurrenceThreshold,
            r.SpikeWindowMinutes,
            r.ChangedAt,
            r.ChangedBy)).ToList();
    }

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

    private sealed record LatencySpikeSettingRow(
        string Domain,
        int? SpikeThresholdMs,
        int? SpikeOccurrenceThreshold,
        int? SpikeWindowMinutes,
        DateTimeOffset? UpdatedAt,
        string? UpdatedBy);

    private sealed record LatencySpikeSettingAuditRow(
        string Domain,
        int? SpikeThresholdMs,
        int? SpikeOccurrenceThreshold,
        int? SpikeWindowMinutes,
        DateTimeOffset ChangedAt,
        string ChangedBy);
}
