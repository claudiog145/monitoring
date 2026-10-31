using System.Collections.Concurrent;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Configuration;

namespace JourneyMonitor.Infrastructure.Monitoring;

/// <summary>
/// In-memory telemetry source catalog for the fresh-clone path, with the same append-only audit
/// shape as the SQL store.
///
/// Seeded from <see cref="TelemetrySourceDefaults"/> rather than starting empty - the deliberate
/// difference from <see cref="InMemoryServiceOwnerStore"/>. An empty ownership map is the honest
/// state of a decision nobody has made; an empty source catalog just means every query resolves to
/// nothing, which looks identical to "no problems found".
/// </summary>
public sealed class InMemoryTelemetrySourceStore : ITelemetrySourceStore
{
    private readonly ConcurrentDictionary<string, TelemetrySourceEntry> _sources = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<TelemetrySourceAuditEntry> _audit = [];
    private long _auditId;

    public InMemoryTelemetrySourceStore()
    {
        foreach (var entry in TelemetrySourceDefaults.All)
        {
            _sources[Key(entry.Alias, entry.Environment)] = entry;
        }
    }

    public Task<IReadOnlyList<TelemetrySourceEntry>> GetAllAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<TelemetrySourceEntry>>(
            _sources.Values.OrderBy(s => s.Alias).ThenBy(s => s.Environment).ToList());

    public Task<TelemetrySourceEntry> UpsertAsync(TelemetrySourceEntry source, string changedBy, CancellationToken ct = default)
    {
        var stored = source with { UpdatedAt = DateTimeOffset.UtcNow, UpdatedBy = changedBy };
        _sources[Key(source.Alias, source.Environment)] = stored;
        Record(stored, false, changedBy);
        return Task.FromResult(stored);
    }

    public Task DeleteAsync(string alias, string environment, string changedBy, CancellationToken ct = default)
    {
        _sources.TryRemove(Key(alias, environment), out _);
        // Recorded even when nothing was removed: the attempt is still history.
        Record(new TelemetrySourceEntry(alias, environment, "", ""), true, changedBy);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TelemetrySourceAuditEntry>> GetAuditAsync(int top, CancellationToken ct = default)
    {
        lock (_audit)
        {
            return Task.FromResult<IReadOnlyList<TelemetrySourceAuditEntry>>(
                _audit.OrderByDescending(a => a.AuditId).Take(top).ToList());
        }
    }

    private void Record(TelemetrySourceEntry entry, bool removed, string changedBy)
    {
        lock (_audit)
        {
            _audit.Add(new TelemetrySourceAuditEntry(
                ++_auditId, entry.Alias, entry.Environment,
                removed ? null : entry.Kind,
                removed ? null : entry.ResourceId,
                removed ? null : entry.AppId,
                removed ? null : entry.TenantId,
                removed ? null : entry.Enabled,
                removed ? null : entry.Notes,
                removed, DateTimeOffset.UtcNow, changedBy));
        }
    }

    private static string Key(string alias, string environment) => $"{alias.Trim()}|{environment.Trim()}";
}
