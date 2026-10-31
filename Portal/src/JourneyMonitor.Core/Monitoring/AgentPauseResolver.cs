using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Core.Monitoring;

/// <summary>
/// Answers "is this agent paused right now?" for the runners and for the /api/monkeys cards.
///
/// Never throws: a store that is not configured, or a failing lookup, resolves to "not paused".
/// The alternative - failing the tick - would turn a settings-store outage into a silent
/// monitoring outage, which is the opposite of what a reliability agent should do. The honest
/// cost of that choice is that an unreachable database means the agents keep running, not that
/// they stop; that is the safer of the two lies.
///
/// Cached for a few seconds because every runner asks on every tick and the answer changes only
/// when a human clicks a button. <see cref="Invalidate"/> exists so a pause taking effect does
/// not have to wait out the TTL in the same process that just wrote it.
/// </summary>
public interface IAgentPauseResolver
{
    Task<bool> IsPausedAsync(string monkeyKey, CancellationToken cancellationToken);

    /// <summary>Pause state for every agent that has one, for callers that need more than one answer (the /api/monkeys cards).</summary>
    Task<IReadOnlyDictionary<string, bool>> GetAllAsync(CancellationToken cancellationToken);

    /// <summary>Drops the cached snapshot so the next read goes back to the store.</summary>
    void Invalidate();
}

public sealed class AgentPauseResolver(
    IAgentPauseStore pauseStore,
    IOptions<JourneyStoreOptions> storeOptions,
    ILogger<AgentPauseResolver> logger) : IAgentPauseResolver
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(10);
    private static readonly IReadOnlyDictionary<string, bool> NothingPaused =
        new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

    // Reference assignment is atomic; a concurrent pair of readers can at worst both refresh.
    private Snapshot? _snapshot;

    public async Task<bool> IsPausedAsync(string monkeyKey, CancellationToken cancellationToken)
    {
        var all = await GetAllAsync(cancellationToken);
        return all.TryGetValue(monkeyKey, out var paused) && paused;
    }

    public async Task<IReadOnlyDictionary<string, bool>> GetAllAsync(CancellationToken cancellationToken)
    {
        var cached = _snapshot;
        if (cached is not null && cached.ExpiresAt > DateTimeOffset.UtcNow) return cached.Paused;

        try
        {
            var rows = await pauseStore.GetAllAsync(cancellationToken);
            var map = rows.ToDictionary(r => r.MonkeyKey, r => r.IsPaused, StringComparer.OrdinalIgnoreCase);
            _snapshot = new Snapshot(map, DateTimeOffset.UtcNow.Add(CacheTtl));
            return map;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A genuine cancellation must propagate, not be swallowed into a "nothing is paused".
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Agent pause lookup failed; treating every agent as not paused until the store answers again.");
            // The failure is not cached: a stale "not paused" that outlives the outage would keep
            // a deliberately paused agent running for longer than the outage itself lasted.
            return cached?.Paused ?? NothingPaused;
        }
    }

    public void Invalidate() => _snapshot = null;

    private sealed record Snapshot(IReadOnlyDictionary<string, bool> Paused, DateTimeOffset ExpiresAt);
}
