using JourneyMonitor.Core.Domain;

namespace JourneyMonitor.Core.Abstractions;

/// <summary>
/// Durable store for <see cref="GoldenSignalSample"/> buckets (see <c>monitoring.GoldenSignalSample</c>).
/// Backed by the same SQL database as <see cref="IJourneyStore"/> - requires
/// <c>JourneyStore:ConnectionString</c> to be configured; callers (GoldenSignalCollector,
/// BurnRateDetector) must degrade gracefully rather than throw when it isn't, same pattern as
/// every other Monitoring store in this repo.
/// </summary>
public interface IGoldenSignalStore
{
    /// <summary>Upserts one bucket - idempotent by (Domain, Stage, SignalType, BucketStart), safe to call every collector cycle even if the bucket was already written.</summary>
    Task AddOrUpdateAsync(GoldenSignalSample sample, CancellationToken cancellationToken);

    /// <summary>Every bucket for (domain, signalType[, stage]) at or after <paramref name="since"/>, oldest first - the raw material BurnRateDetector sums per window itself (kept out of SQL so the burn-rate math stays unit-testable in Core).</summary>
    Task<IReadOnlyList<GoldenSignalSample>> GetSinceAsync(string domain, string signalType, string? stage, DateTimeOffset since, CancellationToken cancellationToken);
}
