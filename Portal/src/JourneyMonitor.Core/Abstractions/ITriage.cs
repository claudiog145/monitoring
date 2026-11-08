using JourneyMonitor.Core.Domain;

namespace JourneyMonitor.Core.Abstractions;

public interface ITriageSignalSubmitter
{
    /// <summary>Creates a Queued investigation and enqueues AgentWorkPayloadType.TriageSignal, unless an open (Queued/Running) one with the same dedup key exists - then returns it with Duplicate=true.</summary>
    Task<TriageSubmission> SubmitAsync(TriageSignal signal, CancellationToken ct = default);
}

public interface IEvidenceProvider
{
    /// <summary>One of TriageDimensions.All.</summary>
    string Dimension { get; }
    Task<EvidenceSection> CollectAsync(TriageSignal signal, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken ct = default);
}

public interface ITriageInvestigationStore
{
    Task CreateAsync(TriageInvestigation investigation, CancellationToken ct = default);
    Task<TriageInvestigation?> GetAsync(Guid investigationId, CancellationToken ct = default);
    /// <summary>Only Queued or Running investigations.</summary>
    Task<TriageInvestigation?> FindOpenByDedupKeyAsync(string dedupKey, CancellationToken ct = default);
    Task UpdateStatusAsync(Guid investigationId, TriageInvestigationStatus status, string? error, CancellationToken ct = default);
    Task CompleteAsync(Guid investigationId, TriageResult result, bool notified, CancellationToken ct = default);
    /// <summary>Newest first. profileKey filters on Signal.ProfileKey; null means every profile.</summary>
    Task<IReadOnlyList<TriageInvestigation>> GetRecentAsync(int top, string? profileKey = null, CancellationToken ct = default);
}
