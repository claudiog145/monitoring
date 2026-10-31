using System.Data;
using Dapper;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Options;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Infrastructure.Sql;

/// <summary>SQL-backed service ownership map (monitoring.ServiceOwner). Same Dapper shape as the other monitoring settings stores.</summary>
public sealed class SqlServiceOwnerStore(IOptions<JourneyStoreOptions> storeOptions) : IServiceOwnerStore
{
    public async Task<IReadOnlyList<ServiceOwner>> GetAllAsync(CancellationToken ct = default)
    {
        await using var connection = OpenConnection();
        var rows = await connection.QueryAsync<OwnerRow>(new CommandDefinition(
            "monitoring.usp_GetAllServiceOwners", commandType: CommandType.StoredProcedure, cancellationToken: ct));
        return rows.Select(r => r.ToDomain()).ToList();
    }

    public async Task<ServiceOwner> UpsertAsync(ServiceOwner owner, string changedBy, CancellationToken ct = default)
    {
        var parameters = new DynamicParameters();
        parameters.Add("Vertical", owner.Vertical, DbType.String, size: 64);
        parameters.Add("Stage", owner.Stage, DbType.String, size: 128);
        parameters.Add("OwnerTeam", owner.OwnerTeam, DbType.String, size: 200);
        parameters.Add("PrimaryResponder", owner.PrimaryResponder, DbType.String, size: 200);
        parameters.Add("Backup", owner.Backup, DbType.String, size: 200);
        parameters.Add("ContactRef", owner.ContactRef, DbType.String, size: 400);
        parameters.Add("Notes", owner.Notes, DbType.String, size: 1000);
        parameters.Add("ChangedBy", changedBy, DbType.String, size: 128);

        await using var connection = OpenConnection();
        var row = await connection.QuerySingleAsync<OwnerRow>(new CommandDefinition(
            "monitoring.usp_UpsertServiceOwner", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));
        return row.ToDomain();
    }

    public async Task DeleteAsync(string vertical, string stage, string changedBy, CancellationToken ct = default)
    {
        var parameters = new DynamicParameters();
        parameters.Add("Vertical", vertical, DbType.String, size: 64);
        parameters.Add("Stage", stage, DbType.String, size: 128);
        parameters.Add("ChangedBy", changedBy, DbType.String, size: 128);

        await using var connection = OpenConnection();
        await connection.ExecuteAsync(new CommandDefinition(
            "monitoring.usp_DeleteServiceOwner", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<ServiceOwnerAuditEntry>> GetAuditAsync(int top, CancellationToken ct = default)
    {
        var parameters = new DynamicParameters();
        parameters.Add("Top", top);

        await using var connection = OpenConnection();
        var rows = await connection.QueryAsync<AuditRow>(new CommandDefinition(
            "monitoring.usp_GetServiceOwnerAudit", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));
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

    // Mapped through intermediate rows rather than straight onto the records: Dapper needs a
    // constructor matching every selected column, and a positional record breaks at runtime the
    // moment a column is added to the proc.
    private sealed record OwnerRow(
        string Vertical, string Stage, string OwnerTeam, string? PrimaryResponder, string? Backup,
        string? ContactRef, string? Notes, DateTimeOffset UpdatedAt, string UpdatedBy)
    {
        public ServiceOwner ToDomain() =>
            new(Vertical, Stage, OwnerTeam, PrimaryResponder, Backup, ContactRef, Notes, UpdatedAt, UpdatedBy);
    }

    private sealed record AuditRow(
        long AuditId, string Vertical, string Stage, string? OwnerTeam, string? PrimaryResponder,
        string? Backup, string? ContactRef, bool Removed, DateTimeOffset ChangedAt, string ChangedBy)
    {
        public ServiceOwnerAuditEntry ToDomain() =>
            new(AuditId, Vertical, Stage, OwnerTeam, PrimaryResponder, Backup, ContactRef, Removed, ChangedAt, ChangedBy);
    }
}
