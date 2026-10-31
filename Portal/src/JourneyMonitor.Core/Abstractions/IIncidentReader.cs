namespace JourneyMonitor.Core.Abstractions;

/// <summary>
/// One incident row for the Exceptions tab. Member and URL come from the latest
/// occurrence; <see cref="RecentOccurrences"/> carries a short history of both.
/// </summary>
public sealed record IncidentRow(
    string ExternalId,
    string Domain,
    string ClubId,
    string Environment,
    string ExceptionType,
    string Component,
    string Operation,
    string Message,
    string? StackTrace,
    string Severity,
    string Status,
    string? AssignedTo,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen,
    int OccurrenceCount,
    int UsersAffected,
    string? LatestMemberId,
    string? LatestUrl,
    IReadOnlyList<string> JourneyKeys,
    IReadOnlyList<IncidentOccurrenceRow> RecentOccurrences);

public sealed record IncidentOccurrenceRow(
    DateTimeOffset OccurredAt,
    string? MemberId,
    string? Url,
    string? Message);

public sealed record IncidentReadQuery(
    string? Domain,
    string? ClubId,
    string Environment,
    DateTimeOffset? Since,
    string? SearchPattern,
    int Limit);

/// <summary>
/// Reads grouped incidents already stored by exception ingestion.
/// </summary>
public interface IIncidentReader
{
    Task<IReadOnlyList<IncidentRow>> ListAsync(IncidentReadQuery query, CancellationToken cancellationToken);
}
