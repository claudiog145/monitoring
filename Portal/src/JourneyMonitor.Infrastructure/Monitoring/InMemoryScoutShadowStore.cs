using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Infrastructure.Sql;

namespace JourneyMonitor.Infrastructure.Monitoring;

/// <summary>
/// Process-local Scout shadow comparisons with the same rules as SqlScoutShadowStore: rows are validated
/// and trimmed, one row per (Board, ProfileKey, EntityKey, Stage, BucketStart) case-insensitive - a later
/// cycle updates it, but never downgrades a 'both' - rows older than 8 days are purged on write, reads are by
/// board over BucketStart in the last N hours, newest first, capped at 500, and counts are per entity-hour
/// incident (<see cref="ScoutShadowRows.Count"/>).
/// </summary>
public sealed class InMemoryScoutShadowStore(TimeProvider? timeProvider = null) : IScoutShadowStore
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly Dictionary<ScoutShadowRows.RowKey, (long Id, ScoutShadowComparison Row)> _rows = [];
    private long _nextId;

    public Task AddRangeAsync(IReadOnlyList<ScoutShadowComparison> comparisons, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(comparisons);
        ScoutShadowRows.Validate(comparisons, nameof(comparisons));
        lock (_gate)
        {
            foreach (var row in ScoutShadowRows.Deduplicate(comparisons))
            {
                var key = ScoutShadowRows.Key(row);
                if (_rows.TryGetValue(key, out var existing))
                {
                    // Like the MERGE: the row keeps its identity and takes the latest values, unless that would downgrade a 'both'.
                    if (ScoutShadowRows.MayReplace(existing.Row, row)) _rows[key] = (existing.Id, row);
                    continue;
                }
                _rows[key] = (++_nextId, row);
            }

            var cutoff = _time.GetUtcNow() - ScoutShadowRows.Retention;
            foreach (var key in _rows.Where(kv => kv.Value.Row.BucketStart < cutoff).Select(kv => kv.Key).ToList())
            {
                _rows.Remove(key);
            }
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ScoutShadowComparison>> GetRecentAsync(string board, int hours, string? profileKey, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ScoutShadowComparison>>(Window(board, hours, profileKey)
            .OrderByDescending(r => r.Row.BucketStart)
            .ThenByDescending(r => r.Id)
            .Take(ScoutShadowRows.MaxRecentRows)
            .Select(r => r.Row)
            .ToList());

    public Task<ScoutShadowCounts> GetCountsAsync(string board, int hours, string? profileKey, CancellationToken ct = default) =>
        Task.FromResult(ScoutShadowRows.Count(Window(board, hours, profileKey).Select(r => r.Row)));

    private List<(long Id, ScoutShadowComparison Row)> Window(string board, int hours, string? profileKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(board);
        var since = ScoutShadowRows.WindowStart(_time.GetUtcNow(), hours);
        var boardKey = board.Trim();
        var profile = string.IsNullOrWhiteSpace(profileKey) ? null : profileKey.Trim();
        lock (_gate)
        {
            return _rows.Values
                .Where(r => string.Equals(r.Row.Board, boardKey, StringComparison.OrdinalIgnoreCase)
                    && r.Row.BucketStart >= since
                    && (profile is null || string.Equals(r.Row.ProfileKey, profile, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }
    }
}
