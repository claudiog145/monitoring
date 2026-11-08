using System.Data;
using Dapper;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Options;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Infrastructure.Sql;

/// <summary>monitoring.KpiDetectionState: one row per (KPI, EntityKey); a missing row is the initial state.</summary>
public sealed class SqlKpiStateStore(IOptions<JourneyStoreOptions> storeOptions) : IKpiStateStore
{
    public Task<KpiDetectionState> GetAsync(int kpiDefinitionId, CancellationToken ct = default) =>
        GetAsync(kpiDefinitionId, null, ct);

    public async Task<KpiDetectionState> GetAsync(int kpiDefinitionId, string? entityKey, CancellationToken ct = default)
    {
        var key = KpiValueGuard.NormalizeEntityKey(entityKey);
        var parameters = new DynamicParameters();
        parameters.Add("KpiDefinitionId", kpiDefinitionId, DbType.Int32);
        parameters.Add("EntityKey", key, DbType.String, size: KpiValueGuard.MaxEntityKeyLength);

        await using var connection = SqlKpiConnection.Open(storeOptions);
        var row = await connection.QuerySingleOrDefaultAsync<StateRow>(new CommandDefinition(
            "monitoring.usp_GetKpiDetectionState", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));
        return row is null ? KpiDetectionState.Initial(kpiDefinitionId, key) : row.ToDomain();
    }

    public async Task<IReadOnlyList<KpiDetectionState>> GetEntityStatesAsync(int kpiDefinitionId, CancellationToken ct = default)
    {
        var parameters = new DynamicParameters();
        parameters.Add("KpiDefinitionId", kpiDefinitionId, DbType.Int32);

        await using var connection = SqlKpiConnection.Open(storeOptions);
        var rows = await connection.QueryAsync<StateRow>(new CommandDefinition(
            "monitoring.usp_GetKpiEntityDetectionStates", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));
        return rows.Select(r => r.ToDomain()).ToList();
    }

    public async Task SaveAsync(KpiDetectionState state, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        KpiValueGuard.EnsureEntityKey(state.EntityKey, nameof(state));
        var parameters = new DynamicParameters();
        parameters.Add("KpiDefinitionId", state.KpiDefinitionId, DbType.Int32);
        parameters.Add("ConsecutiveCount", state.ConsecutiveCount, DbType.Int32);
        parameters.Add("LastBucketEvaluated", state.LastBucketEvaluated, DbType.DateTimeOffset);
        parameters.Add("CooldownUntil", state.CooldownUntil, DbType.DateTimeOffset);
        parameters.Add("LastEvaluatedAt", state.LastEvaluatedAt, DbType.DateTimeOffset);
        parameters.Add("EntityKey", KpiValueGuard.NormalizeEntityKey(state.EntityKey), DbType.String, size: KpiValueGuard.MaxEntityKeyLength);

        await using var connection = SqlKpiConnection.Open(storeOptions);
        await connection.ExecuteAsync(new CommandDefinition(
            "monitoring.usp_SaveKpiDetectionState", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));
    }

    private sealed class StateRow
    {
        public int KpiDefinitionId { get; set; }
        public int ConsecutiveCount { get; set; }
        public DateTimeOffset? LastBucketEvaluated { get; set; }
        public DateTimeOffset? CooldownUntil { get; set; }
        public DateTimeOffset? LastEvaluatedAt { get; set; }
        public string? EntityKey { get; set; }

        public KpiDetectionState ToDomain() =>
            new(KpiDefinitionId, ConsecutiveCount, LastBucketEvaluated, CooldownUntil, LastEvaluatedAt) { EntityKey = EntityKey ?? "" };
    }
}
