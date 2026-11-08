using System.Data;
using System.Text.Json;
using Dapper;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Options;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Infrastructure.Sql;

/// <summary>monitoring.DaemonActivity through usp_AddDaemonActivity / usp_GetDaemonActivity / usp_PurgeDaemonActivity.</summary>
public sealed class SqlDaemonActivityLog(IOptions<JourneyStoreOptions> storeOptions) : IDaemonActivityLog
{
    public const int MaxMessageLength = 1000;

    public async Task AppendAsync(IReadOnlyList<DaemonActivityEntry> entries, CancellationToken ct = default)
    {
        var payloads = BuildBatchPayloads(entries);
        if (payloads.Count == 0) return;

        await using var connection = SqlKpiConnection.Open(storeOptions);
        foreach (var payload in payloads)
        {
            var parameters = new DynamicParameters();
            parameters.Add("EntriesJson", payload, DbType.String, size: -1);
            await connection.ExecuteAsync(new CommandDefinition(
                "monitoring.usp_AddDaemonActivity", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));
        }
    }

    /// <summary>The exact @EntriesJson values AppendAsync sends: camelCase arrays of at most 1000 entries, level as the enum name.</summary>
    public static IReadOnlyList<string> BuildBatchPayloads(IReadOnlyList<DaemonActivityEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return entries
            .Select(e => new EntryPayload(
                e.DaemonKey,
                e.RunId,
                e.At,
                e.Level.ToString(),
                e.Kind,
                e.Message is { Length: > MaxMessageLength } m ? m[..MaxMessageLength] : e.Message ?? "",
                e.DataJson))
            .Chunk(SqlKpiConnection.BatchSize)
            .Select(batch => JsonSerializer.Serialize(batch, SqlKpiConnection.BatchJsonOptions))
            .ToList();
    }

    public async Task<DaemonActivityPage> GetAsync(long afterId, string? daemonKey, DaemonActivityLevel? minLevel, int top, CancellationToken ct = default)
    {
        var parameters = new DynamicParameters();
        parameters.Add("AfterId", Math.Max(0, afterId), DbType.Int64);
        parameters.Add("DaemonKey", daemonKey, DbType.String, size: 64);
        parameters.Add("MinLevel", minLevel?.ToString(), DbType.String, size: 16);
        parameters.Add("Top", Math.Clamp(top, 1, 1000), DbType.Int32);

        await using var connection = SqlKpiConnection.Open(storeOptions);
        var rows = await connection.QueryAsync<ActivityRow>(new CommandDefinition(
            "monitoring.usp_GetDaemonActivity", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));
        var entries = rows
            .Select(r => new DaemonActivityEntry(
                r.ActivityId,
                r.DaemonKey,
                r.RunId,
                r.At,
                Enum.TryParse<DaemonActivityLevel>(r.Level, ignoreCase: true, out var level) ? level : DaemonActivityLevel.Info,
                r.Kind,
                r.Message,
                r.DataJson))
            .ToList();
        var lastId = entries.Count > 0 ? entries[^1].ActivityId : Math.Max(0, afterId);
        return new DaemonActivityPage(entries, lastId);
    }

    public async Task<int> PurgeOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        var parameters = new DynamicParameters();
        parameters.Add("Cutoff", cutoff, DbType.DateTimeOffset);

        await using var connection = SqlKpiConnection.Open(storeOptions);
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "monitoring.usp_PurgeDaemonActivity", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));
    }

    private sealed record EntryPayload(string DaemonKey, Guid RunId, DateTimeOffset At, string Level, string Kind, string Message, string? DataJson);

    private sealed class ActivityRow
    {
        public long ActivityId { get; set; }
        public string DaemonKey { get; set; } = "";
        public Guid RunId { get; set; }
        public DateTimeOffset At { get; set; }
        public string Level { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Message { get; set; } = "";
        public string? DataJson { get; set; }
    }
}
