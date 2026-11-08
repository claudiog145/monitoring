using System.Data;
using System.Text.Json;
using Dapper;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring.Inspector;
using JourneyMonitor.Core.Options;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Infrastructure.Sql;

/// <summary>monitoring.TriageInvestigation over the usp_* procedures of CreateMonitoringTriage.sql.</summary>
public sealed class SqlTriageInvestigationStore(IOptions<JourneyStoreOptions> storeOptions) : ITriageInvestigationStore
{
    public async Task CreateAsync(TriageInvestigation investigation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(investigation);
        var parameters = new DynamicParameters();
        parameters.Add("InvestigationId", investigation.InvestigationId, DbType.Guid);
        parameters.Add("DedupKey", investigation.DedupKey, DbType.String, size: 256);
        parameters.Add("AnomalyId", investigation.AnomalyId, DbType.Int64);
        parameters.Add("SignalJson", JsonSerializer.Serialize(investigation.Signal, TriageJson.Options), DbType.String, size: -1);
        parameters.Add("Source", investigation.Signal.Source, DbType.String, size: 16);
        parameters.Add("ProfileKey", TriageMonkeyService.ProfileKeyOf(investigation.Signal), DbType.String, size: 128);

        await using var connection = SqlKpiConnection.Open(storeOptions);
        await connection.ExecuteAsync(new CommandDefinition(
            "monitoring.usp_CreateTriageInvestigation", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));
    }

    public async Task<TriageInvestigation?> GetAsync(Guid investigationId, CancellationToken ct = default)
    {
        var parameters = new DynamicParameters();
        parameters.Add("InvestigationId", investigationId, DbType.Guid);

        await using var connection = SqlKpiConnection.Open(storeOptions);
        var row = await connection.QuerySingleOrDefaultAsync<InvestigationRow>(new CommandDefinition(
            "monitoring.usp_GetTriageInvestigation", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));
        return row?.ToDomain();
    }

    public async Task<TriageInvestigation?> FindOpenByDedupKeyAsync(string dedupKey, CancellationToken ct = default)
    {
        var parameters = new DynamicParameters();
        parameters.Add("DedupKey", dedupKey, DbType.String, size: 256);

        await using var connection = SqlKpiConnection.Open(storeOptions);
        var row = await connection.QuerySingleOrDefaultAsync<InvestigationRow>(new CommandDefinition(
            "monitoring.usp_FindOpenTriageInvestigationByDedupKey", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));
        return row?.ToDomain();
    }

    public async Task UpdateStatusAsync(Guid investigationId, TriageInvestigationStatus status, string? error, CancellationToken ct = default)
    {
        var parameters = new DynamicParameters();
        parameters.Add("InvestigationId", investigationId, DbType.Guid);
        parameters.Add("Status", status.ToString(), DbType.String, size: 16);
        parameters.Add("Error", error, DbType.String, size: 2000);

        await using var connection = SqlKpiConnection.Open(storeOptions);
        await connection.ExecuteAsync(new CommandDefinition(
            "monitoring.usp_UpdateTriageInvestigationStatus", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));
    }

    public async Task CompleteAsync(Guid investigationId, TriageResult result, bool notified, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        var parameters = new DynamicParameters();
        parameters.Add("InvestigationId", investigationId, DbType.Guid);
        parameters.Add("ResultJson", JsonSerializer.Serialize(result, TriageJson.Options), DbType.String, size: -1);
        parameters.Add("AlertLevel", result.Assessment.AlertLevel, DbType.String, size: 16);
        parameters.Add("Notified", notified, DbType.Boolean);

        await using var connection = SqlKpiConnection.Open(storeOptions);
        await connection.ExecuteAsync(new CommandDefinition(
            "monitoring.usp_CompleteTriageInvestigation", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<TriageInvestigation>> GetRecentAsync(int top, string? profileKey = null, CancellationToken ct = default)
    {
        var parameters = new DynamicParameters();
        parameters.Add("Top", top, DbType.Int32);
        parameters.Add("ProfileKey", string.IsNullOrWhiteSpace(profileKey) ? null : profileKey.Trim(), DbType.String, size: 128);

        await using var connection = SqlKpiConnection.Open(storeOptions);
        var rows = await connection.QueryAsync<InvestigationRow>(new CommandDefinition(
            "monitoring.usp_GetRecentTriageInvestigations", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));
        return rows.Select(r => r.ToDomain()).ToList();
    }

    private sealed class InvestigationRow
    {
        public Guid InvestigationId { get; set; }
        public string DedupKey { get; set; } = "";
        public long? AnomalyId { get; set; }
        public string SignalJson { get; set; } = "";
        public string Source { get; set; } = "";
        public string Status { get; set; } = "";
        public string? ResultJson { get; set; }
        public string? AlertLevel { get; set; }
        public bool Notified { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }
        public string? Error { get; set; }
        public string? ProfileKey { get; set; }

        public TriageInvestigation ToDomain() => new(
            InvestigationId,
            DedupKey,
            AnomalyId,
            ReadSignal(),
            Enum.Parse<TriageInvestigationStatus>(Status),
            string.IsNullOrEmpty(ResultJson) ? null : JsonSerializer.Deserialize<TriageResult>(ResultJson, TriageJson.Options),
            AlertLevel,
            Notified,
            CreatedAt,
            CompletedAt,
            Error);

        // Signals stored before contract v3 have no profileKey in SignalJson; the column holds it.
        private TriageSignal ReadSignal()
        {
            var signal = JsonSerializer.Deserialize<TriageSignal>(SignalJson, TriageJson.Options)
                ?? throw new InvalidOperationException($"Triage investigation {InvestigationId} has an empty SignalJson.");
            return string.IsNullOrWhiteSpace(signal.ProfileKey) && !string.IsNullOrWhiteSpace(ProfileKey)
                ? signal with { ProfileKey = ProfileKey }
                : signal;
        }
    }
}
