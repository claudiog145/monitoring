using System.Collections.Concurrent;
using JourneyMonitor.Core.Abstractions;

namespace JourneyMonitor.Infrastructure.Monitoring;

/// <summary>
/// In-memory editable KQL for the fresh-clone path, with the same append-only audit shape as the
/// SQL store. Starts empty, which means every key falls through to its built-in text - the same
/// behaviour as a database where nobody has overridden anything yet.
/// </summary>
public sealed class InMemoryKqlQueryStore : IKqlQueryStore
{
    private readonly ConcurrentDictionary<string, KqlQueryEntry> _queries = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<KqlQueryAuditEntry> _audit = [];
    private long _auditId;

    public Task<IReadOnlyList<KqlQuerySummary>> GetAllAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<KqlQuerySummary>>(
            _queries.Values
                .OrderBy(q => q.QueryKey, StringComparer.Ordinal)
                .Select(q => new KqlQuerySummary(
                    q.QueryKey, q.Version, q.Description, q.SourceRef, q.IsBuiltIn, q.Enabled,
                    q.UpdatedAt ?? DateTimeOffset.UtcNow, q.UpdatedBy ?? "", q.KqlText.Length))
                .ToList());

    public Task<IReadOnlyDictionary<string, string>> GetEnabledTextsAsync(CancellationToken ct = default)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var q in _queries.Values.Where(q => q.Enabled && !string.IsNullOrWhiteSpace(q.KqlText)))
        {
            map[q.QueryKey] = q.KqlText;
        }
        return Task.FromResult<IReadOnlyDictionary<string, string>>(map);
    }

    public Task<KqlQueryEntry?> GetAsync(string queryKey, CancellationToken ct = default) =>
        Task.FromResult(_queries.TryGetValue(queryKey, out var entry) ? entry : null);

    public Task<KqlQueryEntry> UpsertAsync(KqlQueryEntry entry, string changedBy, CancellationToken ct = default)
    {
        var nextVersion = _queries.TryGetValue(entry.QueryKey, out var existing) ? existing.Version + 1 : 1;
        var stored = entry with { Version = nextVersion, UpdatedAt = DateTimeOffset.UtcNow, UpdatedBy = changedBy };
        _queries[entry.QueryKey] = stored;
        Record(stored.QueryKey, nextVersion, stored.KqlText, stored.Description, stored.SourceRef, stored.Enabled, false, changedBy);
        return Task.FromResult(stored);
    }

    public Task DeleteAsync(string queryKey, string changedBy, CancellationToken ct = default)
    {
        var version = _queries.TryGetValue(queryKey, out var existing) ? existing.Version : 0;
        _queries.TryRemove(queryKey, out _);
        // Recorded even when nothing was removed: "someone reverted this to the built-in" is history.
        Record(queryKey, version, null, null, null, null, true, changedBy);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<KqlQueryAuditSummary>> GetAuditAsync(string? queryKey, int top, CancellationToken ct = default)
    {
        lock (_audit)
        {
            return Task.FromResult<IReadOnlyList<KqlQueryAuditSummary>>(
                _audit
                    .Where(a => queryKey is null || a.QueryKey.Equals(queryKey, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(a => a.AuditId)
                    .Take(top)
                    .Select(a => new KqlQueryAuditSummary(
                        a.AuditId, a.QueryKey, a.Version, a.Description, a.SourceRef, a.Enabled,
                        a.Removed, a.ChangedAt, a.ChangedBy, a.KqlText?.Length ?? 0))
                    .ToList());
        }
    }

    public Task<KqlQueryAuditEntry?> GetAuditEntryAsync(long auditId, CancellationToken ct = default)
    {
        lock (_audit)
        {
            return Task.FromResult(_audit.FirstOrDefault(a => a.AuditId == auditId));
        }
    }

    private void Record(string key, int version, string? text, string? description, string? sourceRef, bool? enabled, bool removed, string changedBy)
    {
        lock (_audit)
        {
            _audit.Add(new KqlQueryAuditEntry(
                ++_auditId, key, version, text, description, sourceRef, enabled, removed, DateTimeOffset.UtcNow, changedBy));
        }
    }
}
