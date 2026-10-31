using System.Data;
using Dapper;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Options;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Infrastructure.Sql;

/// <summary>
/// SQL-backed Inspector case store (monitoring.ForensicCase). The insert is deliberately not
/// idempotent: UQ_monitoring_ForensicCase_PackageId rejecting a second case for the same package is
/// the guarantee that one package never produces two verdicts.
/// </summary>
public sealed class SqlForensicCaseRepository(IOptions<JourneyStoreOptions> storeOptions) : IForensicCaseRepository
{
    public async Task<ForensicCase> AddAsync(ForensicCase forensicCase, CancellationToken ct = default)
    {
        var parameters = new DynamicParameters();
        parameters.Add("Id", forensicCase.Id);
        parameters.Add("InvestigationPackageId", forensicCase.InvestigationPackageId);
        parameters.Add("ProfileKey", forensicCase.ProfileKey, DbType.String, size: 128);
        parameters.Add("CaseId", forensicCase.CaseId, DbType.String, size: 512);
        parameters.Add("EntityKey", forensicCase.EntityKey, DbType.String, size: 256);
        parameters.Add("BrandName", forensicCase.BrandName, DbType.String, size: 200);
        parameters.Add("ClubName", forensicCase.ClubName, DbType.String, size: 200);
        parameters.Add("Vertical", forensicCase.Vertical, DbType.String, size: 64);
        parameters.Add("Stage", forensicCase.Stage, DbType.String, size: 128);
        parameters.Add("ClubId", forensicCase.ClubId, DbType.String, size: 64);
        parameters.Add("IcePartnerId", forensicCase.IcePartnerId, DbType.String, size: 64);
        parameters.Add("ClubResolvedBy", forensicCase.ClubResolvedBy, DbType.String, size: 32);
        parameters.Add("StartedAt", forensicCase.StartedAt);
        AddMutableFields(parameters, forensicCase);

        await using var connection = OpenConnection();
        await connection.ExecuteAsync(new CommandDefinition(
            "monitoring.usp_AddForensicCase", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));
        return forensicCase;
    }

    public async Task<ForensicCase> UpdateAsync(ForensicCase forensicCase, CancellationToken ct = default)
    {
        var parameters = new DynamicParameters();
        parameters.Add("Id", forensicCase.Id);
        AddMutableFields(parameters, forensicCase);

        await using var connection = OpenConnection();
        await connection.ExecuteAsync(new CommandDefinition(
            "monitoring.usp_UpdateForensicCase", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));
        return forensicCase;
    }

    public async Task UpdatePhaseAsync(Guid caseId, string phase, CancellationToken ct = default)
    {
        var parameters = new DynamicParameters();
        parameters.Add("Id", caseId);
        parameters.Add("Phase", phase, DbType.String, size: 32);

        await using var connection = OpenConnection();
        await connection.ExecuteAsync(new CommandDefinition(
            "monitoring.usp_UpdateForensicCasePhase", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));
    }

    public async Task<ForensicCase?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var parameters = new DynamicParameters();
        parameters.Add("Id", id);

        await using var connection = OpenConnection();
        var row = await connection.QuerySingleOrDefaultAsync<ForensicCaseRow>(new CommandDefinition(
            "monitoring.usp_GetForensicCaseById", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));
        return row?.ToDomain();
    }

    public async Task<ForensicCase?> GetByPackageIdAsync(Guid investigationPackageId, CancellationToken ct = default)
    {
        var parameters = new DynamicParameters();
        parameters.Add("InvestigationPackageId", investigationPackageId);

        await using var connection = OpenConnection();
        var row = await connection.QuerySingleOrDefaultAsync<ForensicCaseRow>(new CommandDefinition(
            "monitoring.usp_GetForensicCaseByPackageId", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));
        return row?.ToDomain();
    }

    public async Task<IReadOnlyList<ForensicCase>> GetRecentAsync(
        int top, string? vertical = null, string? clubId = null, string? profileKey = null, CancellationToken ct = default)
    {
        var parameters = new DynamicParameters();
        parameters.Add("Top", top);
        // Blank and null are the same "no filter" here; an empty string would match nothing.
        parameters.Add("Vertical", string.IsNullOrWhiteSpace(vertical) ? null : vertical.Trim(), DbType.String, size: 64);
        parameters.Add("ClubId", string.IsNullOrWhiteSpace(clubId) ? null : clubId.Trim(), DbType.String, size: 64);
        parameters.Add("ProfileKey", string.IsNullOrWhiteSpace(profileKey) ? null : profileKey.Trim(), DbType.String, size: 128);

        await using var connection = OpenConnection();
        var rows = await connection.QueryAsync<ForensicCaseRow>(new CommandDefinition(
            "monitoring.usp_GetRecentForensicCases", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));
        return rows.Select(r => r.ToDomain()).ToList();
    }

    public async Task<InspectorStats> GetStatsAsync(string? profileKey = null, CancellationToken ct = default)
    {
        var parameters = new DynamicParameters();
        parameters.Add("ProfileKey", string.IsNullOrWhiteSpace(profileKey) ? null : profileKey.Trim(), DbType.String, size: 128);

        await using var connection = OpenConnection();
        var row = await connection.QuerySingleOrDefaultAsync<InspectorStats>(new CommandDefinition(
            "monitoring.usp_GetInspectorStats", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));
        return row ?? InspectorStats.Empty;
    }

    /// <summary>The fields both procs share - everything an investigation can still change after it opened.</summary>
    private static void AddMutableFields(DynamicParameters parameters, ForensicCase forensicCase)
    {
        parameters.Add("Status", forensicCase.Status, DbType.String, size: 32);
        parameters.Add("Phase", forensicCase.Phase, DbType.String, size: 32);
        parameters.Add("CompletedAt", forensicCase.CompletedAt);
        parameters.Add("DurationMs", forensicCase.DurationMs);
        parameters.Add("Verdict", forensicCase.Verdict, DbType.String, size: 1024);
        parameters.Add("Decision", forensicCase.Decision, DbType.String, size: 32);
        parameters.Add("ProbableRootCause", forensicCase.ProbableRootCause, DbType.String, size: -1);
        parameters.Add("Confidence", forensicCase.Confidence);
        parameters.Add("BlockedBy", forensicCase.BlockedBy, DbType.String, size: 1024);
        parameters.Add("SynthesisJson", forensicCase.SynthesisJson, DbType.String, size: -1);
        parameters.Add("BlackboardJson", forensicCase.BlackboardJson, DbType.String, size: -1);
        parameters.Add("RunbookJson", forensicCase.RunbookJson, DbType.String, size: -1);
        parameters.Add("GroundingJson", forensicCase.GroundingJson, DbType.String, size: -1);
        parameters.Add("CitationsTotal", forensicCase.CitationsTotal);
        parameters.Add("CitationsVerified", forensicCase.CitationsVerified);
        parameters.Add("LlmCalls", forensicCase.LlmCalls);
        parameters.Add("TokensIn", forensicCase.TokensIn);
        parameters.Add("TokensOut", forensicCase.TokensOut);
        parameters.Add("AlertLevel", forensicCase.AlertLevel, DbType.String, size: 32);
        parameters.Add("AlertReason", forensicCase.AlertReason, DbType.String, size: 1024);
        parameters.Add("OwnerTeam", forensicCase.OwnerTeam, DbType.String, size: 200);
        parameters.Add("OwnerPrimary", forensicCase.OwnerPrimary, DbType.String, size: 200);
        parameters.Add("OwnerBackup", forensicCase.OwnerBackup, DbType.String, size: 200);
        parameters.Add("OwnerResolvedBy", forensicCase.OwnerResolvedBy, DbType.String, size: 32);
        parameters.Add("ErrorMessage", forensicCase.ErrorMessage, DbType.String, size: 1024);
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

    // Positional record: Dapper matches it against the three case SELECTs by position, name AND
    // exact count, so a column added to one of them has to be added here in the same position.
    private sealed record ForensicCaseRow(
        Guid Id, Guid InvestigationPackageId, string ProfileKey, string CaseId, string EntityKey, string? BrandName,
        string? ClubName,
        string Vertical, string Stage, string Status, string? Phase,
        DateTimeOffset StartedAt, DateTimeOffset? CompletedAt, long DurationMs,
        string? Verdict, string? Decision, string? ProbableRootCause, double Confidence, string? BlockedBy,
        string? SynthesisJson, string? BlackboardJson, string? RunbookJson, string? GroundingJson,
        int CitationsTotal, int CitationsVerified, int LlmCalls, int TokensIn, int TokensOut,
        string AlertLevel, string? AlertReason,
        string? OwnerTeam, string? OwnerPrimary, string? OwnerBackup, string? OwnerResolvedBy,
        string? ClubId, string? IcePartnerId, string? ClubResolvedBy,
        string? ErrorMessage)
    {
        public ForensicCase ToDomain() => new()
        {
            Id = Id,
            InvestigationPackageId = InvestigationPackageId,
            ProfileKey = ProfileKey,
            CaseId = CaseId,
            EntityKey = EntityKey,
            BrandName = BrandName,
            ClubName = ClubName,
            Vertical = Vertical,
            Stage = Stage,
            Status = Status,
            Phase = Phase,
            StartedAt = StartedAt,
            CompletedAt = CompletedAt,
            DurationMs = DurationMs,
            Verdict = Verdict,
            Decision = Decision,
            ProbableRootCause = ProbableRootCause,
            Confidence = Confidence,
            BlockedBy = BlockedBy,
            SynthesisJson = SynthesisJson,
            BlackboardJson = BlackboardJson,
            RunbookJson = RunbookJson,
            GroundingJson = GroundingJson,
            CitationsTotal = CitationsTotal,
            CitationsVerified = CitationsVerified,
            LlmCalls = LlmCalls,
            TokensIn = TokensIn,
            TokensOut = TokensOut,
            AlertLevel = AlertLevel,
            AlertReason = AlertReason,
            OwnerTeam = OwnerTeam,
            OwnerPrimary = OwnerPrimary,
            OwnerBackup = OwnerBackup,
            OwnerResolvedBy = OwnerResolvedBy,
            ClubId = ClubId,
            IcePartnerId = IcePartnerId,
            ClubResolvedBy = ClubResolvedBy,
            ErrorMessage = ErrorMessage,
        };
    }
}
