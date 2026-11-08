using JourneyMonitor.Core.Domain;

namespace JourneyMonitor.Core.Abstractions;

public interface IKpiDefinitionStore
{
    /// <summary>Every definition of every profile.</summary>
    Task<IReadOnlyList<KpiDefinition>> GetAllAsync(CancellationToken ct);
    /// <summary>Enabled definitions of every profile.</summary>
    Task<IReadOnlyList<KpiDefinition>> GetEnabledAsync(KpiPillar? pillar, CancellationToken ct);

    /// <summary>Definitions of one profile; null means every profile.</summary>
    async Task<IReadOnlyList<KpiDefinition>> GetAllAsync(string? profileKey = null, CancellationToken ct = default) =>
        KpiProfileFilter.Apply(await GetAllAsync(ct).ConfigureAwait(false), profileKey);

    /// <summary>Enabled definitions of one profile; null means every profile.</summary>
    async Task<IReadOnlyList<KpiDefinition>> GetEnabledAsync(KpiPillar? pillar, string? profileKey = null, CancellationToken ct = default) =>
        KpiProfileFilter.Apply(await GetEnabledAsync(pillar, ct).ConfigureAwait(false), profileKey);

    Task<KpiDefinition?> GetAsync(int kpiDefinitionId, CancellationToken ct = default);
    /// <summary>Insert when KpiDefinitionId == 0, otherwise update. Always appends an audit row. Returns the stored row.</summary>
    Task<KpiDefinition> UpsertAsync(KpiDefinition definition, string changedBy, CancellationToken ct = default);
    Task<IReadOnlyList<KpiDefinitionAuditEntry>> GetAuditAsync(int kpiDefinitionId, int top, CancellationToken ct = default);
}

public interface IKpiBucketStore
{
    /// <summary>Insert or replace by (KpiDefinitionId, EntityKey, BucketStart).</summary>
    Task UpsertAsync(IReadOnlyList<KpiBucket> buckets, CancellationToken ct = default);
    /// <summary>Half-open range [from, to) of the domain aggregate (EntityKey ""), ordered by BucketStart ascending.</summary>
    Task<IReadOnlyList<KpiBucket>> GetRangeAsync(int kpiDefinitionId, DateTimeOffset fromInclusive, DateTimeOffset toExclusive, CancellationToken ct = default);

    /// <summary>
    /// Contract v4 §1: range of one series; entityKey null or "" is the domain aggregate. The default
    /// keeps v3 behaviour - a store without entity rows has no series other than "".
    /// </summary>
    async Task<IReadOnlyList<KpiBucket>> GetRangeAsync(int kpiDefinitionId, DateTimeOffset fromInclusive, DateTimeOffset toExclusive, string? entityKey, CancellationToken ct = default) =>
        string.IsNullOrEmpty(entityKey)
            ? await GetRangeAsync(kpiDefinitionId, fromInclusive, toExclusive, ct).ConfigureAwait(false)
            : [];

    /// <summary>Contract v4 §1: every entity series (EntityKey != "") in [from, to), ordered by EntityKey then BucketStart. Default: none.</summary>
    Task<IReadOnlyList<KpiBucket>> GetEntityRangeAsync(int kpiDefinitionId, DateTimeOffset fromInclusive, DateTimeOffset toExclusive, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<KpiBucket>>([]);
}

public interface IKpiSampleStore
{
    Task AddAsync(IReadOnlyList<KpiSample> samples, CancellationToken ct = default);
    Task<IReadOnlyList<KpiSample>> GetAsync(int kpiDefinitionId, DateTimeOffset bucketStart, int top, CancellationToken ct = default);
    Task<int> PurgeOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default);
}

public interface IKpiStateStore
{
    /// <summary>State of the domain aggregate (EntityKey ""). Returns KpiDetectionState.Initial(id) when no row exists.</summary>
    Task<KpiDetectionState> GetAsync(int kpiDefinitionId, CancellationToken ct = default);
    /// <summary>Upsert by (KpiDefinitionId, EntityKey).</summary>
    Task SaveAsync(KpiDetectionState state, CancellationToken ct = default);

    /// <summary>
    /// Contract v4 §1: state of one series; entityKey null or "" is the domain aggregate. Returns
    /// KpiDetectionState.Initial(id, entityKey) when no row exists, which is the default for any entity.
    /// </summary>
    async Task<KpiDetectionState> GetAsync(int kpiDefinitionId, string? entityKey, CancellationToken ct = default) =>
        string.IsNullOrEmpty(entityKey)
            ? await GetAsync(kpiDefinitionId, ct).ConfigureAwait(false)
            : KpiDetectionState.Initial(kpiDefinitionId, entityKey);

    /// <summary>Contract v4 §1: every stored entity state (EntityKey != "") of the definition. Default: none.</summary>
    Task<IReadOnlyList<KpiDetectionState>> GetEntityStatesAsync(int kpiDefinitionId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<KpiDetectionState>>([]);
}

public interface IKpiAnomalyStore
{
    /// <summary>Returns the anomaly with its generated AnomalyId.</summary>
    Task<KpiAnomaly> AddAsync(KpiAnomaly anomaly, CancellationToken ct = default);
    Task AttachInvestigationAsync(long anomalyId, Guid investigationId, CancellationToken ct = default);
    /// <summary>Newest first. profileKey filters on the definition's profile; null means every profile.</summary>
    Task<IReadOnlyList<KpiAnomaly>> GetRecentAsync(KpiPillar? pillar, string? domain, int top, string? profileKey = null, CancellationToken ct = default);

    /// <summary>
    /// Contract v4 §1: newest first, restricted to one series; entityKey null or "" is the domain
    /// aggregate. The default filters the v3 read, so a store without entity rows returns only "" anomalies.
    /// </summary>
    async Task<IReadOnlyList<KpiAnomaly>> GetRecentAsync(KpiPillar? pillar, string? domain, int top, string? profileKey, string? entityKey, CancellationToken ct = default)
    {
        var key = entityKey ?? "";
        var recent = await GetRecentAsync(pillar, domain, top, profileKey, ct).ConfigureAwait(false);
        return recent.Where(a => string.Equals(a.EntityKey, key, StringComparison.Ordinal)).ToList();
    }
}

public interface IIngestionWatermarkStore
{
    Task<DateTimeOffset?> GetAsync(int kpiDefinitionId, CancellationToken ct = default);
    Task SetAsync(int kpiDefinitionId, DateTimeOffset watermark, CancellationToken ct = default);
}

public interface IKpiSource
{
    bool CanHandle(string sourceKey);
    /// <summary>Returns bucket totals for [from, to) plus up to samplesPerBucket raw samples per bucket (0 = none). Throws KpiSourceUnavailableException when the source is not configured.</summary>
    Task<KpiSourceResult> FetchAsync(KpiDefinition definition, DateTimeOffset fromInclusive, DateTimeOffset toExclusive, int samplesPerBucket, CancellationToken ct = default);
}

public interface IKpiEvaluator
{
    /// <summary>How much history before the first evaluated bucket the evaluator needs.</summary>
    TimeSpan GetRequiredHistory(KpiDefinition definition);

    /// <summary>Pure and deterministic. Evaluates bucketsToEvaluate in ascending order, threading the state through; history holds earlier buckets.</summary>
    IReadOnlyList<KpiEvaluation> Evaluate(KpiDefinition definition, KpiDetectionState state, IReadOnlyList<KpiBucket> history, IReadOnlyList<KpiBucket> bucketsToEvaluate);
}

public interface IKpiIngestionRunner
{
    Task<KpiIngestionRunResult> RunAsync(CancellationToken ct = default);
}

public interface IPillarHealthMonitor
{
    Task<PillarMonitorRunResult> RunAsync(KpiPillar pillar, CancellationToken ct = default);
}

public interface IPillarHealthReader
{
    /// <summary>profileKey null means every profile.</summary>
    Task<IReadOnlyList<PillarHealth>> GetAsync(string? domain, string? profileKey = null, CancellationToken ct = default);
}

public static class KpiProfileFilter
{
    /// <summary>Keeps the definitions of profileKey (case-insensitive); null or blank keeps all.</summary>
    public static IReadOnlyList<KpiDefinition> Apply(IReadOnlyList<KpiDefinition> definitions, string? profileKey) =>
        string.IsNullOrWhiteSpace(profileKey)
            ? definitions
            : definitions.Where(d => string.Equals(d.ProfileKey, profileKey.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
}
