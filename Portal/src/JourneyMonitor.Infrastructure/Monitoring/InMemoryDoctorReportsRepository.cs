using System.Collections.Concurrent;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;

namespace JourneyMonitor.Infrastructure.Monitoring;

/// <summary>Process-local DoctorReports store for local development / MVP — see InMemoryFindingsRepository remarks.</summary>
public sealed class InMemoryDoctorReportsRepository : IDoctorReportsRepository
{
    private readonly ConcurrentDictionary<Guid, DoctorReport> _byId = new();

    public Task<DoctorReport> AddAsync(DoctorReport report, CancellationToken ct = default)
    {
        _byId[report.Id] = report;
        return Task.FromResult(report);
    }

    public Task<DoctorReport?> GetByFindingIdAsync(Guid findingId, CancellationToken ct = default)
    {
        var match = _byId.Values
            .Where(r => r.FindingId == findingId)
            .OrderByDescending(r => r.GeneratedAt)
            .FirstOrDefault();
        return Task.FromResult(match);
    }

    public Task<IReadOnlyList<DoctorReport>> GetAllAsync(CancellationToken ct = default)
    {
        IReadOnlyList<DoctorReport> all = _byId.Values.OrderByDescending(r => r.GeneratedAt).ToList();
        return Task.FromResult(all);
    }
}
