using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Infrastructure.Sql;

namespace JourneyMonitor.Infrastructure.Monitoring;

/// <summary>Process-local raw samples with incremental SampleIds, read by bucket ordered by Timestamp.</summary>
public sealed class InMemoryKpiSampleStore : IKpiSampleStore
{
    private readonly object _gate = new();
    private readonly List<KpiSample> _samples = [];
    private long _nextId;

    public Task AddAsync(IReadOnlyList<KpiSample> samples, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(samples);
        KpiValueGuard.EnsureFinite(samples, nameof(samples));
        lock (_gate)
        {
            foreach (var sample in samples)
            {
                _samples.Add(sample with { SampleId = ++_nextId });
            }
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<KpiSample>> GetAsync(int kpiDefinitionId, DateTimeOffset bucketStart, int top, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<KpiSample>>(_samples
                .Where(s => s.KpiDefinitionId == kpiDefinitionId && s.BucketStart == bucketStart)
                .OrderBy(s => s.Timestamp)
                .ThenBy(s => s.SampleId)
                .Take(Math.Max(top, 0))
                .ToList());
        }
    }

    public Task<int> PurgeOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_samples.RemoveAll(s => s.BucketStart < cutoff));
        }
    }
}
