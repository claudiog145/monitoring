using System.Data;
using Dapper;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Options;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Infrastructure.Sql;

/// <summary>SQL-backed Scout cycle log (monitoring.ScoutRun) - survives process/container restarts.</summary>
public sealed class SqlScoutRunRepository(IOptions<JourneyStoreOptions> storeOptions) : IScoutRunRepository
{
    public async Task<ScoutRun> AddAsync(ScoutRun run, CancellationToken ct = default)
    {
        var parameters = new DynamicParameters();
        parameters.Add("Id", run.Id);
        parameters.Add("Board", run.Board, DbType.String, size: 16);
        parameters.Add("ProfileKey", run.ProfileKey, DbType.String, size: 128);
        parameters.Add("StartedAt", run.StartedAt);
        parameters.Add("CompletedAt", run.CompletedAt);
        parameters.Add("Status", run.Status, DbType.String, size: 16);
        parameters.Add("WindowFrom", run.WindowFrom);
        parameters.Add("WindowTo", run.WindowTo);
        AddCounters(parameters, run);

        await using var connection = OpenConnection();
        await connection.ExecuteAsync(new CommandDefinition(
            "monitoring.usp_AddScoutRun", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));
        return run;
    }

    public async Task<ScoutRun> UpdateAsync(ScoutRun run, CancellationToken ct = default)
    {
        var parameters = new DynamicParameters();
        parameters.Add("Id", run.Id);
        // The row is inserted before the company profile resolves, so this is the write that
        // actually attributes the cycle - the insert can only ever carry the empty default.
        parameters.Add("ProfileKey", run.ProfileKey, DbType.String, size: 128);
        parameters.Add("CompletedAt", run.CompletedAt);
        parameters.Add("Status", run.Status, DbType.String, size: 16);
        parameters.Add("BoardQueriesJson", run.BoardQueriesJson, DbType.String, size: -1);
        parameters.Add("SkippedCaseIdsJson", run.SkippedCaseIdsJson, DbType.String, size: -1);
        AddCounters(parameters, run);

        await using var connection = OpenConnection();
        await connection.ExecuteAsync(new CommandDefinition(
            "monitoring.usp_UpdateScoutRun", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));
        return run;
    }

    public async Task UpdatePhaseAsync(Guid runId, string phase, int boardRowsEvaluated, int clubsWatched, CancellationToken ct = default)
    {
        var parameters = new DynamicParameters();
        parameters.Add("Id", runId);
        parameters.Add("Phase", phase, DbType.String, size: 32);
        parameters.Add("BoardRowsEvaluated", boardRowsEvaluated);
        parameters.Add("ClubsWatched", clubsWatched);

        await using var connection = OpenConnection();
        await connection.ExecuteAsync(new CommandDefinition(
            "monitoring.usp_UpdateScoutRunPhase", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));
    }

    public async Task<ScoutStats> GetStatsAsync(string? board = null, string? profileKey = null, CancellationToken ct = default)
    {
        var parameters = new DynamicParameters();
        parameters.Add("Board", board, DbType.String, size: 16);
        parameters.Add("ProfileKey", NullIfBlank(profileKey), DbType.String, size: 128);

        await using var connection = OpenConnection();
        var row = await connection.QuerySingleOrDefaultAsync<ScoutStats>(new CommandDefinition(
            "monitoring.usp_GetScoutStats", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));
        return row ?? ScoutStats.Empty;
    }

    public async Task<IReadOnlyList<ScoutRun>> GetRecentAsync(int top, string? board = null, string? profileKey = null, CancellationToken ct = default)
    {
        var parameters = new DynamicParameters();
        parameters.Add("Top", top);
        parameters.Add("Board", board, DbType.String, size: 16);
        // Blank collapses to NULL - the proc reads NULL as "every company". Sending N'' instead
        // would match the rows written before ProfileKey existed and nothing else.
        parameters.Add("ProfileKey", NullIfBlank(profileKey), DbType.String, size: 128);

        await using var connection = OpenConnection();
        var rows = await connection.QueryAsync<ScoutRunRow>(new CommandDefinition(
            "monitoring.usp_GetRecentScoutRuns", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));
        return rows.Select(r => r.ToDomain()).ToList();
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static void AddCounters(DynamicParameters parameters, ScoutRun run)
    {
        parameters.Add("BoardRowsEvaluated", run.BoardRowsEvaluated);
        parameters.Add("ClubsWatched", run.ClubsWatched);
        parameters.Add("CandidatesFound", run.CandidatesFound);
        parameters.Add("PackagesCreated", run.PackagesCreated);
        parameters.Add("PackagesSkippedDuplicate", run.PackagesSkippedDuplicate);
        parameters.Add("PackagesSkippedCap", run.PackagesSkippedCap);
        parameters.Add("PackagesVetoedByScout", run.PackagesVetoedByScout);
        parameters.Add("DurationMs", run.DurationMs);
        parameters.Add("ErrorMessage", run.ErrorMessage, DbType.String, size: 1024);
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
    /// Positional record: Dapper matches the selected columns to the constructor parameters by
    /// position and name and requires equal counts, so this list and the SELECT in
    /// usp_GetRecentScoutRuns have to be changed together or the query throws at runtime.
    /// </summary>
    private sealed record ScoutRunRow(
        Guid Id, string Board, string ProfileKey, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt, string Status,
        DateTimeOffset WindowFrom, DateTimeOffset WindowTo, int BoardRowsEvaluated, int ClubsWatched,
        string? Phase, int CandidatesFound,
        int PackagesCreated, int PackagesSkippedDuplicate, int PackagesSkippedCap, int PackagesVetoedByScout,
        long DurationMs, string? BoardQueriesJson, string? SkippedCaseIdsJson, string? ErrorMessage)
    {
        public ScoutRun ToDomain() => new()
        {
            Id = Id,
            Board = Board,
            ProfileKey = ProfileKey,
            StartedAt = StartedAt,
            CompletedAt = CompletedAt,
            Status = Status,
            WindowFrom = WindowFrom,
            WindowTo = WindowTo,
            BoardRowsEvaluated = BoardRowsEvaluated,
            ClubsWatched = ClubsWatched,
            Phase = Phase,
            CandidatesFound = CandidatesFound,
            PackagesCreated = PackagesCreated,
            PackagesSkippedDuplicate = PackagesSkippedDuplicate,
            PackagesSkippedCap = PackagesSkippedCap,
            PackagesVetoedByScout = PackagesVetoedByScout,
            DurationMs = DurationMs,
            BoardQueriesJson = BoardQueriesJson,
            SkippedCaseIdsJson = SkippedCaseIdsJson,
            ErrorMessage = ErrorMessage,
        };
    }
}
