using System.Collections.Concurrent;
using JourneyMonitor.Core.Abstractions;

namespace JourneyMonitor.Infrastructure.Monitoring;

/// <summary>Process-local run tracker for the MVP - see InMemoryFindingsRepository remarks.</summary>
public sealed class InMemoryMonkeyRunTracker : IMonkeyRunTracker
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastRuns = new();

    public Task RecordRunAsync(string monkeyKey, DateTimeOffset at, CancellationToken ct = default)
    {
        _lastRuns[monkeyKey] = at;
        return Task.CompletedTask;
    }

    public Task<DateTimeOffset?> GetLastRunAsync(string monkeyKey, CancellationToken ct = default) =>
        Task.FromResult(_lastRuns.TryGetValue(monkeyKey, out var at) ? at : (DateTimeOffset?)null);
}
