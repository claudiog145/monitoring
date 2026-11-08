using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;

namespace JourneyMonitor.Infrastructure.Monitoring;

/// <summary>
/// Process-local daemon activity log (no JourneyStore or no monitoring.DaemonActivity table). Same paging and
/// filter semantics as usp_GetDaemonActivity; keeps at most <see cref="MaxEntries"/> entries, oldest dropped first.
/// </summary>
public sealed class InMemoryDaemonActivityLog : IDaemonActivityLog
{
    public const int MaxEntries = 10_000;
    public const int MaxMessageLength = 1000;

    private readonly object _gate = new();
    private readonly LinkedList<DaemonActivityEntry> _entries = new();
    private long _nextId;

    public Task AppendAsync(IReadOnlyList<DaemonActivityEntry> entries, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        lock (_gate)
        {
            foreach (var entry in entries)
            {
                var message = entry.Message ?? "";
                if (message.Length > MaxMessageLength) message = message[..MaxMessageLength];
                _entries.AddLast(entry with { ActivityId = ++_nextId, Message = message });
            }
            while (_entries.Count > MaxEntries) _entries.RemoveFirst();
        }
        return Task.CompletedTask;
    }

    public Task<DaemonActivityPage> GetAsync(long afterId, string? daemonKey, DaemonActivityLevel? minLevel, int top, CancellationToken ct = default)
    {
        var take = Math.Clamp(top, 1, 1000);
        List<DaemonActivityEntry> page;
        lock (_gate)
        {
            var matching = _entries.Where(e =>
                (daemonKey is null || string.Equals(e.DaemonKey, daemonKey, StringComparison.OrdinalIgnoreCase))
                && (minLevel is null || e.Level >= minLevel.Value));

            page = afterId > 0
                ? matching.Where(e => e.ActivityId > afterId).Take(take).ToList()
                : matching.Reverse().Take(take).Reverse().ToList();
        }
        var lastId = page.Count > 0 ? page[^1].ActivityId : Math.Max(0, afterId);
        return Task.FromResult(new DaemonActivityPage(page, lastId));
    }

    public Task<int> PurgeOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        var removed = 0;
        lock (_gate)
        {
            var node = _entries.First;
            while (node is not null)
            {
                var next = node.Next;
                if (node.Value.At < cutoff)
                {
                    _entries.Remove(node);
                    removed++;
                }
                node = next;
            }
        }
        return Task.FromResult(removed);
    }
}
