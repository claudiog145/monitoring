using System.Data;
using Dapper;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Options;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Infrastructure.Sql;

/// <summary>
/// SQL-backed investigation transcript (monitoring.AgentTurn / monitoring.AgentToolCall). Tool calls
/// hang off a turn, so reading them for a case is a join the proc does - the caller correlates on
/// AgentTurnId.
/// </summary>
public sealed class SqlAgentTranscriptRepository(IOptions<JourneyStoreOptions> storeOptions) : IAgentTranscriptRepository
{
    public async Task<AgentTurn> AddTurnAsync(AgentTurn turn, CancellationToken ct = default)
    {
        var parameters = new DynamicParameters();
        parameters.Add("Id", turn.Id);
        parameters.Add("ForensicCaseId", turn.ForensicCaseId);
        parameters.Add("AgentRole", turn.AgentRole, DbType.String, size: 128);
        parameters.Add("Kind", turn.Kind, DbType.String, size: 32);
        parameters.Add("Round", turn.Round);
        parameters.Add("Hop", turn.Hop);
        parameters.Add("ModelUsed", turn.ModelUsed, DbType.String, size: 128);
        parameters.Add("InputContext", turn.InputContext, DbType.String, size: -1);
        parameters.Add("PromptText", turn.PromptText, DbType.String, size: -1);
        parameters.Add("RawResponseText", turn.RawResponseText, DbType.String, size: -1);
        parameters.Add("OutcomeStatus", turn.OutcomeStatus, DbType.String, size: 32);
        parameters.Add("OutcomeJson", turn.OutcomeJson, DbType.String, size: -1);
        parameters.Add("TokensIn", turn.TokensIn);
        parameters.Add("TokensOut", turn.TokensOut);
        parameters.Add("DurationMs", turn.DurationMs);
        parameters.Add("ErrorMessage", turn.ErrorMessage, DbType.String, size: 1024);
        parameters.Add("CreatedAt", turn.CreatedAt);

        await using var connection = OpenConnection();
        await connection.ExecuteAsync(new CommandDefinition(
            "monitoring.usp_AddAgentTurn", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));
        return turn;
    }

    public async Task AddToolCallAsync(AgentToolCall toolCall, CancellationToken ct = default)
    {
        var parameters = new DynamicParameters();
        parameters.Add("Id", toolCall.Id);
        parameters.Add("AgentTurnId", toolCall.AgentTurnId);
        parameters.Add("ToolName", toolCall.ToolName, DbType.String, size: 128);
        parameters.Add("ArgumentsJson", toolCall.ArgumentsJson, DbType.String, size: -1);
        parameters.Add("ResultJson", toolCall.ResultJson, DbType.String, size: -1);
        parameters.Add("Truncated", toolCall.Truncated);
        parameters.Add("Success", toolCall.Success);
        parameters.Add("ErrorMessage", toolCall.ErrorMessage, DbType.String, size: 1024);
        parameters.Add("ResultRowCount", toolCall.ResultRowCount);
        parameters.Add("DurationMs", toolCall.DurationMs);
        parameters.Add("CreatedAt", toolCall.CreatedAt);

        await using var connection = OpenConnection();
        await connection.ExecuteAsync(new CommandDefinition(
            "monitoring.usp_AddAgentToolCall", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<AgentTurn>> GetTurnsAsync(Guid forensicCaseId, CancellationToken ct = default)
    {
        var parameters = new DynamicParameters();
        parameters.Add("ForensicCaseId", forensicCaseId);

        await using var connection = OpenConnection();
        var rows = await connection.QueryAsync<AgentTurnRow>(new CommandDefinition(
            "monitoring.usp_GetAgentTurns", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));
        return rows.Select(r => r.ToDomain()).ToList();
    }

    public async Task<IReadOnlyList<AgentToolCall>> GetToolCallsAsync(Guid forensicCaseId, CancellationToken ct = default)
    {
        var parameters = new DynamicParameters();
        parameters.Add("ForensicCaseId", forensicCaseId);

        await using var connection = OpenConnection();
        var rows = await connection.QueryAsync<AgentToolCallRow>(new CommandDefinition(
            "monitoring.usp_GetAgentToolCalls", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));
        return rows.Select(r => r.ToDomain()).ToList();
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

    private sealed record AgentTurnRow(
        Guid Id, Guid ForensicCaseId, string AgentRole, string Kind, int Round, int Hop,
        string? ModelUsed, string? InputContext, string PromptText, string RawResponseText,
        string OutcomeStatus, string? OutcomeJson, int TokensIn, int TokensOut, long DurationMs,
        string? ErrorMessage, DateTimeOffset CreatedAt)
    {
        public AgentTurn ToDomain() => new()
        {
            Id = Id,
            ForensicCaseId = ForensicCaseId,
            AgentRole = AgentRole,
            Kind = Kind,
            Round = Round,
            Hop = Hop,
            ModelUsed = ModelUsed,
            InputContext = InputContext,
            PromptText = PromptText,
            RawResponseText = RawResponseText,
            OutcomeStatus = OutcomeStatus,
            OutcomeJson = OutcomeJson,
            TokensIn = TokensIn,
            TokensOut = TokensOut,
            DurationMs = DurationMs,
            ErrorMessage = ErrorMessage,
            CreatedAt = CreatedAt,
        };
    }

    private sealed record AgentToolCallRow(
        Guid Id, Guid AgentTurnId, string ToolName, string ArgumentsJson, string ResultJson,
        bool Truncated, bool Success, string? ErrorMessage, int ResultRowCount, long DurationMs,
        DateTimeOffset CreatedAt)
    {
        public AgentToolCall ToDomain() => new()
        {
            Id = Id,
            AgentTurnId = AgentTurnId,
            ToolName = ToolName,
            ArgumentsJson = ArgumentsJson,
            ResultJson = ResultJson,
            Truncated = Truncated,
            Success = Success,
            ErrorMessage = ErrorMessage,
            ResultRowCount = ResultRowCount,
            DurationMs = DurationMs,
            CreatedAt = CreatedAt,
        };
    }
}
