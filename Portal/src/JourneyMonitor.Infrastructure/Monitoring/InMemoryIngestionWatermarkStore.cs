using System.Collections.Concurrent;
using JourneyMonitor.Core.Abstractions;

namespace JourneyMonitor.Infrastructure.Monitoring;

/// <summary>Process-local ingestion watermarks. The watermark only moves forward: an older value is ignored.</summary>
public sealed class InMemoryIngestionWatermarkStore : IIngestionWatermarkStore
{
    private readonly ConcurrentDictionary<int, DateTimeOffset> _watermarks = new();

    public Task<DateTimeOffset?> GetAsync(int kpiDefinitionId, CancellationToken ct = default) =>
        Task.FromResult(_watermarks.TryGetValue(kpiDefinitionId, out var value) ? value : (DateTimeOffset?)null);

    public Task SetAsync(int kpiDefinitionId, DateTimeOffset watermark, CancellationToken ct = default)
    {
        _watermarks.AddOrUpdate(kpiDefinitionId, watermark, (_, current) => watermark > current ? watermark : current);
        return Task.CompletedTask;
    }
}
