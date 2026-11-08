using System.Data;
using System.Text.Json;
using Dapper;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Options;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Infrastructure.Sql;

/// <summary>
/// monitoring.ScoutShadowComparison (CreateMonitoringScoutShadow.sql): batched upserts through
/// usp_AddScoutShadowComparisons - one row per (Board, ProfileKey, EntityKey, Stage, BucketStart), so a
/// retried or overlapping cycle updates the row instead of counting it twice, and never downgrades a 'both'; rows
/// older than 8 days are purged - window reads by board, newest first, capped at <see cref="ScoutShadowRows.MaxRecentRows"/>,
/// and counts per entity-hour incident (usp_GetScoutShadowCounts, mirrored by <see cref="ScoutShadowRows.Count"/>).
/// </summary>
public sealed class SqlScoutShadowStore(IOptions<JourneyStoreOptions> storeOptions, TimeProvider? timeProvider = null) : IScoutShadowStore
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task AddRangeAsync(IReadOnlyList<ScoutShadowComparison> comparisons, CancellationToken ct = default)
    {
        var payloads = BuildBatchPayloads(comparisons);
        if (payloads.Count == 0)
        {
            return;
        }

        await using var connection = SqlKpiConnection.Open(storeOptions);
        foreach (var payload in payloads)
        {
            var parameters = new DynamicParameters();
            parameters.Add("RowsJson", payload, DbType.String, size: -1);
            await connection.ExecuteAsync(new CommandDefinition(
                "monitoring.usp_AddScoutShadowComparisons", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));
        }
    }

    /// <summary>The @RowsJson values AddRangeAsync sends: validated, deduplicated (last wins), at most 1000 rows each.</summary>
    public static IReadOnlyList<string> BuildBatchPayloads(IReadOnlyList<ScoutShadowComparison> comparisons)
    {
        ArgumentNullException.ThrowIfNull(comparisons);
        ScoutShadowRows.Validate(comparisons, nameof(comparisons));
        return ScoutShadowRows.Deduplicate(comparisons)
            .Chunk(SqlKpiConnection.BatchSize)
            .Select(batch => JsonSerializer.Serialize(batch, SqlKpiConnection.BatchJsonOptions))
            .ToList();
    }

    public async Task<IReadOnlyList<ScoutShadowComparison>> GetRecentAsync(string board, int hours, string? profileKey, CancellationToken ct = default)
    {
        var parameters = WindowParameters(board, hours, profileKey);
        parameters.Add("Top", ScoutShadowRows.MaxRecentRows, DbType.Int32);

        await using var connection = SqlKpiConnection.Open(storeOptions);
        var rows = await connection.QueryAsync<ShadowRow>(new CommandDefinition(
            "monitoring.usp_GetScoutShadowComparisons", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));
        return rows.Select(r => new ScoutShadowComparison(
            r.RunId, r.Board, r.ProfileKey, r.EntityKey, r.Stage ?? "", r.BucketStart, r.RuleRank, r.MethodVerdict, r.KpiAnomalyId, r.Agreement)).ToList();
    }

    public async Task<ScoutShadowCounts> GetCountsAsync(string board, int hours, string? profileKey, CancellationToken ct = default)
    {
        await using var connection = SqlKpiConnection.Open(storeOptions);
        var row = await connection.QuerySingleAsync<CountsRow>(new CommandDefinition(
            "monitoring.usp_GetScoutShadowCounts", WindowParameters(board, hours, profileKey), commandType: CommandType.StoredProcedure, cancellationToken: ct));
        return new ScoutShadowCounts(row.Both, row.RulesOnly, row.MethodOnly);
    }

    private DynamicParameters WindowParameters(string board, int hours, string? profileKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(board);
        var parameters = new DynamicParameters();
        parameters.Add("Board", board.Trim(), DbType.String, size: 16);
        parameters.Add("Since", ScoutShadowRows.WindowStart(_time.GetUtcNow(), hours), DbType.DateTimeOffset);
        parameters.Add("ProfileKey", string.IsNullOrWhiteSpace(profileKey) ? null : profileKey.Trim(), DbType.String, size: 128);
        return parameters;
    }

    private sealed class ShadowRow
    {
        public Guid RunId { get; set; }
        public string Board { get; set; } = "";
        public string ProfileKey { get; set; } = "";
        public string EntityKey { get; set; } = "";
        public string? Stage { get; set; }
        public DateTimeOffset BucketStart { get; set; }
        public int? RuleRank { get; set; }
        public string MethodVerdict { get; set; } = "";
        public long? KpiAnomalyId { get; set; }
        public string Agreement { get; set; } = "";
    }

    private sealed class CountsRow
    {
        public int Both { get; set; }
        public int RulesOnly { get; set; }
        public int MethodOnly { get; set; }
    }
}

/// <summary>Rules shared by the SQL and in-memory shadow stores, so both accept and return the same rows.</summary>
internal static class ScoutShadowRows
{
    internal const int MaxRecentRows = 500;

    /// <summary>Shadow rows are kept as long as entity buckets (contract v4 section 1).</summary>
    internal static readonly TimeSpan Retention = SqlKpiBucketStore.EntityRetention;
    internal const int MaxBoardLength = 16;
    internal const int MaxProfileKeyLength = 128;
    internal const int MaxStageLength = 64;

    internal static DateTimeOffset WindowStart(DateTimeOffset now, int hours)
    {
        if (hours < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(hours), hours, "hours must be >= 1.");
        }
        return now - TimeSpan.FromHours(hours);
    }

    internal static void Validate(IReadOnlyList<ScoutShadowComparison> comparisons, string paramName)
    {
        foreach (var c in comparisons)
        {
            if (c is null)
            {
                throw new ArgumentException("Shadow comparison rows must not be null.", paramName);
            }
            if (string.IsNullOrWhiteSpace(c.Board) || c.Board.Length > MaxBoardLength)
            {
                throw new ArgumentException($"Board is required (max {MaxBoardLength}).", paramName);
            }
            if (string.IsNullOrWhiteSpace(c.ProfileKey) || c.ProfileKey.Length > MaxProfileKeyLength)
            {
                throw new ArgumentException($"ProfileKey is required (max {MaxProfileKeyLength}).", paramName);
            }
            KpiValueGuard.EnsureEntityKey(c.EntityKey, paramName);
            if (c.Stage is null || c.Stage.Length > MaxStageLength)
            {
                throw new ArgumentException($"Stage must be \"\" or max {MaxStageLength} chars.", paramName);
            }
            if (!ScoutShadowMethodVerdict.All.Contains(c.MethodVerdict))
            {
                throw new ArgumentException($"MethodVerdict '{c.MethodVerdict}' is not one of {string.Join(", ", ScoutShadowMethodVerdict.All)}.", paramName);
            }
            if (!ScoutShadowAgreement.All.Contains(c.Agreement))
            {
                throw new ArgumentException($"Agreement '{c.Agreement}' is not one of {string.Join(", ", ScoutShadowAgreement.All)}.", paramName);
            }
        }
    }

    /// <summary>
    /// Trims the key columns (SQL ignores trailing spaces in comparisons, so untrimmed keys could collide
    /// in the MERGE), then keeps one row per (Board, ProfileKey, EntityKey, Stage, BucketStart instant),
    /// case-insensitive like the SQL unique index; the last one wins.
    /// </summary>
    internal static List<ScoutShadowComparison> Deduplicate(IReadOnlyList<ScoutShadowComparison> comparisons)
    {
        var order = new List<RowKey>();
        var latest = new Dictionary<RowKey, ScoutShadowComparison>();
        foreach (var c in comparisons.Select(Normalize))
        {
            var key = Key(c);
            if (!latest.ContainsKey(key))
            {
                order.Add(key);
            }
            latest[key] = c;
        }
        return order.Select(k => latest[k]).ToList();
    }

    internal static ScoutShadowComparison Normalize(ScoutShadowComparison c) =>
        c with { Board = c.Board.Trim(), ProfileKey = c.ProfileKey.Trim(), EntityKey = c.EntityKey.Trim(), Stage = c.Stage.Trim() };

    internal static RowKey Key(ScoutShadowComparison c) =>
        new(c.Board.Trim().ToUpperInvariant(), c.ProfileKey.Trim().ToUpperInvariant(), c.EntityKey.Trim().ToUpperInvariant(),
            c.Stage.Trim().ToUpperInvariant(), c.BucketStart);

    internal readonly record struct RowKey(string Board, string ProfileKey, string EntityKey, string Stage, DateTimeOffset BucketStart);

    /// <summary>Whether an upsert of <paramref name="incoming"/> may replace <paramref name="existing"/>: a 'both' is never downgraded.</summary>
    internal static bool MayReplace(ScoutShadowComparison existing, ScoutShadowComparison incoming) =>
        existing.Agreement != ScoutShadowAgreement.Both || incoming.Agreement == ScoutShadowAgreement.Both;

    /// <summary>
    /// Counts per entity-hour incident, exactly like usp_GetScoutShadowCounts: group by (ProfileKey, EntityKey,
    /// method stage = Stage for method-only rows else "", UTC hour of BucketStart), case-insensitive; a group with a
    /// 'both' row or with rows from both sides is 'both', otherwise 'rules-only' or 'method-only'.
    /// </summary>
    internal static ScoutShadowCounts Count(IEnumerable<ScoutShadowComparison> rows)
    {
        int both = 0, rulesOnly = 0, methodOnly = 0;
        var groups = rows.GroupBy(r => (
            Profile: r.ProfileKey.Trim().ToUpperInvariant(),
            Entity: r.EntityKey.Trim().ToUpperInvariant(),
            Stage: r.Agreement == ScoutShadowAgreement.MethodOnly ? r.Stage.Trim().ToUpperInvariant() : "",
            Hour: HourOf(r.BucketStart)));
        foreach (var group in groups)
        {
            var hasBoth = group.Any(r => r.Agreement == ScoutShadowAgreement.Both);
            var hasRules = group.Any(r => r.Agreement == ScoutShadowAgreement.RulesOnly);
            var hasMethod = group.Any(r => r.Agreement == ScoutShadowAgreement.MethodOnly);
            if (hasBoth || (hasRules && hasMethod)) both++;
            else if (hasRules) rulesOnly++;
            else if (hasMethod) methodOnly++;
        }
        return new ScoutShadowCounts(both, rulesOnly, methodOnly);
    }

    private static DateTimeOffset HourOf(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - utc.Ticks % TimeSpan.TicksPerHour, TimeSpan.Zero);
    }
}