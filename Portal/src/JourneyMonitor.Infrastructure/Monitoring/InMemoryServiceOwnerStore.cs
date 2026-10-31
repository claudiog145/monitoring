using System.Collections.Concurrent;
using JourneyMonitor.Core.Abstractions;

namespace JourneyMonitor.Infrastructure.Monitoring;

/// <summary>
/// In-memory service ownership map for the fresh-clone path, with the same append-only audit shape
/// as the SQL store. Starts empty, which is also the honest state of the real map until the named
/// responders are agreed.
/// </summary>
public sealed class InMemoryServiceOwnerStore : IServiceOwnerStore
{
    private readonly ConcurrentDictionary<string, ServiceOwner> _owners = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ServiceOwnerAuditEntry> _audit = [];
    private long _auditId;

    public Task<IReadOnlyList<ServiceOwner>> GetAllAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ServiceOwner>>(
            _owners.Values.OrderBy(o => o.Vertical).ThenBy(o => o.Stage).ToList());

    public Task<ServiceOwner> UpsertAsync(ServiceOwner owner, string changedBy, CancellationToken ct = default)
    {
        var stored = owner with { UpdatedAt = DateTimeOffset.UtcNow, UpdatedBy = changedBy };
        _owners[Key(owner.Vertical, owner.Stage)] = stored;
        Record(owner.Vertical, owner.Stage, owner.OwnerTeam, owner.PrimaryResponder, owner.Backup, owner.ContactRef, false, changedBy);
        return Task.FromResult(stored);
    }

    public Task DeleteAsync(string vertical, string stage, string changedBy, CancellationToken ct = default)
    {
        _owners.TryRemove(Key(vertical, stage), out _);
        // Recorded even when nothing was removed: the attempt is still history.
        Record(vertical, stage, null, null, null, null, true, changedBy);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ServiceOwnerAuditEntry>> GetAuditAsync(int top, CancellationToken ct = default)
    {
        lock (_audit)
        {
            return Task.FromResult<IReadOnlyList<ServiceOwnerAuditEntry>>(
                _audit.OrderByDescending(a => a.AuditId).Take(top).ToList());
        }
    }

    private void Record(string vertical, string stage, string? team, string? primary, string? backup, string? contact, bool removed, string changedBy)
    {
        lock (_audit)
        {
            _audit.Add(new ServiceOwnerAuditEntry(
                ++_auditId, vertical, stage, team, primary, backup, contact, removed, DateTimeOffset.UtcNow, changedBy));
        }
    }

    private static string Key(string vertical, string stage) => $"{vertical.Trim()}|{stage.Trim()}";
}
