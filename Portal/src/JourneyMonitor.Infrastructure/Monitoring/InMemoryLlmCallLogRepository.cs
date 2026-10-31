using System.Collections.Concurrent;
using JourneyMonitor.Core.Abstractions;

namespace JourneyMonitor.Infrastructure.Monitoring;

/// <summary>Process-local LlmCallLog store for local development / MVP - see InMemoryFindingsRepository remarks.</summary>
public sealed class InMemoryLlmCallLogRepository : ILlmCallLogRepository
{
    private readonly ConcurrentDictionary<Guid, LlmCallLog> _byDoctorReportId = new();

    public Task<LlmCallLog> AddAsync(LlmCallLog log, CancellationToken ct = default)
    {
        _byDoctorReportId[log.DoctorReportId] = log;
        return Task.FromResult(log);
    }

    public Task<LlmCallLog?> GetByDoctorReportIdAsync(Guid doctorReportId, CancellationToken ct = default)
    {
        _byDoctorReportId.TryGetValue(doctorReportId, out var log);
        return Task.FromResult(log);
    }
}
