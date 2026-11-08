using JourneyMonitor.Core.Domain;

namespace JourneyMonitor.Core.Abstractions;

/// <summary>
/// Contract v6 §4.1. What <c>GET /api/profile-journeys</c> and <c>GET /api/profile-journeys/{journeyId}</c>
/// return: journeys of one company only, labelled with that company's vocabulary.
/// </summary>
public interface IProfileJourneyReader
{
    Task<ProfileJourneyListResponse> ListAsync(ProfileJourneyQuery query, CancellationToken cancellationToken);

    /// <summary>Null when <paramref name="journeyId"/> does not exist for <paramref name="profileKey"/>.</summary>
    Task<ProfileJourneyDetailResponse?> GetAsync(string profileKey, string journeyId, CancellationToken cancellationToken);
}
