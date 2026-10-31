using System.Collections.Concurrent;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;

namespace JourneyMonitor.Infrastructure.Monitoring;

/// <summary>
/// Process-local Findings store for local development / MVP (no connection string configured
/// yet — same "fallback to in-memory" convention as <c>InMemoryTelemetryQueryCache</c>). Findings
/// are lost on restart; a future <c>EfFindingsRepository</c> can implement the same interface
/// without touching any caller.
/// </summary>
public sealed class InMemoryFindingsRepository : IFindingsRepository
{
    private readonly ConcurrentDictionary<Guid, Finding> _byId = new();

    public Task<Finding> AddAsync(Finding finding, CancellationToken ct = default)
    {
        _byId[finding.Id] = finding;
        return Task.FromResult(finding);
    }

    public Task<Finding> UpdateAsync(Finding finding, CancellationToken ct = default)
    {
        _byId[finding.Id] = finding;
        return Task.FromResult(finding);
    }

    public Task<Finding?> GetOpenByCorrelationIdAsync(string correlationId, CancellationToken ct = default)
    {
        var match = _byId.Values.FirstOrDefault(f => f.CorrelationId == correlationId && f.Status == "open");
        return Task.FromResult(match);
    }

    public Task<IReadOnlyList<Finding>> GetOpenByDomainAsync(string domain, CancellationToken ct = default)
    {
        IReadOnlyList<Finding> matches = _byId.Values
            .Where(f => f.Domain == domain && f.Status == "open")
            .ToList();
        return Task.FromResult(matches);
    }

    public Task<IReadOnlyList<Finding>> GetAllAsync(CancellationToken ct = default)
    {
        IReadOnlyList<Finding> all = _byId.Values.OrderByDescending(f => f.CreatedAt).ToList();
        return Task.FromResult(all);
    }

    public Task<Finding?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        _byId.TryGetValue(id, out var finding);
        return Task.FromResult(finding);
    }
}
