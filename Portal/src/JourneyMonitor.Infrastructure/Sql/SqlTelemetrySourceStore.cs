using System.Data;
using Dapper;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Options;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Infrastructure.Sql;

/// <summary>SQL-backed telemetry source catalog (monitoring.TelemetrySource). Same Dapper shape as the other monitoring settings stores.</summary>
public sealed class SqlTelemetrySourceStore(IOptions<JourneyStoreOptions> storeOptions) : ITelemetrySourceStore
{
    public async Task<IReadOnlyList<TelemetrySourceEntry>> GetAllAsync(CancellationToken ct = default)
    {
        await using var connection = OpenConnection();
        var rows = await connection.QueryAsync<SourceRow>(new CommandDefinition(
            "monitoring.usp_GetAllTelemetrySources", commandType: CommandType.StoredProcedure, cancellationToken: ct));
        return rows.Select(r => r.ToDomain()).ToList();
    }

    public async Task<TelemetrySourceEntry> UpsertAsync(TelemetrySourceEntry source, string changedBy, CancellationToken ct = default)
    {
        var parameters = new DynamicParameters();
        parameters.Add("Alias", source.Alias, DbType.String, size: 64);
        parameters.Add("Environment", source.Environment, DbType.String, size: 32);
        parameters.Add("Kind", source.Kind, DbType.String, size: 32);
        parameters.Add("ResourceId", source.ResourceId, DbType.String, size: 512);
        parameters.Add("AppId", source.AppId, DbType.String, size: 64);
        parameters.Add("TenantId", source.TenantId, DbType.String, size: 64);
        parameters.Add("Enabled", source.Enabled, DbType.Boolean);
        parameters.Add("Notes", source.Notes, DbType.String, size: 1000);
        parameters.Add("ChangedBy", changedBy, DbType.String, size: 128);

        await using var connection = OpenConnection();
        var row = await connection.QuerySingleAsync<SourceRow>(new CommandDefinition(
            "monitoring.usp_UpsertTelemetrySource", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));
        return row.ToDomain();
    }

    public async Task DeleteAsync(string alias, string environment, string changedBy, CancellationToken ct = default)
    {
        var parameters = new DynamicParameters();
        parameters.Add("Alias", alias, DbType.String, size: 64);
        parameters.Add("Environment", environment, DbType.String, size: 32);
        parameters.Add("ChangedBy", changedBy, DbType.String, size: 128);

        await using var connection = OpenConnection();
        await connection.ExecuteAsync(new CommandDefinition(
            "monitoring.usp_DeleteTelemetrySource", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<TelemetrySourceAuditEntry>> GetAuditAsync(int top, CancellationToken ct = default)
    {
        var parameters = new DynamicParameters();
        parameters.Add("Top", top);

        await using var connection = OpenConnection();
        var rows = await connection.QueryAsync<AuditRow>(new CommandDefinition(
            "monitoring.usp_GetTelemetrySourceAudit", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));
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

    private sealed record SourceRow(
        string Alias, string Environment, string Kind, string ResourceId, string? AppId, string? TenantId,
        bool Enabled, string? Notes, DateTimeOffset UpdatedAt, string UpdatedBy)
    {
        public TelemetrySourceEntry ToDomain() =>
            new(Alias, Environment, Kind, ResourceId, AppId, TenantId, Enabled, Notes, UpdatedAt, UpdatedBy);
    }

    private sealed record AuditRow(
        long AuditId, string Alias, string Environment, string? Kind, string? ResourceId, string? AppId,
        string? TenantId, bool? Enabled, string? Notes, bool Removed, DateTimeOffset ChangedAt, string ChangedBy)
    {
        public TelemetrySourceAuditEntry ToDomain() =>
            new(AuditId, Alias, Environment, Kind, ResourceId, AppId, TenantId, Enabled, Notes, Removed, ChangedAt, ChangedBy);
    }
}
