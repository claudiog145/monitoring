using System.Collections.Concurrent;
using JourneyMonitor.Core.Abstractions;

namespace JourneyMonitor.Infrastructure.Monitoring;

/// <summary>Process-local NotificationLog store for local development / MVP - see InMemoryFindingsRepository remarks.</summary>
public sealed class InMemoryNotificationLogRepository : INotificationLogRepository
{
    private readonly ConcurrentBag<NotificationLogEntry> _entries = new();

    public Task<NotificationLogEntry> AddAsync(NotificationLogEntry entry, CancellationToken ct = default)
    {
        _entries.Add(entry);
        return Task.FromResult(entry);
    }

    public Task<IReadOnlyList<NotificationLogEntry>> GetBySourceAsync(string sourceType, Guid sourceId, CancellationToken ct = default)
    {
        IReadOnlyList<NotificationLogEntry> matches = _entries
            .Where(e => e.SourceId == sourceId && string.Equals(e.SourceType, sourceType, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.SentAt)
            .ToList();
        return Task.FromResult(matches);
    }

    public Task<IReadOnlyList<NotificationLogEntry>> GetAllAsync(CancellationToken ct = default)
    {
        IReadOnlyList<NotificationLogEntry> all = _entries.OrderByDescending(e => e.SentAt).ToList();
        return Task.FromResult(all);
    }

    public Task<IReadOnlyList<NotificationLogEntry>> GetUndeliveredToastsAsync(DateTimeOffset since, CancellationToken ct = default)
    {
        IReadOnlyList<NotificationLogEntry> toasts = _entries
            .Where(e => e.Channel == NotificationChannel.Toast && e.Success && e.SentAt > since)
            .OrderBy(e => e.SentAt)
            .ToList();
        return Task.FromResult(toasts);
    }
}
