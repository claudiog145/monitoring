namespace JourneyMonitor.Core.Abstractions;

/// <summary>
/// Whether <c>incident.usp_IngestExceptions</c> exists. A JourneyStore connection string is not
/// proof the incident script has been applied. Local docker-compose runs sql-init; QA and
/// production do not, so the timer must skip ingestion until CreateIncidentSchema.sql is applied.
/// </summary>
public interface IIncidentSchemaGate
{
    bool IsProvisioned();
}
