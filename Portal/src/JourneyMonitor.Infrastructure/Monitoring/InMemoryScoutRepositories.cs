using System.Collections.Concurrent;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;

namespace JourneyMonitor.Infrastructure.Monitoring;

/// <summary>
/// "Which company's rows?", shared by both stores so they answer it the same way the read procs
/// do: null or blank means every company, and the empty string is never treated as a wildcard -
/// it is a real stored ProfileKey (the rows written before the column existed).
/// </summary>
internal static class ScoutProfileFilter
{
    public static bool Matches(string storedProfileKey, string? requestedProfileKey) =>
        string.IsNullOrWhiteSpace(requestedProfileKey)
        || string.Equals(storedProfileKey, requestedProfileKey, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Process-local Scout stores, used when no JourneyStore connection string is configured so a
/// fresh clone still runs end to end. Same graceful-degradation contract as the other
/// InMemory* repositories in this folder: data is lost on restart, by design.
/// </summary>
public sealed class InMemoryInvestigationPackageRepository : IInvestigationPackageRepository
{
    private readonly ConcurrentDictionary<Guid, InvestigationPackage> _byId = new();
    private readonly ConcurrentDictionary<string, Guid> _byCaseAndBoard = new(StringComparer.OrdinalIgnoreCase);

    public Task<InvestigationPackage> AddAsync(InvestigationPackage package, CancellationToken ct = default)
    {
        // Mirrors the SQL proc's idempotency: first writer for a (CaseId, Board, ProfileKey) wins,
        // later ones are no-ops. Scoped to the board so each scout dedups independently of the
        // other, and to the profile so one company's package never suppresses another's - the
        // CaseId shape is identical for two companies reading the same telemetry.
        if (_byCaseAndBoard.TryAdd(DedupKey(package.CaseId, package.Board, package.ProfileKey), package.Id))
        {
            _byId[package.Id] = package;
        }
        return Task.FromResult(package);
    }

    public Task<bool> ExistsByCaseIdAsync(string caseId, string board, string profileKey, CancellationToken ct = default) =>
        Task.FromResult(_byCaseAndBoard.ContainsKey(DedupKey(caseId, board, profileKey)));

    public Task<InvestigationPackage?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult(_byId.TryGetValue(id, out var p) ? p : null);

    public Task<IReadOnlyList<InvestigationPackage>> GetRecentAsync(int top, string? board = null, string? profileKey = null, CancellationToken ct = default)
    {
        IReadOnlyList<InvestigationPackage> result = _byId.Values
            .Where(p => board is null || string.Equals(p.Board, board, StringComparison.OrdinalIgnoreCase))
            .Where(p => ScoutProfileFilter.Matches(p.ProfileKey, profileKey))
            .OrderByDescending(p => p.CreatedAt)
            .Take(top)
            .ToList();
        return Task.FromResult(result);
    }

    private static string DedupKey(string caseId, string board, string profileKey) => $"{profileKey}|{board}|{caseId}";

    public Task<PackageRecurrence> GetRecurrenceAsync(string groupKey, CancellationToken ct = default)
    {
        var matches = _byId.Values
            .Where(p => string.Equals(GroupKeyOf(p.CaseId), groupKey, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return Task.FromResult(matches.Count == 0
            ? PackageRecurrence.None
            : new PackageRecurrence(matches.Count, matches.Min(p => p.WindowFrom), matches.Max(p => p.WindowFrom)));
    }

    /// <summary>Mirrors usp_GetInvestigationPackageRecurrence: CaseId is "{GroupKey} | {bucket}".</summary>
    private static string GroupKeyOf(string caseId)
    {
        var lastSeparator = caseId.LastIndexOf('|');
        return lastSeparator < 0 ? caseId : caseId[..lastSeparator].TrimEnd();
    }
}

public sealed class InMemoryScoutRunRepository : IScoutRunRepository
{
    private readonly ConcurrentDictionary<Guid, ScoutRun> _runs = new();

    public Task<ScoutRun> AddAsync(ScoutRun run, CancellationToken ct = default)
    {
        _runs[run.Id] = run;
        return Task.FromResult(run);
    }

    public Task<ScoutRun> UpdateAsync(ScoutRun run, CancellationToken ct = default)
    {
        _runs[run.Id] = run;
        return Task.FromResult(run);
    }

    public Task UpdatePhaseAsync(Guid runId, string phase, int boardRowsEvaluated, int clubsWatched, CancellationToken ct = default)
    {
        if (_runs.TryGetValue(runId, out var run) && run.CompletedAt is null)
        {
            run.Phase = phase;
            run.BoardRowsEvaluated = boardRowsEvaluated;
            run.ClubsWatched = clubsWatched;
        }
        return Task.CompletedTask;
    }

    public Task<ScoutStats> GetStatsAsync(string? board = null, string? profileKey = null, CancellationToken ct = default)
    {
        var all = _runs.Values
            .Where(r => board is null || string.Equals(r.Board, board, StringComparison.OrdinalIgnoreCase))
            .Where(r => ScoutProfileFilter.Matches(r.ProfileKey, profileKey))
            .ToList();
        if (all.Count == 0) return Task.FromResult(ScoutStats.Empty);

        return Task.FromResult(new ScoutStats(
            Cycles: all.Count(r => r.CompletedAt is not null),
            ClearCycles: all.Count(r => r.Status == ScoutRunStatus.Clear),
            DetectedCycles: all.Count(r => r.Status == ScoutRunStatus.Detected),
            FailedCycles: all.Count(r => r.Status == ScoutRunStatus.Failed),
            BoardRowsEvaluated: all.Sum(r => (long)r.BoardRowsEvaluated),
            CandidatesFound: all.Sum(r => r.CandidatesFound),
            PackagesCreated: all.Sum(r => r.PackagesCreated),
            DuplicatesAvoided: all.Sum(r => r.PackagesSkippedDuplicate),
            MaxClubsWatched: all.Max(r => r.ClubsWatched),
            // The in-memory store has no call log to join against; the SQL path reports real totals.
            LlmCalls: 0,
            TokensIn: 0,
            TokensOut: 0,
            FirstCycleAt: all.Min(r => r.StartedAt),
            LastCycleAt: all.Max(r => r.StartedAt)));
    }

    public Task<IReadOnlyList<ScoutRun>> GetRecentAsync(int top, string? board = null, string? profileKey = null, CancellationToken ct = default)
    {
        IReadOnlyList<ScoutRun> result = _runs.Values
            .Where(r => board is null || string.Equals(r.Board, board, StringComparison.OrdinalIgnoreCase))
            .Where(r => ScoutProfileFilter.Matches(r.ProfileKey, profileKey))
            .OrderByDescending(r => r.StartedAt)
            .Take(top)
            .ToList();
        return Task.FromResult(result);
    }
}

public sealed class InMemoryScoutLlmCallLogRepository : IScoutLlmCallLogRepository
{
    private readonly ConcurrentDictionary<Guid, ScoutLlmCall> _byPackageId = new();

    public Task<ScoutLlmCall> AddAsync(ScoutLlmCall call, CancellationToken ct = default)
    {
        _byPackageId.TryAdd(call.InvestigationPackageId, call);
        return Task.FromResult(call);
    }

    public Task<ScoutLlmCall?> GetByPackageIdAsync(Guid investigationPackageId, CancellationToken ct = default) =>
        Task.FromResult(_byPackageId.TryGetValue(investigationPackageId, out var c) ? c : null);
}
