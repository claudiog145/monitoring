using JourneyMonitor.Core.Domain;

namespace JourneyMonitor.Core.Abstractions;

public interface IDaemonActivityLog
{
    /// <summary>Appends entries in order. Implementations must truncate Message to 1000 chars. ActivityId is ignored.</summary>
    Task AppendAsync(IReadOnlyList<DaemonActivityEntry> entries, CancellationToken ct = default);

    /// <summary>
    /// Entries with ActivityId &gt; afterId, ascending by ActivityId, at most top (1..1000).
    /// When afterId == 0 returns the LATEST top entries (still ascending). Filters are optional.
    /// </summary>
    Task<DaemonActivityPage> GetAsync(long afterId, string? daemonKey, DaemonActivityLevel? minLevel, int top, CancellationToken ct = default);

    Task<int> PurgeOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default);
}

/// <summary>Best-effort writer used by the daemons: never throws, never blocks a run on a logging failure.</summary>
public interface IDaemonActivityRecorder
{
    /// <summary>Starts a run scope; entries written through it share the RunId.</summary>
    IDaemonRun BeginRun(string daemonKey);
}

public interface IDaemonRun
{
    Guid RunId { get; }
    string DaemonKey { get; }
    void Info(string kind, string message, object? data = null);
    void Warning(string kind, string message, object? data = null);
    void Error(string kind, string message, object? data = null);
    /// <summary>Flushes buffered entries (best-effort, swallows and logs failures).</summary>
    Task FlushAsync(CancellationToken ct = default);
}

public interface IDaemonStatusReader
{
    /// <summary>Always returns one DaemonStatus per DaemonKeys.All, in that order.</summary>
    Task<IReadOnlyList<DaemonStatus>> GetAsync(CancellationToken ct = default);
}
