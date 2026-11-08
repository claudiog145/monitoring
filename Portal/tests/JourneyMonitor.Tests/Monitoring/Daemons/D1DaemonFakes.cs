using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring.Daemons;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Infrastructure.Monitoring;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JourneyMonitor.Tests.Spec1000.D1;

internal sealed class D1FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>ILogger that keeps every entry so a test can assert a failure was logged.</summary>
internal sealed class D1ListLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (Entries) Entries.Add((logLevel, formatter(state, exception), exception));
    }
}

/// <summary>Activity log whose every call throws.</summary>
internal sealed class D1ThrowingActivityLog : IDaemonActivityLog
{
    public int AppendCalls;

    public Task AppendAsync(IReadOnlyList<DaemonActivityEntry> entries, CancellationToken ct = default)
    {
        Interlocked.Increment(ref AppendCalls);
        throw new InvalidOperationException("activity store down");
    }

    public Task<DaemonActivityPage> GetAsync(long afterId, string? daemonKey, DaemonActivityLevel? minLevel, int top, CancellationToken ct = default) =>
        throw new InvalidOperationException("activity store down");

    public Task<int> PurgeOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default) =>
        throw new InvalidOperationException("activity store down");
}

/// <summary>Real recorder over an in-memory log, plus the helpers the instrumentation tests read back with.</summary>
internal sealed class D1ActivityHarness
{
    public D1ActivityHarness(TimeProvider? time = null)
    {
        Recorder = new DaemonActivityRecorder(Log, time ?? TimeProvider.System, NullLogger<DaemonActivityRecorder>.Instance);
    }

    public InMemoryDaemonActivityLog Log { get; } = new();
    public InMemoryMonkeyRunTracker Tracker { get; } = new();
    public DaemonActivityRecorder Recorder { get; }

    public async Task<IReadOnlyList<DaemonActivityEntry>> EntriesAsync(string? daemonKey = null) =>
        (await Log.GetAsync(0, daemonKey, null, 1000)).Entries;
}

internal static class D1Entries
{
    public static DaemonActivityEntry Entry(
        string daemonKey, DateTimeOffset at, string kind, DaemonActivityLevel level = DaemonActivityLevel.Info, string? message = null, Guid? runId = null) =>
        new(0, daemonKey, runId ?? Guid.Empty, at, level, kind, message ?? kind, null);

    public static HealthMonitoringOptions AllPillars() => new() { Enabled = true, Pillars = ["Application", "Business", "Infrastructure"] };
}
