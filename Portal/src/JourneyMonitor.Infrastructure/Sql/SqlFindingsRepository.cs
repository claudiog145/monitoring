using System.Data;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Options;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Dapper;

namespace JourneyMonitor.Infrastructure.Sql;

/// <summary>SQL-backed IFindingsRepository (monitoring.Finding) - survives process/container restarts, unlike InMemoryFindingsRepository.</summary>
public sealed class SqlFindingsRepository(IOptions<JourneyStoreOptions> storeOptions) : IFindingsRepository
{
    public async Task<Finding> AddAsync(Finding finding, CancellationToken ct = default)
    {
        var parameters = new DynamicParameters();
        parameters.Add("Id", finding.Id);
        parameters.Add("Domain", finding.Domain, DbType.String, size: 32);
        parameters.Add("FindingType", finding.FindingType, DbType.String, size: 64);
        parameters.Add("Stage", finding.Stage, DbType.String, size: 128);
        parameters.Add("Title", finding.Title, DbType.String, size: 512);
        parameters.Add("Severity", finding.Severity, DbType.String, size: 16);
        parameters.Add("EvidenceJson", finding.EvidenceJson, DbType.String, size: -1);
        parameters.Add("Status", finding.Status, DbType.String, size: 16);
        parameters.Add("OriginMonkeyKey", finding.OriginMonkeyKey, DbType.String, size: 64);
        parameters.Add("CreatedAt", finding.CreatedAt);
        parameters.Add("CorrelationId", finding.CorrelationId, DbType.String, size: 256);

        await using var connection = OpenConnection();
        await connection.ExecuteAsync(new CommandDefinition(
            "monitoring.usp_AddFinding", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));
        return finding;
    }

    public async Task<Finding> UpdateAsync(Finding finding, CancellationToken ct = default)
    {
        var parameters = new DynamicParameters();
        parameters.Add("Id", finding.Id);
        parameters.Add("Title", finding.Title, DbType.String, size: 512);
        parameters.Add("Severity", finding.Severity, DbType.String, size: 16);
        parameters.Add("EvidenceJson", finding.EvidenceJson, DbType.String, size: -1);
        parameters.Add("Status", finding.Status, DbType.String, size: 16);

        await using var connection = OpenConnection();
        await connection.ExecuteAsync(new CommandDefinition(
            "monitoring.usp_UpdateFinding", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));
        return finding;
    }

    public async Task<Finding?> GetOpenByCorrelationIdAsync(string correlationId, CancellationToken ct = default)
    {
        var parameters = new DynamicParameters();
        parameters.Add("CorrelationId", correlationId, DbType.String, size: 256);

        await using var connection = OpenConnection();
        var row = await connection.QuerySingleOrDefaultAsync<FindingRow>(new CommandDefinition(
            "monitoring.usp_GetOpenFindingByCorrelationId", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));
        return row?.ToDomain();
    }

    public async Task<IReadOnlyList<Finding>> GetOpenByDomainAsync(string domain, CancellationToken ct = default)
    {
        var parameters = new DynamicParameters();
        parameters.Add("Domain", domain, DbType.String, size: 32);

        await using var connection = OpenConnection();
        var rows = await connection.QueryAsync<FindingRow>(new CommandDefinition(
            "monitoring.usp_GetOpenFindingsByDomain", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));
        return rows.Select(r => r.ToDomain()).ToList();
    }

    public async Task<IReadOnlyList<Finding>> GetAllAsync(CancellationToken ct = default)
    {
        await using var connection = OpenConnection();
        var rows = await connection.QueryAsync<FindingRow>(new CommandDefinition(
            "monitoring.usp_GetAllFindings", commandType: CommandType.StoredProcedure, cancellationToken: ct));
        return rows.Select(r => r.ToDomain()).ToList();
    }

    public async Task<Finding?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var parameters = new DynamicParameters();
        parameters.Add("Id", id);

        await using var connection = OpenConnection();
        var row = await connection.QuerySingleOrDefaultAsync<FindingRow>(new CommandDefinition(
            "monitoring.usp_GetFindingById", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));
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

    private sealed record FindingRow(
        Guid Id, string Domain, string FindingType, string Stage, string Title, string Severity,
        string EvidenceJson, string Status, string OriginMonkeyKey, DateTimeOffset CreatedAt, string CorrelationId)
    {
        public Finding ToDomain() => new()
        {
            Id = Id,
            Domain = Domain,
            FindingType = FindingType,
            Stage = Stage,
            Title = Title,
            Severity = Severity,
            EvidenceJson = EvidenceJson,
            Status = Status,
            OriginMonkeyKey = OriginMonkeyKey,
            CreatedAt = CreatedAt,
            CorrelationId = CorrelationId,
        };
    }
}
