using System.Data;
using Dapper;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Options;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Infrastructure.Sql;

/// <summary>SQL-backed editable KQL (monitoring.KqlQuery). Same Dapper shape as the other monitoring settings stores.</summary>
public sealed class SqlKqlQueryStore(IOptions<JourneyStoreOptions> storeOptions) : IKqlQueryStore
{
    public async Task<IReadOnlyList<KqlQuerySummary>> GetAllAsync(CancellationToken ct = default)
    {
        await using var connection = OpenConnection();
        var rows = await connection.QueryAsync<SummaryRow>(new CommandDefinition(
            "monitoring.usp_GetAllKqlQueries", commandType: CommandType.StoredProcedure, cancellationToken: ct));
        return rows.Select(r => r.ToDomain()).ToList();
    }

    public async Task<IReadOnlyDictionary<string, string>> GetEnabledTextsAsync(CancellationToken ct = default)
    {
        await using var connection = OpenConnection();
        var rows = await connection.QueryAsync<TextRow>(new CommandDefinition(
            "monitoring.usp_GetEnabledKqlQueryTexts", commandType: CommandType.StoredProcedure, cancellationToken: ct));

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (!string.IsNullOrWhiteSpace(row.KqlText)) map[row.QueryKey] = row.KqlText;
        }
        return map;
    }

    public async Task<KqlQueryEntry?> GetAsync(string queryKey, CancellationToken ct = default)
    {
        var parameters = new DynamicParameters();
        parameters.Add("QueryKey", queryKey, DbType.String, size: 128);

        await using var connection = OpenConnection();
        var row = await connection.QuerySingleOrDefaultAsync<EntryRow>(new CommandDefinition(
            "monitoring.usp_GetKqlQuery", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));
        return row?.ToDomain();
    }

    public async Task<KqlQueryEntry> UpsertAsync(KqlQueryEntry entry, string changedBy, CancellationToken ct = default)
    {
        var parameters = new DynamicParameters();
        parameters.Add("QueryKey", entry.QueryKey, DbType.String, size: 128);
        parameters.Add("KqlText", entry.KqlText, DbType.String, size: -1);
        parameters.Add("Description", entry.Description, DbType.String, size: 500);
        parameters.Add("SourceRef", entry.SourceRef, DbType.String, size: 500);
        parameters.Add("IsBuiltIn", entry.IsBuiltIn, DbType.Boolean);
        parameters.Add("Enabled", entry.Enabled, DbType.Boolean);
        parameters.Add("ChangedBy", changedBy, DbType.String, size: 128);

        await using var connection = OpenConnection();
        var row = await connection.QuerySingleAsync<EntryRow>(new CommandDefinition(
            "monitoring.usp_UpsertKqlQuery", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));
        return row.ToDomain();
    }

    public async Task DeleteAsync(string queryKey, string changedBy, CancellationToken ct = default)
    {
        var parameters = new DynamicParameters();
        parameters.Add("QueryKey", queryKey, DbType.String, size: 128);
        parameters.Add("ChangedBy", changedBy, DbType.String, size: 128);

        await using var connection = OpenConnection();
        await connection.ExecuteAsync(new CommandDefinition(
            "monitoring.usp_DeleteKqlQuery", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<KqlQueryAuditSummary>> GetAuditAsync(string? queryKey, int top, CancellationToken ct = default)
    {
        var parameters = new DynamicParameters();
        parameters.Add("QueryKey", queryKey, DbType.String, size: 128);
        parameters.Add("Top", top);

        await using var connection = OpenConnection();
        var rows = await connection.QueryAsync<AuditSummaryRow>(new CommandDefinition(
            "monitoring.usp_GetKqlQueryAudit", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));
        return rows.Select(r => r.ToDomain()).ToList();
    }

    public async Task<KqlQueryAuditEntry?> GetAuditEntryAsync(long auditId, CancellationToken ct = default)
    {
        var parameters = new DynamicParameters();
        parameters.Add("AuditId", auditId);

        await using var connection = OpenConnection();
        var row = await connection.QuerySingleOrDefaultAsync<AuditEntryRow>(new CommandDefinition(
            "monitoring.usp_GetKqlQueryAuditEntry", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));
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

    private sealed record TextRow(string QueryKey, string? KqlText, int Version);

    private sealed record SummaryRow(
        string QueryKey, int Version, string? Description, string? SourceRef, bool IsBuiltIn,
        bool Enabled, DateTimeOffset UpdatedAt, string UpdatedBy, int TextLength)
    {
        public KqlQuerySummary ToDomain() =>
            new(QueryKey, Version, Description, SourceRef, IsBuiltIn, Enabled, UpdatedAt, UpdatedBy, TextLength);
    }

    private sealed record EntryRow(
        string QueryKey, string KqlText, int Version, string? Description, string? SourceRef,
        bool IsBuiltIn, bool Enabled, DateTimeOffset UpdatedAt, string UpdatedBy)
    {
        public KqlQueryEntry ToDomain() =>
            new(QueryKey, KqlText, Version, Description, SourceRef, IsBuiltIn, Enabled, UpdatedAt, UpdatedBy);
    }

    private sealed record AuditSummaryRow(
        long AuditId, string QueryKey, int Version, string? Description, string? SourceRef,
        bool? Enabled, bool Removed, DateTimeOffset ChangedAt, string ChangedBy, int TextLength)
    {
        public KqlQueryAuditSummary ToDomain() =>
            new(AuditId, QueryKey, Version, Description, SourceRef, Enabled, Removed, ChangedAt, ChangedBy, TextLength);
    }

    private sealed record AuditEntryRow(
        long AuditId, string QueryKey, int Version, string? KqlText, string? Description,
        string? SourceRef, bool? Enabled, bool Removed, DateTimeOffset ChangedAt, string ChangedBy)
    {
        public KqlQueryAuditEntry ToDomain() =>
            new(AuditId, QueryKey, Version, KqlText, Description, SourceRef, Enabled, Removed, ChangedAt, ChangedBy);
    }
}
