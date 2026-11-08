using System.Data;
using Dapper;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Options;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Infrastructure.Sql;

/// <summary>
/// monitoring.KpiDefinition / KpiDefinitionAudit over the usp_* procedures of CreateMonitoringKpi.sql, in
/// their contract v4 versions from CreateMonitoringScoutShadow.sql (Grain, MaxEntitiesPerBucket).
/// </summary>
public sealed class SqlKpiDefinitionStore(IOptions<JourneyStoreOptions> storeOptions) : IKpiDefinitionStore
{
    public Task<IReadOnlyList<KpiDefinition>> GetAllAsync(CancellationToken ct) =>
        QueryDefinitionsAsync(null, onlyEnabled: false, null, ct);

    public Task<IReadOnlyList<KpiDefinition>> GetAllAsync(string? profileKey = null, CancellationToken ct = default) =>
        QueryDefinitionsAsync(null, onlyEnabled: false, profileKey, ct);

    public Task<IReadOnlyList<KpiDefinition>> GetEnabledAsync(KpiPillar? pillar, CancellationToken ct) =>
        QueryDefinitionsAsync(pillar, onlyEnabled: true, null, ct);

    public Task<IReadOnlyList<KpiDefinition>> GetEnabledAsync(KpiPillar? pillar, string? profileKey = null, CancellationToken ct = default) =>
        QueryDefinitionsAsync(pillar, onlyEnabled: true, profileKey, ct);

    public async Task<KpiDefinition?> GetAsync(int kpiDefinitionId, CancellationToken ct = default)
    {
        var parameters = new DynamicParameters();
        parameters.Add("KpiDefinitionId", kpiDefinitionId, DbType.Int32);

        await using var connection = SqlKpiConnection.Open(storeOptions);
        var row = await connection.QuerySingleOrDefaultAsync<KpiDefinitionRow>(new CommandDefinition(
            "monitoring.usp_GetKpiDefinition", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));
        return row?.ToDomain();
    }

    public async Task<KpiDefinition> UpsertAsync(KpiDefinition definition, string changedBy, CancellationToken ct = default)
    {
        var parameters = new DynamicParameters();
        parameters.Add("KpiDefinitionId", definition.KpiDefinitionId, DbType.Int32);
        parameters.Add("Pillar", definition.Pillar.ToString(), DbType.String, size: 16);
        parameters.Add("Kpi", definition.Kpi, DbType.String, size: 64);
        parameters.Add("Domain", definition.Domain, DbType.String, size: 32);
        parameters.Add("Stage", definition.Stage ?? "", DbType.String, size: 64);
        parameters.Add("SourceKey", definition.SourceKey, DbType.String, size: 64);
        parameters.Add("Direction", definition.Direction.ToString(), DbType.String, size: 16);
        parameters.Add("Aggregation", definition.Aggregation.ToString(), DbType.String, size: 8);
        parameters.Add("EvaluationFrequencySec", ToSeconds(definition.EvaluationFrequency), DbType.Int32);
        parameters.Add("BucketSizeSec", ToSeconds(definition.BucketSize), DbType.Int32);
        parameters.Add("LookbackWindowSec", ToSeconds(definition.LookbackWindow), DbType.Int32);
        parameters.Add("SampleSize", definition.SampleSize, DbType.Int32);
        parameters.Add("MinObservationsPerBucket", definition.MinObservationsPerBucket, DbType.Int32);
        parameters.Add("DetectionMethod", definition.DetectionMethod.ToString(), DbType.String, size: 32);
        parameters.Add("MethodParamsJson", definition.MethodParamsJson, DbType.String, size: 1000);
        parameters.Add("ConsecutiveCount", definition.ConsecutiveCount, DbType.Int32);
        parameters.Add("CooldownMinutes", definition.CooldownMinutes, DbType.Int32);
        parameters.Add("Enabled", definition.Enabled, DbType.Boolean);
        parameters.Add("ChangedBy", changedBy, DbType.String, size: 128);
        parameters.Add("ProfileKey", definition.ProfileKey.Trim(), DbType.String, size: 128);
        parameters.Add("Grain", ToSql(definition.Grain), DbType.String, size: 16);
        parameters.Add("MaxEntitiesPerBucket", definition.MaxEntitiesPerBucket, DbType.Int32);

        await using var connection = SqlKpiConnection.Open(storeOptions);
        var row = await connection.QuerySingleAsync<KpiDefinitionRow>(new CommandDefinition(
            "monitoring.usp_UpsertKpiDefinition", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));
        return row.ToDomain();
    }

    public async Task<IReadOnlyList<KpiDefinitionAuditEntry>> GetAuditAsync(int kpiDefinitionId, int top, CancellationToken ct = default)
    {
        var parameters = new DynamicParameters();
        parameters.Add("KpiDefinitionId", kpiDefinitionId, DbType.Int32);
        parameters.Add("Top", top, DbType.Int32);

        await using var connection = SqlKpiConnection.Open(storeOptions);
        var rows = await connection.QueryAsync<AuditRow>(new CommandDefinition(
            "monitoring.usp_GetKpiDefinitionAudit", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));
        return rows.Select(r => new KpiDefinitionAuditEntry(r.AuditId, r.KpiDefinitionId, r.SnapshotJson, r.ChangedAt, r.ChangedBy)).ToList();
    }

    private async Task<IReadOnlyList<KpiDefinition>> QueryDefinitionsAsync(KpiPillar? pillar, bool onlyEnabled, string? profileKey, CancellationToken ct)
    {
        var parameters = new DynamicParameters();
        parameters.Add("Pillar", pillar?.ToString(), DbType.String, size: 16);
        parameters.Add("OnlyEnabled", onlyEnabled, DbType.Boolean);
        parameters.Add("ProfileKey", string.IsNullOrWhiteSpace(profileKey) ? null : profileKey.Trim(), DbType.String, size: 128);

        await using var connection = SqlKpiConnection.Open(storeOptions);
        var rows = await connection.QueryAsync<KpiDefinitionRow>(new CommandDefinition(
            "monitoring.usp_GetKpiDefinitions", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));
        return rows.Select(r => r.ToDomain()).ToList();
    }

    private static int ToSeconds(TimeSpan value) => checked((int)value.TotalSeconds);

    /// <summary>KpiDefinition.Grain stores the contract's JSON spelling: domain | entity.</summary>
    internal static string ToSql(KpiGrain grain) => grain.ToString().ToLowerInvariant();

    internal static KpiGrain ParseGrain(string? value) =>
        string.IsNullOrWhiteSpace(value) ? KpiGrain.Domain : Enum.Parse<KpiGrain>(value, ignoreCase: true);

    private sealed class KpiDefinitionRow
    {
        public int KpiDefinitionId { get; set; }
        public string Pillar { get; set; } = "";
        public string Kpi { get; set; } = "";
        public string Domain { get; set; } = "";
        public string? Stage { get; set; }
        public string SourceKey { get; set; } = "";
        public string Direction { get; set; } = "";
        public string Aggregation { get; set; } = "";
        public int EvaluationFrequencySec { get; set; }
        public int BucketSizeSec { get; set; }
        public int LookbackWindowSec { get; set; }
        public int SampleSize { get; set; }
        public int MinObservationsPerBucket { get; set; }
        public string DetectionMethod { get; set; } = "";
        public string MethodParamsJson { get; set; } = "{}";
        public int ConsecutiveCount { get; set; }
        public int CooldownMinutes { get; set; }
        public bool Enabled { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public string? UpdatedBy { get; set; }
        public string? ProfileKey { get; set; }
        public string? Grain { get; set; }
        public int? MaxEntitiesPerBucket { get; set; }

        public KpiDefinition ToDomain() => new(
            KpiDefinitionId,
            Enum.Parse<KpiPillar>(Pillar),
            Kpi,
            Domain,
            Stage ?? "",
            SourceKey,
            Enum.Parse<KpiDirection>(Direction),
            Enum.Parse<KpiAggregation>(Aggregation),
            TimeSpan.FromSeconds(EvaluationFrequencySec),
            TimeSpan.FromSeconds(BucketSizeSec),
            TimeSpan.FromSeconds(LookbackWindowSec),
            SampleSize,
            MinObservationsPerBucket,
            Enum.Parse<DetectionMethodKind>(DetectionMethod),
            MethodParamsJson,
            ConsecutiveCount,
            CooldownMinutes,
            Enabled,
            UpdatedAt,
            UpdatedBy ?? "")
        {
            ProfileKey = string.IsNullOrWhiteSpace(ProfileKey) ? KpiProfiles.Default : ProfileKey,
            Grain = ParseGrain(Grain),
            MaxEntitiesPerBucket = MaxEntitiesPerBucket ?? 500,
        };
    }

    private sealed class AuditRow
    {
        public long AuditId { get; set; }
        public int KpiDefinitionId { get; set; }
        public string SnapshotJson { get; set; } = "";
        public DateTimeOffset ChangedAt { get; set; }
        public string ChangedBy { get; set; } = "";
    }
}

/// <summary>Shared connection/JSON helpers for the Sql*Kpi* stores.</summary>
internal static class SqlKpiConnection
{
    internal const int BatchSize = 1000;

    internal static readonly System.Text.Json.JsonSerializerOptions BatchJsonOptions = new(System.Text.Json.JsonSerializerDefaults.Web);

    internal static SqlConnection Open(IOptions<JourneyStoreOptions> storeOptions)
    {
        var options = storeOptions.Value;
        if (!options.IsConfigured)
        {
            throw new InvalidOperationException($"{JourneyStoreOptions.SectionName}:ConnectionString is not configured.");
        }
        return new SqlConnection(options.ConnectionString);
    }
}
