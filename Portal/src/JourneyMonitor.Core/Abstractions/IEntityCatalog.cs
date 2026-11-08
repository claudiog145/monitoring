using JourneyMonitor.Core.Domain;

namespace JourneyMonitor.Core.Abstractions;

/// <summary>
/// Contract v6 §4.2. Top entities of one company by request volume, identified with the profile's
/// entityIdentitySources. Replaces <c>/api/clubs</c> as the source of the header entity filter.
/// </summary>
public interface IEntityCatalog
{
    Task<EntityListResponse> GetTopEntitiesAsync(EntityQuery query, CancellationToken cancellationToken);
}
