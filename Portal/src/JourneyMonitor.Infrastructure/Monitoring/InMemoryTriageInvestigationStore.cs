using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring.Inspector;

namespace JourneyMonitor.Infrastructure.Monitoring;

/// <summary>
/// Process-local triage investigations with the semantics of the SQL procedures: create always
/// starts Queued with CreatedAt = now, at most one open (Queued/Running) investigation per DedupKey,
/// Failed/Discarded stamp CompletedAt, Complete stores the result and its alert level.
/// </summary>
public sealed class InMemoryTriageInvestigationStore(TimeProvider? timeProvider = null) : ITriageInvestigationStore
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, TriageInvestigation> _investigations = [];

    public Task CreateAsync(TriageInvestigation investigation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(investigation);
        lock (_gate)
        {
            if (_investigations.ContainsKey(investigation.InvestigationId))
            {
                throw new InvalidOperationException($"Triage investigation {investigation.InvestigationId} already exists.");
            }
            if (FindOpen(investigation.DedupKey) is not null)
            {
                throw new InvalidOperationException($"An open triage investigation already exists for dedup key '{investigation.DedupKey}'.");
            }

            _investigations[investigation.InvestigationId] = investigation with
            {
                Status = TriageInvestigationStatus.Queued,
                Result = null,
                AlertLevel = null,
                Notified = false,
                CreatedAt = _time.GetUtcNow(),
                CompletedAt = null,
                Error = null,
            };
        }
        return Task.CompletedTask;
    }

    public Task<TriageInvestigation?> GetAsync(Guid investigationId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_investigations.GetValueOrDefault(investigationId));
        }
    }

    public Task<TriageInvestigation?> FindOpenByDedupKeyAsync(string dedupKey, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(FindOpen(dedupKey));
        }
    }

    public Task UpdateStatusAsync(Guid investigationId, TriageInvestigationStatus status, string? error, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_investigations.TryGetValue(investigationId, out var current))
            {
                if (status is TriageInvestigationStatus.Queued or TriageInvestigationStatus.Running
                    && current.Status is not (TriageInvestigationStatus.Queued or TriageInvestigationStatus.Running)
                    && FindOpen(current.DedupKey) is not null)
                {
                    throw new InvalidOperationException($"An open triage investigation already exists for dedup key '{current.DedupKey}'.");
                }

                _investigations[investigationId] = current with
                {
                    Status = status,
                    Error = error,
                    CompletedAt = status is TriageInvestigationStatus.Failed or TriageInvestigationStatus.Discarded
                        ? _time.GetUtcNow()
                        : current.CompletedAt,
                };
            }
        }
        return Task.CompletedTask;
    }

    public Task CompleteAsync(Guid investigationId, TriageResult result, bool notified, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        lock (_gate)
        {
            if (_investigations.TryGetValue(investigationId, out var current))
            {
                _investigations[investigationId] = current with
                {
                    Status = TriageInvestigationStatus.Completed,
                    Result = result,
                    AlertLevel = result.Assessment.AlertLevel,
                    Notified = notified,
                    CompletedAt = _time.GetUtcNow(),
                };
            }
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TriageInvestigation>> GetRecentAsync(int top, string? profileKey = null, CancellationToken ct = default)
    {
        var profile = string.IsNullOrWhiteSpace(profileKey) ? null : profileKey.Trim();
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<TriageInvestigation>>(_investigations.Values
                .Where(i => profile is null || string.Equals(TriageMonkeyService.ProfileKeyOf(i.Signal), profile, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(i => i.CreatedAt)
                .Take(Math.Max(top, 0))
                .ToList());
        }
    }

    private TriageInvestigation? FindOpen(string dedupKey) => _investigations.Values
        .Where(i => string.Equals(i.DedupKey, dedupKey, StringComparison.OrdinalIgnoreCase)
            && i.Status is TriageInvestigationStatus.Queued or TriageInvestigationStatus.Running)
        .OrderByDescending(i => i.CreatedAt)
        .FirstOrDefault();
}
