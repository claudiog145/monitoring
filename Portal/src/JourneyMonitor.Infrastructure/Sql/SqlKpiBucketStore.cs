using System.Data;
using System.Text.Json;
using Dapper;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Options;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Infrastructure.Sql;

/// <summary>
/// monitoring.KpiBucket: batched MERGE through usp_UpsertKpiBuckets keyed by (KpiDefinitionId, EntityKey,
/// BucketStart), half-open range reads per series. Entity buckets (EntityKey != "") are kept
/// <see cref="EntityRetention"/>; the upsert procedure purges older ones of the definitions it writes.
/// </summary>
public sealed class SqlKpiBucketStore(IOptions<JourneyStoreOptions> storeOptions) : IKpiBucketStore
{
    /// <summary>Contract v4 section 1: how long entity-grain buckets are kept. Domain buckets are not purged.</summary>
    public static readonly TimeSpan EntityRetention = TimeSpan.FromDays(8);

    public async Task UpsertAsync(IReadOnlyList<KpiBucket> buckets, CancellationToken ct = default)
    {
        var payloads = BuildBatchPayloads(buckets);
        if (payloads.Count == 0)
        {
            return;
        }

        await using var connection = SqlKpiConnection.Open(storeOptions);
        foreach (var payload in payloads)
        {
            var parameters = new DynamicParameters();
            parameters.Add("BucketsJson", payload, DbType.String, size: -1);
            await connection.ExecuteAsync(new CommandDefinition(
                "monitoring.usp_UpsertKpiBuckets", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));
        }
    }

    /// <summary>
    /// The exact @BucketsJson values UpsertAsync sends, one per procedure call: entity keys trimmed, duplicates
    /// collapsed by (KpiDefinitionId, EntityKey, BucketStart) with the last one winning - MERGE fails on duplicate source
    /// keys; EntityKey compares case-insensitively like the column - then split into camelCase JSON arrays of
    /// at most 1000 buckets.
    /// </summary>
    public static IReadOnlyList<string> BuildBatchPayloads(IReadOnlyList<KpiBucket> buckets)
    {
        ArgumentNullException.ThrowIfNull(buckets);
        KpiValueGuard.EnsureFinite(buckets, nameof(buckets));
        KpiValueGuard.EnsureEntityKeys(buckets.Select(b => b.EntityKey), nameof(buckets));
        return Deduplicate(buckets.Select(b => b with { EntityKey = KpiValueGuard.NormalizeEntityKey(b.EntityKey) }).ToList())
            .Chunk(SqlKpiConnection.BatchSize)
            .Select(batch => JsonSerializer.Serialize(batch, SqlKpiConnection.BatchJsonOptions))
            .ToList();
    }

    public Task<IReadOnlyList<KpiBucket>> GetRangeAsync(int kpiDefinitionId, DateTimeOffset fromInclusive, DateTimeOffset toExclusive, CancellationToken ct = default) =>
        GetRangeAsync(kpiDefinitionId, fromInclusive, toExclusive, null, ct);

    public async Task<IReadOnlyList<KpiBucket>> GetRangeAsync(int kpiDefinitionId, DateTimeOffset fromInclusive, DateTimeOffset toExclusive, string? entityKey, CancellationToken ct = default)
    {
        var parameters = RangeParameters(kpiDefinitionId, fromInclusive, toExclusive);
        parameters.Add("EntityKey", KpiValueGuard.NormalizeEntityKey(entityKey), DbType.String, size: KpiValueGuard.MaxEntityKeyLength);
        return await QueryAsync("monitoring.usp_GetKpiBuckets", parameters, ct);
    }

    public async Task<IReadOnlyList<KpiBucket>> GetEntityRangeAsync(int kpiDefinitionId, DateTimeOffset fromInclusive, DateTimeOffset toExclusive, CancellationToken ct = default) =>
        await QueryAsync("monitoring.usp_GetKpiEntityBuckets", RangeParameters(kpiDefinitionId, fromInclusive, toExclusive), ct);

    private static DynamicParameters RangeParameters(int kpiDefinitionId, DateTimeOffset fromInclusive, DateTimeOffset toExclusive)
    {
        var parameters = new DynamicParameters();
        parameters.Add("KpiDefinitionId", kpiDefinitionId, DbType.Int32);
        parameters.Add("From", fromInclusive, DbType.DateTimeOffset);
        parameters.Add("To", toExclusive, DbType.DateTimeOffset);
        return parameters;
    }

    private async Task<IReadOnlyList<KpiBucket>> QueryAsync(string procedure, DynamicParameters parameters, CancellationToken ct)
    {
        await using var connection = SqlKpiConnection.Open(storeOptions);
        var rows = await connection.QueryAsync<BucketRow>(new CommandDefinition(
            procedure, parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));
        return rows.Select(r => new KpiBucket(r.KpiDefinitionId, r.BucketStart, r.TotalCount, r.SampledCount, r.P50, r.P95, r.Mean, r.StdDev, r.Value)
        {
            EntityKey = r.EntityKey ?? "",
            FailedCount = r.FailedCount,
        }).ToList();
    }

    private static List<KpiBucket> Deduplicate(IReadOnlyList<KpiBucket> buckets)
    {
        var order = new List<BucketKey>();
        var latest = new Dictionary<BucketKey, KpiBucket>();
        foreach (var bucket in buckets)
        {
            var key = new BucketKey(bucket.KpiDefinitionId, bucket.EntityKey.ToUpperInvariant(), bucket.BucketStart);
            if (!latest.ContainsKey(key))
            {
                order.Add(key);
            }
            latest[key] = bucket;
        }
        return order.Select(k => latest[k]).ToList();
    }

    private readonly record struct BucketKey(int KpiDefinitionId, string EntityKey, DateTimeOffset BucketStart);

    private sealed class BucketRow
    {
        public int KpiDefinitionId { get; set; }
        public DateTimeOffset BucketStart { get; set; }
        public long TotalCount { get; set; }
        public int SampledCount { get; set; }
        public double? P50 { get; set; }
        public double? P95 { get; set; }
        public double? Mean { get; set; }
        public double? StdDev { get; set; }
        public double? Value { get; set; }
        public string? EntityKey { get; set; }
        public long? FailedCount { get; set; }
    }
}
