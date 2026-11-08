using JourneyMonitor.Core.Domain;

namespace JourneyMonitor.Core.Abstractions;

/// <summary>
/// Contract v6 §4.1. Persistence of generic per-company journeys (monitoring.ProfileJourney and
/// monitoring.ProfileJourneyStep). Every read and write is scoped to one ProfileKey.
/// </summary>
public interface IProfileJourneyStore
{
    /// <summary>Inserts or replaces the given journeys of <paramref name="profileKey"/> together with their steps.</summary>
    Task<int> UpsertAsync(
        string profileKey,
        IReadOnlyList<ProfileJourney> journeys,
        IReadOnlyList<ProfileJourneyStep> steps,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ProfileJourney>> ListAsync(ProfileJourneyQuery query, CancellationToken cancellationToken);

    Task<ProfileJourney?> GetAsync(string profileKey, string journeyId, CancellationToken cancellationToken);

    Task<IReadOnlyList<ProfileJourneyStep>> GetStepsAsync(string profileKey, string journeyId, CancellationToken cancellationToken);

    /// <summary>Retention: deletes journeys (and their steps) that started before <paramref name="cutoff"/>.</summary>
    Task<int> PurgeOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken);
}
