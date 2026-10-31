using System.Collections.Concurrent;
using JourneyMonitor.Core.Abstractions;

namespace JourneyMonitor.Infrastructure.Caching;

/// <summary>
/// Process-local 90-second query cache. Same cache-key format and TTL semantics as the
/// original Node in-process Map cache. Does not survive a restart and is not shared across
/// Function instances; durable snapshots belong in <see cref="IJourneyStore"/>.
/// </summary>
public sealed class InMemoryTelemetryQueryCache : ITelemetryQueryCache
{
    private sealed record Entry(string PayloadJson, DateTimeOffset CachedAt, DateTimeOffset ExpiresAt);

    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    public Task<CachedQueryPayload?> GetAsync(string key, CancellationToken cancellationToken)
    {
        if (_entries.TryGetValue(key, out var entry) && entry.ExpiresAt > DateTimeOffset.UtcNow)
        {
            return Task.FromResult<CachedQueryPayload?>(new CachedQueryPayload(entry.PayloadJson, entry.CachedAt));
        }
        _entries.TryRemove(key, out _);
        return Task.FromResult<CachedQueryPayload?>(null);
    }

    public Task SetAsync(string key, string payloadJson, TimeSpan timeToLive, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        _entries[key] = new Entry(payloadJson, now, now + timeToLive);
        return Task.CompletedTask;
    }
}
