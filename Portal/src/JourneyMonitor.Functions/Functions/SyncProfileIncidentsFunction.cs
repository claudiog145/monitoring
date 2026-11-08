using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Core.UseCases;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Functions.Functions;

/// <summary>
/// Contract v6 §4.3: exception ingestion for every enabled company other than the incumbent, on the
/// same schedule as the journey sync. Thin by design: reading, classifying and storing live in
/// <see cref="SyncProfileExceptionsService"/>. The incumbent keeps its own pass in <c>SyncJourneysFunction</c>.
/// </summary>
public sealed class SyncProfileIncidentsFunction(
    IIncidentSchemaGate incidentSchemaGate,
    IOptions<JourneyStoreOptions> storeOptions,
    ILogger<SyncProfileIncidentsFunction> logger,
    SyncProfileExceptionsService? syncProfileExceptions = null)
{
    [Function("SyncProfileIncidents")]
    public async Task RunAsync(
        [TimerTrigger("%platform_reliability_cron_setting%")] TimerInfo timer,
        CancellationToken cancellationToken)
    {
        if (!storeOptions.Value.IsConfigured || syncProfileExceptions is null)
        {
            logger.LogInformation(
                "Per-company exception ingestion skipped: {Section}:ConnectionString is not configured.",
                JourneyStoreOptions.SectionName);
            return;
        }

        if (!incidentSchemaGate.IsProvisioned())
        {
            logger.LogWarning(
                "Per-company exception ingestion skipped: incident.usp_IngestExceptions was not found. "
                + "Apply Sql/incident/CreateIncidentSchema.sql.");
            return;
        }

        var passes = await syncProfileExceptions.SyncAllAsync(cancellationToken);
        foreach (var pass in passes)
        {
            if (pass.Stored)
            {
                logger.LogInformation(
                    "Ingested {ProfileKey}/{Service} exceptions: {IncidentCount} incidents ({InsertedIncidents} new, {UpdatedIncidents} updated, {InsertedOccurrences} new occurrences).",
                    pass.ProfileKey, pass.Service, pass.IncidentCount, pass.InsertedIncidents, pass.UpdatedIncidents, pass.InsertedOccurrences);
            }
            else
            {
                logger.LogWarning(
                    "Exception ingestion skipped for {ProfileKey}/{Service}: {SkipReason}",
                    pass.ProfileKey, pass.Service, pass.SkipReason);
            }
        }
    }
}
