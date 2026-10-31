using JourneyMonitor.Core.Incidents;

namespace JourneyMonitor.Core.Abstractions;

public sealed record IncidentIngestRequest(
    string Domain,
    string Environment,
    string Window,
    string FallbackClubId,
    IReadOnlyList<PlannedIncident> Incidents);

public sealed record IncidentIngestResult(
    int QueryRunCount,
    int InsertedIncidents,
    int UpdatedIncidents,
    int InsertedOccurrences);

/// <summary>
/// Writes one exception sync into the incident schema. Implemented by
/// <c>incident.usp_IngestExceptions</c>.
/// </summary>
public interface IIncidentStore
{
    Task<IncidentIngestResult> IngestAsync(IncidentIngestRequest request, CancellationToken cancellationToken);
}
