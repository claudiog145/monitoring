using System.Data;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Options;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Dapper;

namespace JourneyMonitor.Infrastructure.Sql;

/// <summary>SQL-backed IDoctorReportsRepository (monitoring.DoctorReport) - survives process/container restarts, unlike InMemoryDoctorReportsRepository.</summary>
public sealed class SqlDoctorReportsRepository(IOptions<JourneyStoreOptions> storeOptions) : IDoctorReportsRepository
{
    public async Task<DoctorReport> AddAsync(DoctorReport report, CancellationToken ct = default)
    {
        var parameters = new DynamicParameters();
        parameters.Add("Id", report.Id);
        parameters.Add("FindingId", report.FindingId);
        parameters.Add("Verdict", report.Verdict, DbType.String, size: 1024);
        parameters.Add("ErrorCategory", report.ErrorCategory, DbType.String, size: 64);
        parameters.Add("RootCauseHypothesis", report.RootCauseHypothesis, DbType.String, size: -1);
        parameters.Add("Confidence", report.Confidence, DbType.Double);
        parameters.Add("CitationsJson", report.CitationsJson, DbType.String, size: -1);
        parameters.Add("ProposedFixJson", report.ProposedFixJson, DbType.String, size: -1);
        parameters.Add("ModelUsed", report.ModelUsed, DbType.String, size: 128);
        parameters.Add("TokensIn", report.TokensIn, DbType.Int32);
        parameters.Add("TokensOut", report.TokensOut, DbType.Int32);
        parameters.Add("GeneratedAt", report.GeneratedAt);

        await using var connection = OpenConnection();
        await connection.ExecuteAsync(new CommandDefinition(
            "monitoring.usp_AddDoctorReport", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));
        return report;
    }

    public async Task<DoctorReport?> GetByFindingIdAsync(Guid findingId, CancellationToken ct = default)
    {
        var parameters = new DynamicParameters();
        parameters.Add("FindingId", findingId);

        await using var connection = OpenConnection();
        var row = await connection.QuerySingleOrDefaultAsync<DoctorReportRow>(new CommandDefinition(
            "monitoring.usp_GetDoctorReportByFindingId", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));
        return row?.ToDomain();
    }

    public async Task<IReadOnlyList<DoctorReport>> GetAllAsync(CancellationToken ct = default)
    {
        await using var connection = OpenConnection();
        var rows = await connection.QueryAsync<DoctorReportRow>(new CommandDefinition(
            "monitoring.usp_GetAllDoctorReports", commandType: CommandType.StoredProcedure, cancellationToken: ct));
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

    private sealed record DoctorReportRow(
        Guid Id, Guid FindingId, string Verdict, string ErrorCategory, string? RootCauseHypothesis,
        double Confidence, string? CitationsJson, string? ProposedFixJson, string ModelUsed,
        int TokensIn, int TokensOut, DateTimeOffset GeneratedAt, string? HumanFeedback)
    {
        public DoctorReport ToDomain() => new()
        {
            Id = Id,
            FindingId = FindingId,
            Verdict = Verdict,
            ErrorCategory = ErrorCategory,
            RootCauseHypothesis = RootCauseHypothesis,
            Confidence = Confidence,
            CitationsJson = CitationsJson,
            ProposedFixJson = ProposedFixJson,
            ModelUsed = ModelUsed,
            TokensIn = TokensIn,
            TokensOut = TokensOut,
            GeneratedAt = GeneratedAt,
            HumanFeedback = HumanFeedback,
        };
    }
}
