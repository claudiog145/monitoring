using System.Data;
using Dapper;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Options;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Infrastructure.Sql;

/// <summary>SQL-backed audit trail of the Scout's LLM calls (monitoring.ScoutLlmCallLog), idempotent per package.</summary>
public sealed class SqlScoutLlmCallLogRepository(IOptions<JourneyStoreOptions> storeOptions) : IScoutLlmCallLogRepository
{
    public async Task<ScoutLlmCall> AddAsync(ScoutLlmCall call, CancellationToken ct = default)
    {
        var parameters = new DynamicParameters();
        parameters.Add("Id", call.Id);
        parameters.Add("InvestigationPackageId", call.InvestigationPackageId);
        parameters.Add("PromptText", call.PromptText, DbType.String, size: -1);
        parameters.Add("RawResponseText", call.RawResponseText, DbType.String, size: -1);
        parameters.Add("ModelUsed", call.ModelUsed, DbType.String, size: 128);
        parameters.Add("TokensIn", call.TokensIn);
        parameters.Add("TokensOut", call.TokensOut);
        parameters.Add("DurationMs", call.DurationMs);
        parameters.Add("ErrorMessage", call.ErrorMessage, DbType.String, size: 1024);
        parameters.Add("CalledAt", call.CalledAt);

        await using var connection = OpenConnection();
        await connection.ExecuteAsync(new CommandDefinition(
            "monitoring.usp_AddScoutLlmCallLog", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));
        return call;
    }

    public async Task<ScoutLlmCall?> GetByPackageIdAsync(Guid investigationPackageId, CancellationToken ct = default)
    {
        var parameters = new DynamicParameters();
        parameters.Add("InvestigationPackageId", investigationPackageId);

        await using var connection = OpenConnection();
        var row = await connection.QuerySingleOrDefaultAsync<CallRow>(new CommandDefinition(
            "monitoring.usp_GetScoutLlmCallLogByPackageId", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));
        return row?.ToDomain();
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

    private sealed record CallRow(
        Guid Id, Guid InvestigationPackageId, string PromptText, string RawResponseText, string ModelUsed,
        int TokensIn, int TokensOut, long DurationMs, string? ErrorMessage, DateTimeOffset CalledAt)
    {
        public ScoutLlmCall ToDomain() => new()
        {
            Id = Id,
            InvestigationPackageId = InvestigationPackageId,
            PromptText = PromptText,
            RawResponseText = RawResponseText,
            ModelUsed = ModelUsed,
            TokensIn = TokensIn,
            TokensOut = TokensOut,
            DurationMs = DurationMs,
            ErrorMessage = ErrorMessage,
            CalledAt = CalledAt,
        };
    }
}
