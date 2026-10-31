using JourneyMonitor.Core.Domain;

namespace JourneyMonitor.Core.Abstractions;

/// <summary>
/// Durable store for composed RCAs (<c>monitoring.RcaReport</c>).
///
/// One report per investigation, replaced on re-composition rather than versioned: a second run
/// against the same case is a correction, and keeping both would leave two leadership documents
/// about one incident with no way to tell which is current. The previous text is not lost - the
/// case's own transcript and this document's GeneratedAt say when it was written.
/// </summary>
public interface IRcaReportRepository
{
    /// <summary>Inserts or replaces the report for its investigation.</summary>
    Task<RcaReport> SaveAsync(RcaReport report, CancellationToken ct = default);

    /// <summary>Null when no RCA has been written for that investigation yet.</summary>
    Task<RcaReport?> GetByForensicCaseIdAsync(Guid forensicCaseId, CancellationToken ct = default);

    /// <summary>Most recently composed first. Feeds the agent card's activity feed.</summary>
    Task<IReadOnlyList<RcaReport>> GetRecentAsync(int top, CancellationToken ct = default);
}
