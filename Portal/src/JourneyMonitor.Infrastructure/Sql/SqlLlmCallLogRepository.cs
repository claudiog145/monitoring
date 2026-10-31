using System.Data;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Options;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Dapper;

namespace JourneyMonitor.Infrastructure.Sql;

/// <summary>SQL-backed ILlmCallLogRepository (monitoring.LlmCallLog) - survives process/container restarts, unlike InMemoryLlmCallLogRepository.</summary>
public sealed class SqlLlmCallLogRepository(IOptions<JourneyStoreOptions> storeOptions) : ILlmCallLogRepository
{
    public async Task<LlmCallLog> AddAsync(LlmCallLog log, CancellationToken ct = default)
    {
        var parameters = new DynamicParameters();
        parameters.Add("Id", log.Id);
        parameters.Add("DoctorReportId", log.DoctorReportId);
        parameters.Add("FindingId", log.FindingId);
        parameters.Add("PromptText", log.PromptText, DbType.String, size: -1);
        parameters.Add("RawResponseText", log.RawResponseText, DbType.String, size: -1);
        parameters.Add("ModelUsed", log.ModelUsed, DbType.String, size: 128);
        parameters.Add("TokensIn", log.TokensIn, DbType.Int32);
        parameters.Add("TokensOut", log.TokensOut, DbType.Int32);
        parameters.Add("CreatedAt", log.CreatedAt);

        await using var connection = OpenConnection();
        await connection.ExecuteAsync(new CommandDefinition(
            "monitoring.usp_AddLlmCallLog", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));
        return log;
    }

    public async Task<LlmCallLog?> GetByDoctorReportIdAsync(Guid doctorReportId, CancellationToken ct = default)
    {
        var parameters = new DynamicParameters();
        parameters.Add("DoctorReportId", doctorReportId);

        await using var connection = OpenConnection();
        return await connection.QuerySingleOrDefaultAsync<LlmCallLog>(new CommandDefinition(
            "monitoring.usp_GetLlmCallLogByDoctorReportId", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));
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
