using JourneyMonitor.Core.Domain;

namespace JourneyMonitor.Core.Abstractions;

/// <summary>
/// What to ask the board for. Mirrors the Azure workbook's own parameters, which the Application
/// Insights REST API does not understand on its own - the reader substitutes them into the KQL
/// text before sending it.
/// </summary>
public sealed record ScoutBoardRequest(
    string Board,
    DateTimeOffset WindowFrom,
    DateTimeOffset WindowTo,
    int BucketMinutes,
    int TakePerClub,
    string Vertical,
    string ClubFilter);

/// <summary>
/// The rows of one board plus the exact KQL that produced them. The query text travels with the
/// result so the cycle can record what it actually asked Azure - a board row is not reproducible
/// without it, and the L3 team would otherwise have to rebuild the query by hand.
/// </summary>
public sealed record ScoutBoardResult(IReadOnlyList<ScoutBoardRow> Rows, string Kql);

/// <summary>
/// Runs one of the Scout's priority boards against Application Insights and returns its rows.
/// The board is cross-resource (it unions the SOR app with both Cruise apps), so the identity
/// running this needs read access to all three - see docs/sessions/session-2026-10-07.md.
/// Implementations must surface a failure to the caller rather than returning an empty list: a
/// board that cannot run is not the same as a board with nothing on it, and ScoutRun records the
/// difference.
/// </summary>
public interface IScoutBoardReader
{
    Task<ScoutBoardResult> ReadAsync(ScoutBoardRequest request, CancellationToken ct = default);
}

/// <summary>
/// Fetches the individual requests and exceptions behind one board row, so the handoff package
/// carries the raw evidence and not only the aggregate. Must never throw: a failed collection is
/// reported through <see cref="ScoutRawEvidence.Error"/> and the package still ships.
/// </summary>
public interface IScoutEvidenceCollector
{
    Task<ScoutRawEvidence> CollectAsync(ScoutBoardRow row, CancellationToken ct = default);
}

/// <summary>
/// Resolves the board's Entity key ("club:140" / "host:stays.tmobiletravel.com") to a real brand
/// using brand.Information. Tries club id, then ICE partner id (Cruise telemetry has no
/// VacationClubId - its partner id IS the club), then exact host, then base domain. Returns an
/// explicitly unresolved identity rather than guessing: the catalog currently covers a small
/// fraction of the clubs that actually appear on the board.
/// </summary>
public interface IClubIdentityResolver
{
    Task<ClubIdentity> ResolveAsync(string entityKey, CancellationToken ct = default);
}

/// <summary>
/// Reads one board row and decides, as a human L1/L2 analyst would, whether L3 should pick it up
/// and how to describe it. Never re-derives the board's thresholds - the board already did that
/// deterministically. Must never throw: when the LLM is unavailable the Scout returns
/// <see cref="ScoutAssessment.Unavailable"/> and the package still ships with facts only.
/// </summary>
public interface IScoutAssessor
{
    Task<ScoutAssessmentResult> AssessAsync(ScoutBoardRow row, ClubIdentity club, CancellationToken ct = default);
}

/// <summary>
/// Audit trail of the Scout's LLM calls - prompt sent and raw response received, same role
/// monitoring.LlmCallLog plays for the Doctor Monkey. Writes are best-effort: losing the log must
/// never cost the package it documents.
/// </summary>
public interface IScoutLlmCallLogRepository
{
    Task<ScoutLlmCall> AddAsync(ScoutLlmCall call, CancellationToken ct = default);

    Task<ScoutLlmCall?> GetByPackageIdAsync(Guid investigationPackageId, CancellationToken ct = default);
}

public interface IInvestigationPackageRepository
{
    Task<InvestigationPackage> AddAsync(InvestigationPackage package, CancellationToken ct = default);

    /// <summary>
    /// Dedup lookup, scoped to one board: the same bucket of the same group must only be packaged
    /// once PER SCOUT. The CaseId alone is not the key - both boards can flag the same group and
    /// bucket for different reasons, and that is two pieces of work, not one.
    /// </summary>
    Task<bool> ExistsByCaseIdAsync(string caseId, string board, string profileKey, CancellationToken ct = default);

    Task<InvestigationPackage?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// <paramref name="board"/> null returns both scouts' packages. <paramref name="profileKey"/>
    /// null returns EVERY company's - the pre-multi-company behaviour. Null and not "": the empty
    /// string is a real stored ProfileKey (rows written before the column existed), so it means
    /// "the unattributed ones", never "all of them".
    /// </summary>
    Task<IReadOnlyList<InvestigationPackage>> GetRecentAsync(int top, string? board = null, string? profileKey = null, CancellationToken ct = default);

    /// <summary>
    /// How often this same group has been packaged before. A single package is a 5-minute snapshot,
    /// so without this the reader cannot tell a one-off from something still firing every cycle.
    /// </summary>
    Task<PackageRecurrence> GetRecurrenceAsync(string groupKey, CancellationToken ct = default);
}

public interface IScoutRunRepository
{
    Task<ScoutRun> AddAsync(ScoutRun run, CancellationToken ct = default);

    Task<ScoutRun> UpdateAsync(ScoutRun run, CancellationToken ct = default);

    /// <summary>
    /// Cheap mid-cycle write so an in-flight cycle is visible. Best-effort by contract: failing to
    /// record progress must never abort the cycle it is describing.
    /// </summary>
    Task UpdatePhaseAsync(Guid runId, string phase, int boardRowsEvaluated, int clubsWatched, CancellationToken ct = default);

    /// <summary>
    /// <paramref name="board"/> null returns both scouts' cycles, <paramref name="profileKey"/>
    /// null every company's - same null-vs-empty-string contract as the package read above.
    /// </summary>
    Task<IReadOnlyList<ScoutRun>> GetRecentAsync(int top, string? board = null, string? profileKey = null, CancellationToken ct = default);

    /// <summary>
    /// Cumulative counters across every recorded cycle - see <see cref="ScoutStats"/>.
    /// <paramref name="board"/> null covers both scouts, <paramref name="profileKey"/> null every
    /// company - which sums several customers into one number, so a per-company view must name one.
    /// </summary>
    Task<ScoutStats> GetStatsAsync(string? board = null, string? profileKey = null, CancellationToken ct = default);
}
