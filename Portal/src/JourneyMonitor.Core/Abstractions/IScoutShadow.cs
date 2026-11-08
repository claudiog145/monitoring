using JourneyMonitor.Core.Domain;

namespace JourneyMonitor.Core.Abstractions;

/// <summary>Contract v4 §4: persistence of monitoring.ScoutShadowComparison.</summary>
public interface IScoutShadowStore
{
    Task AddRangeAsync(IReadOnlyList<ScoutShadowComparison> comparisons, CancellationToken ct = default);

    /// <summary>Rows of one board whose BucketStart is within the last <paramref name="hours"/>, newest first. profileKey null means every profile.</summary>
    Task<IReadOnlyList<ScoutShadowComparison>> GetRecentAsync(string board, int hours, string? profileKey, CancellationToken ct = default);

    /// <summary>Same window and filters as <see cref="GetRecentAsync"/>, counted per agreement.</summary>
    Task<ScoutShadowCounts> GetCountsAsync(string board, int hours, string? profileKey, CancellationToken ct = default);
}
