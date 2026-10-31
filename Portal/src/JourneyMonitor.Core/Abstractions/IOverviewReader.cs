using JourneyMonitor.Core.Analytics;

namespace JourneyMonitor.Core.Abstractions;

public sealed record OverviewReadQuery(
    string? Domain,
    string? ClubId,
    string Environment,
    DateTimeOffset FilteredSince,
    DateTimeOffset AvailabilitySince,
    string? SearchId);

public interface IOverviewReader
{
    Task<OverviewCounts> ReadAsync(OverviewReadQuery query, CancellationToken cancellationToken);
}
