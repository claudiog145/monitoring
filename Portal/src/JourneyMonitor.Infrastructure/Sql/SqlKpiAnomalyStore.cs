using System.Data;
using Dapper;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Options;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Infrastructure.Sql;

/// <summary>
/// monitoring.KpiAnomaly: append, attach the triage investigation, newest-first reads filtered through
/// KpiDefinition. Reads are per series: the v3 overload returns only domain anomalies (EntityKey ""), and
/// the series filter is applied before TOP so entity rows never crowd domain rows out (or vice versa).
/// </summary>
public sealed class SqlKpiAnomalyStore(IOptions<JourneyStoreOptions> storeOptions) : IKpiAnomalyStore
{
    public async Task<KpiAnomaly> AddAsync(KpiAnomaly anomaly, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(anomaly);
        KpiValueGuard.EnsureEntityKey(anomaly.EntityKey, nameof(anomaly));
        anomaly = anomaly with { EntityKey = KpiValueGuard.NormalizeEntityKey(anomaly.EntityKey) };
        var parameters = new DynamicParameters();
        parameters.Add("KpiDefinitionId", anomaly.KpiDefinitionId, DbType.Int32);
        parameters.Add("DetectedAt", anomaly.DetectedAt, DbType.DateTimeOffset);
        parameters.Add("BucketStart", anomaly.BucketStart, DbType.DateTimeOffset);
        parameters.Add("ObservedValue", anomaly.ObservedValue, DbType.Double);
        parameters.Add("ReferenceValue", anomaly.ReferenceValue, DbType.Double);
        parameters.Add("Deviation", anomaly.Deviation, DbType.Double);
        parameters.Add("Detection", anomaly.Detection, DbType.String, size: 400);
        parameters.Add("InvestigationId", anomaly.InvestigationId, DbType.Guid);
        parameters.Add("EntityKey", anomaly.EntityKey, DbType.String, size: KpiValueGuard.MaxEntityKeyLength);

        await using var connection = SqlKpiConnection.Open(storeOptions);
        var id = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "monitoring.usp_AddKpiAnomaly", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));
        return anomaly with { AnomalyId = id };
    }

    public async Task AttachInvestigationAsync(long anomalyId, Guid investigationId, CancellationToken ct = default)
    {
        var parameters = new DynamicParameters();
        parameters.Add("AnomalyId", anomalyId, DbType.Int64);
        parameters.Add("InvestigationId", investigationId, DbType.Guid);

        await using var connection = SqlKpiConnection.Open(storeOptions);
        await connection.ExecuteAsync(new CommandDefinition(
            "monitoring.usp_AttachKpiAnomalyInvestigation", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));
    }

    public Task<IReadOnlyList<KpiAnomaly>> GetRecentAsync(KpiPillar? pillar, string? domain, int top, string? profileKey = null, CancellationToken ct = default) =>
        GetRecentAsync(pillar, domain, top, profileKey, null, ct);

    public async Task<IReadOnlyList<KpiAnomaly>> GetRecentAsync(KpiPillar? pillar, string? domain, int top, string? profileKey, string? entityKey, CancellationToken ct = default)
    {
        var parameters = new DynamicParameters();
        parameters.Add("Pillar", pillar?.ToString(), DbType.String, size: 16);
        parameters.Add("Domain", domain, DbType.String, size: 32);
        parameters.Add("Top", top, DbType.Int32);
        parameters.Add("ProfileKey", string.IsNullOrWhiteSpace(profileKey) ? null : profileKey.Trim(), DbType.String, size: 128);
        parameters.Add("EntityKey", KpiValueGuard.NormalizeEntityKey(entityKey), DbType.String, size: KpiValueGuard.MaxEntityKeyLength);

        await using var connection = SqlKpiConnection.Open(storeOptions);
        var rows = await connection.QueryAsync<AnomalyRow>(new CommandDefinition(
            "monitoring.usp_GetRecentKpiAnomalies", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));
        return rows.Select(r => new KpiAnomaly(
            r.AnomalyId, r.KpiDefinitionId, r.DetectedAt, r.BucketStart, r.ObservedValue, r.ReferenceValue, r.Deviation, r.Detection, r.InvestigationId)
        {
            EntityKey = r.EntityKey ?? "",
        }).ToList();
    }

    private sealed class AnomalyRow
    {
        public long AnomalyId { get; set; }
        public int KpiDefinitionId { get; set; }
        public DateTimeOffset DetectedAt { get; set; }
        public DateTimeOffset BucketStart { get; set; }
        public double ObservedValue { get; set; }
        public double ReferenceValue { get; set; }
        public double Deviation { get; set; }
        public string Detection { get; set; } = "";
        public Guid? InvestigationId { get; set; }
        public string? EntityKey { get; set; }
    }
}
