using System.Collections.Concurrent;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;

namespace JourneyMonitor.Infrastructure.ProfileJourneys;

/// <summary>Rules both stores share, so the in-memory store behaves like the SQL one.</summary>
internal static class ProfileJourneyStoreRules
{
    /// <summary>The journeys that belong to <paramref name="profileKey"/>; the last one wins on a duplicate id.</summary>
    public static List<ProfileJourney> OwnJourneys(string profileKey, IEnumerable<ProfileJourney> journeys) =>
        journeys
            .Where(j => j is not null && string.Equals(j.ProfileKey?.Trim(), profileKey, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(j.JourneyId))
            .GroupBy(j => j.JourneyId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Last() with { ProfileKey = profileKey })
            .ToList();

    public static List<ProfileJourneyStep> OwnSteps(string profileKey, IReadOnlyList<ProfileJourney> journeys, IEnumerable<ProfileJourneyStep> steps)
    {
        var ids = journeys.Select(j => j.JourneyId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return steps
            .Where(s => s is not null && string.Equals(s.ProfileKey?.Trim(), profileKey, StringComparison.OrdinalIgnoreCase) && ids.Contains(s.JourneyId))
            .GroupBy(s => (s.JourneyId.ToUpperInvariant(), s.StepOrder))
            .Select(g => g.Last() with { ProfileKey = profileKey })
            .ToList();
    }

    /// <summary><c>status=dropped</c> selects every "Dropped at step N".</summary>
    public static bool IsDroppedFilter(string? status) =>
        string.Equals(status?.Trim(), "dropped", StringComparison.OrdinalIgnoreCase);

    public static bool MatchesStatus(string journeyStatus, string? filter)
    {
        var value = (filter ?? "").Trim();
        if (value.Length == 0) return true;
        if (IsDroppedFilter(value)) return journeyStatus.StartsWith(ProfileJourneyStatus.DroppedPrefix, StringComparison.Ordinal);
        return string.Equals(journeyStatus, value, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Process-local <see cref="IProfileJourneyStore"/> for hosts without the SQL tables (fresh clone, tests).</summary>
public sealed class InMemoryProfileJourneyStore(TimeProvider? clock = null) : IProfileJourneyStore
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ConcurrentDictionary<(string Profile, string Journey), (ProfileJourney Journey, IReadOnlyList<ProfileJourneyStep> Steps)> _rows = new();

    private static (string, string) Key(string profileKey, string journeyId) =>
        (profileKey.Trim().ToUpperInvariant(), journeyId.Trim().ToUpperInvariant());

    public Task<int> UpsertAsync(string profileKey, IReadOnlyList<ProfileJourney> journeys, IReadOnlyList<ProfileJourneyStep> steps, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileKey);
        var key = profileKey.Trim();
        var mine = ProfileJourneyStoreRules.OwnJourneys(key, journeys);
        var mySteps = ProfileJourneyStoreRules.OwnSteps(key, mine, steps);
        foreach (var journey in mine)
        {
            var own = mySteps
                .Where(s => string.Equals(s.JourneyId, journey.JourneyId, StringComparison.OrdinalIgnoreCase))
                .OrderBy(s => s.StepOrder)
                .ToList();
            _rows[Key(key, journey.JourneyId)] = (journey, own);
        }
        return Task.FromResult(mine.Count);
    }

    public Task<IReadOnlyList<ProfileJourney>> ListAsync(ProfileJourneyQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var since = _clock.GetUtcNow() - query.Window;
        IReadOnlyList<ProfileJourney> result = _rows.Values
            .Select(r => r.Journey)
            .Where(j => string.Equals(j.ProfileKey, query.ProfileKey.Trim(), StringComparison.OrdinalIgnoreCase))
            .Where(j => j.StartedAt >= since)
            .Where(j => string.IsNullOrWhiteSpace(query.Service) || string.Equals(j.ServiceKey, query.Service.Trim(), StringComparison.OrdinalIgnoreCase))
            .Where(j => string.IsNullOrWhiteSpace(query.Entity) || string.Equals(j.EntityKey, query.Entity.Trim(), StringComparison.OrdinalIgnoreCase))
            .Where(j => ProfileJourneyStoreRules.MatchesStatus(j.Status, query.Status))
            .OrderByDescending(j => j.StartedAt)
            .ThenBy(j => j.JourneyId, StringComparer.Ordinal)
            .Take(Math.Max(1, query.Top))
            .ToList();
        return Task.FromResult(result);
    }

    public Task<ProfileJourney?> GetAsync(string profileKey, string journeyId, CancellationToken cancellationToken) =>
        Task.FromResult(_rows.TryGetValue(Key(profileKey, journeyId), out var row) ? row.Journey : null);

    public Task<IReadOnlyList<ProfileJourneyStep>> GetStepsAsync(string profileKey, string journeyId, CancellationToken cancellationToken) =>
        Task.FromResult(_rows.TryGetValue(Key(profileKey, journeyId), out var row) ? row.Steps : (IReadOnlyList<ProfileJourneyStep>)[]);

    public Task<int> PurgeOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        var removed = 0;
        foreach (var entry in _rows.Where(r => r.Value.Journey.StartedAt < cutoff).ToList())
        {
            if (_rows.TryRemove(entry.Key, out _)) removed++;
        }
        return Task.FromResult(removed);
    }
}
