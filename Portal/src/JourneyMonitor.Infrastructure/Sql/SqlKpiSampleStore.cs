using System.Data;
using System.Text.Json;
using Dapper;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Options;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Infrastructure.Sql;

/// <summary>monitoring.KpiSample: batched inserts through usp_AddKpiSamples, reads and retention purge.</summary>
public sealed class SqlKpiSampleStore(IOptions<JourneyStoreOptions> storeOptions) : IKpiSampleStore
{
    public async Task AddAsync(IReadOnlyList<KpiSample> samples, CancellationToken ct = default)
    {
        var payloads = BuildBatchPayloads(samples);
        if (payloads.Count == 0)
        {
            return;
        }

        await using var connection = SqlKpiConnection.Open(storeOptions);
        foreach (var payload in payloads)
        {
            var parameters = new DynamicParameters();
            parameters.Add("SamplesJson", payload, DbType.String, size: -1);
            await connection.ExecuteAsync(new CommandDefinition(
                "monitoring.usp_AddKpiSamples", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));
        }
    }

    /// <summary>The exact @SamplesJson values AddAsync sends: camelCase JSON arrays of at most 1000 samples each.</summary>
    public static IReadOnlyList<string> BuildBatchPayloads(IReadOnlyList<KpiSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        KpiValueGuard.EnsureFinite(samples, nameof(samples));
        return samples
            .Chunk(SqlKpiConnection.BatchSize)
            .Select(batch => JsonSerializer.Serialize(batch, SqlKpiConnection.BatchJsonOptions))
            .ToList();
    }

    public async Task<IReadOnlyList<KpiSample>> GetAsync(int kpiDefinitionId, DateTimeOffset bucketStart, int top, CancellationToken ct = default)
    {
        var parameters = new DynamicParameters();
        parameters.Add("KpiDefinitionId", kpiDefinitionId, DbType.Int32);
        parameters.Add("BucketStart", bucketStart, DbType.DateTimeOffset);
        parameters.Add("Top", top, DbType.Int32);

        await using var connection = SqlKpiConnection.Open(storeOptions);
        var rows = await connection.QueryAsync<SampleRow>(new CommandDefinition(
            "monitoring.usp_GetKpiSamples", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));
        return rows.Select(r => new KpiSample(r.SampleId, r.KpiDefinitionId, r.BucketStart, r.Timestamp, r.Value, r.OperationId)).ToList();
    }

    public async Task<int> PurgeOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        var parameters = new DynamicParameters();
        parameters.Add("Cutoff", cutoff, DbType.DateTimeOffset);

        await using var connection = SqlKpiConnection.Open(storeOptions);
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "monitoring.usp_PurgeKpiSamples", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));
    }

    private sealed class SampleRow
    {
        public long SampleId { get; set; }
        public int KpiDefinitionId { get; set; }
        public DateTimeOffset BucketStart { get; set; }
        public DateTimeOffset Timestamp { get; set; }
        public double Value { get; set; }
        public string? OperationId { get; set; }
    }
}
