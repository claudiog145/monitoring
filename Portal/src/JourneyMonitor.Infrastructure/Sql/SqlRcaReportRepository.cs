using System.Data;
using System.Text.Json;
using Dapper;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Options;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Infrastructure.Sql;

/// <summary>SQL-backed RCA store (monitoring.RcaReport). Structured parts travel as JSON - see the schema comment.</summary>
public sealed class SqlRcaReportRepository(IOptions<JourneyStoreOptions> storeOptions) : IRcaReportRepository
{
    /// <summary>Repo-wide rule: anything serialized to or read back by property name uses web defaults.</summary>
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    public async Task<RcaReport> SaveAsync(RcaReport report, CancellationToken ct = default)
    {
        var parameters = new DynamicParameters();
        parameters.Add("Id", report.Id);
        parameters.Add("ForensicCaseId", report.ForensicCaseId);
        parameters.Add("Title", report.Title, DbType.String, size: 512);
        parameters.Add("Domain", report.Domain, DbType.String, size: 64);
        parameters.Add("Stage", report.Stage, DbType.String, size: 128);
        parameters.Add("SectionsJson", JsonSerializer.Serialize(report.Sections, WebJson), DbType.String, size: -1);
        parameters.Add("FiveWhysJson", JsonSerializer.Serialize(report.FiveWhys, WebJson), DbType.String, size: -1);
        parameters.Add("PrecedentsJson", JsonSerializer.Serialize(report.Precedents, WebJson), DbType.String, size: -1);
        parameters.Add("RequiresHumanJson", JsonSerializer.Serialize(report.RequiresHuman, WebJson), DbType.String, size: -1);
        parameters.Add("LlmCalls", report.LlmCalls);
        parameters.Add("TokensIn", report.TokensIn);
        parameters.Add("TokensOut", report.TokensOut);
        parameters.Add("ModelUsed", report.ModelUsed, DbType.String, size: 128);
        parameters.Add("ErrorMessage", report.Error, DbType.String, size: -1);
        parameters.Add("GeneratedAt", report.GeneratedAt);

        await using var connection = OpenConnection();
        await connection.ExecuteAsync(new CommandDefinition(
            "monitoring.usp_SaveRcaReport", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));

        return report;
    }

    public async Task<RcaReport?> GetByForensicCaseIdAsync(Guid forensicCaseId, CancellationToken ct = default)
    {
        await using var connection = OpenConnection();
        var row = await connection.QuerySingleOrDefaultAsync<RcaReportRow>(new CommandDefinition(
            "monitoring.usp_GetRcaReportByForensicCaseId",
            new { ForensicCaseId = forensicCaseId },
            commandType: CommandType.StoredProcedure,
            cancellationToken: ct));

        return row?.ToDomain();
    }

    public async Task<IReadOnlyList<RcaReport>> GetRecentAsync(int top, CancellationToken ct = default)
    {
        await using var connection = OpenConnection();
        var rows = await connection.QueryAsync<RcaReportRow>(new CommandDefinition(
            "monitoring.usp_GetRecentRcaReports",
            new { Top = top },
            commandType: CommandType.StoredProcedure,
            cancellationToken: ct));

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

    /// <summary>
    /// Intermediate row rather than materialising RcaReport directly: Dapper needs a constructor
    /// matching every selected column, and this type has already bitten this repo three times.
    /// </summary>
    private sealed record RcaReportRow(
        Guid Id,
        Guid ForensicCaseId,
        string Title,
        string? Domain,
        string? Stage,
        string SectionsJson,
        string FiveWhysJson,
        string PrecedentsJson,
        string RequiresHumanJson,
        int LlmCalls,
        int TokensIn,
        int TokensOut,
        string ModelUsed,
        string? ErrorMessage,
        DateTimeOffset GeneratedAt)
    {
        public RcaReport ToDomain() => new()
        {
            Id = Id,
            ForensicCaseId = ForensicCaseId,
            Title = Title,
            Domain = Domain,
            Stage = Stage,
            Sections = Deserialize<RcaSection>(SectionsJson),
            FiveWhys = Deserialize<RcaWhy>(FiveWhysJson),
            Precedents = Deserialize<RcaPrecedent>(PrecedentsJson),
            RequiresHuman = Deserialize<string>(RequiresHumanJson),
            LlmCalls = LlmCalls,
            TokensIn = TokensIn,
            TokensOut = TokensOut,
            ModelUsed = ModelUsed,
            Error = ErrorMessage,
            GeneratedAt = GeneratedAt,
        };

        /// <summary>Malformed stored JSON degrades to empty rather than breaking the whole read.</summary>
        private static IReadOnlyList<T> Deserialize<T>(string json)
        {
            try
            {
                return JsonSerializer.Deserialize<List<T>>(json, WebJson) ?? [];
            }
            catch (JsonException)
            {
                return [];
            }
        }
    }
}
