using System.Collections.Concurrent;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;

namespace JourneyMonitor.Infrastructure.Monitoring;

/// <summary>Process-local RCA store for a clone with no database. Same replace-on-save grain as the SQL one.</summary>
public sealed class InMemoryRcaReportRepository : IRcaReportRepository
{
    private readonly ConcurrentDictionary<Guid, RcaReport> _byCase = new();

    public Task<RcaReport> SaveAsync(RcaReport report, CancellationToken ct = default)
    {
        _byCase[report.ForensicCaseId] = report;
        return Task.FromResult(report);
    }

    public Task<RcaReport?> GetByForensicCaseIdAsync(Guid forensicCaseId, CancellationToken ct = default) =>
        Task.FromResult(_byCase.TryGetValue(forensicCaseId, out var report) ? report : null);

    public Task<IReadOnlyList<RcaReport>> GetRecentAsync(int top, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<RcaReport>>(
            _byCase.Values.OrderByDescending(r => r.GeneratedAt).Take(top).ToList());
}
