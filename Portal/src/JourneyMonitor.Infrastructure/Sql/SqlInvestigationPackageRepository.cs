using System.Data;
using Dapper;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Options;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Infrastructure.Sql;

/// <summary>
/// SQL-backed handoff store (monitoring.InvestigationPackage). The insert proc is idempotent on
/// (CaseId, Board, ProfileKey), so a cycle that re-reads the same 4h window does not have to rely
/// on the C# dedup check winning a race against a concurrent cycle - and neither the two scouts
/// nor two companies ever suppress each other's packages for the same group and bucket.
/// </summary>
public sealed class SqlInvestigationPackageRepository(IOptions<JourneyStoreOptions> storeOptions) : IInvestigationPackageRepository
{
    public async Task<InvestigationPackage> AddAsync(InvestigationPackage package, CancellationToken ct = default)
    {
        var parameters = new DynamicParameters();
        parameters.Add("Id", package.Id);
        parameters.Add("CaseId", package.CaseId, DbType.String, size: 512);
        parameters.Add("ProfileKey", package.ProfileKey, DbType.String, size: 128);
        parameters.Add("Board", package.Board, DbType.String, size: 16);
        parameters.Add("Vertical", package.Vertical, DbType.String, size: 64);
        parameters.Add("Stage", package.Stage, DbType.String, size: 128);
        parameters.Add("Source", package.Source, DbType.String, size: 64);
        parameters.Add("EntityKey", package.EntityKey, DbType.String, size: 256);
        parameters.Add("EntityKind", package.EntityKind, DbType.String, size: 16);
        parameters.Add("ClubId", package.ClubId, DbType.String, size: 64);
        parameters.Add("IcePartnerId", package.IcePartnerId, DbType.String, size: 64);
        parameters.Add("BrandName", package.BrandName, DbType.String, size: 200);
        parameters.Add("ClubName", package.ClubName, DbType.String, size: 200);
        parameters.Add("ClubResolvedBy", package.ClubResolvedBy, DbType.String, size: 32);
        parameters.Add("Priority", package.Priority, DbType.String, size: 64);
        parameters.Add("PriorityRank", package.PriorityRank);
        parameters.Add("Reason", package.Reason, DbType.String, size: 512);
        parameters.Add("WindowFrom", package.WindowFrom);
        parameters.Add("WindowTo", package.WindowTo);
        parameters.Add("DeterministicJson", package.DeterministicJson, DbType.String, size: -1);
        parameters.Add("AssessmentJson", package.AssessmentJson, DbType.String, size: -1);
        parameters.Add("DrillDownKql", package.DrillDownKql, DbType.String, size: -1);
        parameters.Add("RawEvidenceJson", package.RawEvidenceJson, DbType.String, size: -1);
        parameters.Add("RequestCount", package.RequestCount);
        parameters.Add("FailedRequestCount", package.FailedRequestCount);
        parameters.Add("ExceptionCount", package.ExceptionCount);
        parameters.Add("AffectedUserCount", package.AffectedUserCount);
        parameters.Add("Status", package.Status, DbType.String, size: 32);
        parameters.Add("ScoutRunId", package.ScoutRunId);
        parameters.Add("CreatedAt", package.CreatedAt);

        await using var connection = OpenConnection();
        await connection.ExecuteAsync(new CommandDefinition(
            "monitoring.usp_AddInvestigationPackage", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));
        return package;
    }

    public async Task<bool> ExistsByCaseIdAsync(string caseId, string board, string profileKey, CancellationToken ct = default)
    {
        var parameters = new DynamicParameters();
        parameters.Add("CaseId", caseId, DbType.String, size: 512);
        parameters.Add("Board", board, DbType.String, size: 16);
        parameters.Add("ProfileKey", profileKey, DbType.String, size: 128);

        await using var connection = OpenConnection();
        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "monitoring.usp_InvestigationPackageExists", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));
    }

    public async Task<InvestigationPackage?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var parameters = new DynamicParameters();
        parameters.Add("Id", id);

        await using var connection = OpenConnection();
        var row = await connection.QuerySingleOrDefaultAsync<PackageRow>(new CommandDefinition(
            "monitoring.usp_GetInvestigationPackageById", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));
        return row?.ToDomain();
    }

    public async Task<IReadOnlyList<InvestigationPackage>> GetRecentAsync(int top, string? board = null, string? profileKey = null, CancellationToken ct = default)
    {
        var parameters = new DynamicParameters();
        parameters.Add("Top", top);
        parameters.Add("Board", board, DbType.String, size: 16);
        // Blank collapses to NULL - the proc reads NULL as "every company". Sending N'' instead
        // would match the rows written before ProfileKey existed and nothing else.
        parameters.Add("ProfileKey", NullIfBlank(profileKey), DbType.String, size: 128);

        await using var connection = OpenConnection();
        var rows = await connection.QueryAsync<PackageRow>(new CommandDefinition(
            "monitoring.usp_GetRecentInvestigationPackages", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));
        return rows.Select(r => r.ToDomain()).ToList();
    }

    public async Task<PackageRecurrence> GetRecurrenceAsync(string groupKey, CancellationToken ct = default)
    {
        var parameters = new DynamicParameters();
        parameters.Add("GroupKey", groupKey);

        await using var connection = OpenConnection();
        var row = await connection.QuerySingleOrDefaultAsync<RecurrenceRow>(new CommandDefinition(
            "monitoring.usp_GetInvestigationPackageRecurrence", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));
        return row is null ? PackageRecurrence.None : new PackageRecurrence(row.Occurrences, row.FirstSeen, row.LastSeen);
    }

    private sealed record RecurrenceRow(int Occurrences, DateTimeOffset? FirstSeen, DateTimeOffset? LastSeen);

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

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
    /// Positional record, so Dapper materialises it through the constructor: it matches the
    /// selected columns to the constructor parameters BY POSITION AND NAME and requires the counts
    /// to be equal. Every column added here must therefore be added to
    /// usp_GetInvestigationPackageById and usp_GetRecentInvestigationPackages at the same index -
    /// a mismatch compiles clean and throws only when a query runs.
    /// </summary>
    private sealed record PackageRow(
        Guid Id, string CaseId, string ProfileKey, string Board, string Vertical, string Stage, string Source,
        string EntityKey, string EntityKind, string? ClubId, string? IcePartnerId, string? BrandName,
        string? ClubName, string ClubResolvedBy, string Priority, int PriorityRank, string Reason,
        DateTimeOffset WindowFrom, DateTimeOffset WindowTo, string DeterministicJson, string AssessmentJson,
        string? DrillDownKql, string? RawEvidenceJson,
        int RequestCount, int FailedRequestCount, int ExceptionCount, int AffectedUserCount,
        string Status, Guid ScoutRunId, DateTimeOffset CreatedAt)
    {
        public InvestigationPackage ToDomain() => new()
        {
            Id = Id,
            CaseId = CaseId,
            ProfileKey = ProfileKey,
            Board = Board,
            Vertical = Vertical,
            Stage = Stage,
            Source = Source,
            EntityKey = EntityKey,
            EntityKind = EntityKind,
            ClubId = ClubId,
            IcePartnerId = IcePartnerId,
            BrandName = BrandName,
            ClubName = ClubName,
            ClubResolvedBy = ClubResolvedBy,
            Priority = Priority,
            PriorityRank = PriorityRank,
            Reason = Reason,
            WindowFrom = WindowFrom,
            WindowTo = WindowTo,
            DeterministicJson = DeterministicJson,
            AssessmentJson = AssessmentJson,
            DrillDownKql = DrillDownKql,
            RawEvidenceJson = RawEvidenceJson,
            RequestCount = RequestCount,
            FailedRequestCount = FailedRequestCount,
            ExceptionCount = ExceptionCount,
            AffectedUserCount = AffectedUserCount,
            Status = Status,
            ScoutRunId = ScoutRunId,
            CreatedAt = CreatedAt,
        };
    }
}
