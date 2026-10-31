using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Core.UseCases;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Functions.Functions;

/// <summary>
/// Timer triggers that keep the SQL journey store current, one vertical at a time. Thin by
/// design: the query, the live-only guard and the write all live in SyncJourneysService (Core),
/// so the HTTP refresh path can reuse the exact same behavior.
///
/// Hotels and cruises used to share one timer function, so a hotel pass held cruises. Each
/// vertical now has its own function, so the two runs overlap instead of waiting. Both read
/// <c>platform_reliability_cron_setting</c> (App Configuration key platform-reliability-cron-setting,
/// hyphens as underscores, every 5 minutes). The Functions host resolves that expression while
/// indexing functions, so it has to arrive as an environment variable. A missing value fails
/// indexing for every function in the app. az containerapp rejects env var names outside
/// a-z, A-Z, 0-9 and _.
/// </summary>
public sealed class SyncJourneysFunction(
    SyncJourneysService syncJourneysService,
    SyncIncidentsService syncIncidentsService,
    IIncidentSchemaGate incidentSchemaGate,
    IOptions<JourneyStoreOptions> storeOptions,
    IOptions<JourneySyncOptions> syncOptions,
    ILogger<SyncJourneysFunction> logger)
{
    private static int _incidentSchemaWarningLogged;

    [Function("SyncHotelJourneys")]
    public Task SyncHotelsAsync(
        [TimerTrigger("%platform_reliability_cron_setting%")] TimerInfo timer,
        CancellationToken cancellationToken) =>
        RunDomainAsync("hotels", cancellationToken);

    [Function("SyncCruiseJourneys")]
    public Task SyncCruisesAsync(
        [TimerTrigger("%platform_reliability_cron_setting%")] TimerInfo timer,
        CancellationToken cancellationToken) =>
        RunDomainAsync("cruises", cancellationToken);

    private async Task RunDomainAsync(string domain, CancellationToken cancellationToken)
    {
        if (!storeOptions.Value.IsConfigured)
        {
            logger.LogInformation(
                "Journey sync skipped: {Section}:ConnectionString is not configured.",
                JourneyStoreOptions.SectionName);
            return;
        }

        if (!syncOptions.Value.EnumerateDomains().Contains(domain, StringComparer.OrdinalIgnoreCase))
        {
            logger.LogInformation(
                "Journey sync skipped for {Domain}: not listed in {Section}:Domains.",
                domain, JourneySyncOptions.SectionName);
            return;
        }

        var passes = await syncJourneysService.SyncDomainAsync(domain, cancellationToken);
        foreach (var pass in passes)
        {
            if (pass.Stored)
            {
                // The window overlaps between runs, so most of a pass is usually unchanged rows.
                // Logging the split is how we can tell the upsert is doing its job.
                logger.LogInformation(
                    "Synced {RecordCount} {Domain} club {ClubId} journeys as query run {QueryRunId} "
                    + "({Inserted} new, {Updated} advanced, {Unchanged} unchanged).",
                    pass.RecordCount, pass.Domain, pass.ClubId, pass.QueryRunId,
                    pass.Inserted, pass.Updated, pass.Unchanged);
            }
            else
            {
                logger.LogWarning(
                    "Nothing stored for {Domain} club {ClubId}: {SkipReason}",
                    pass.Domain, pass.ClubId, pass.SkipReason);
            }

            if (pass.DashboardMetrics is not null)
            {
                foreach (var metric in pass.DashboardMetrics)
                {
                    if (metric.Stored)
                    {
                        logger.LogInformation(
                            "Synced {PointCount} {Domain} {Chart} dashboard points.",
                            metric.PointCount, metric.Domain, metric.Chart);
                    }
                    else if (!string.IsNullOrEmpty(metric.Chart) || !string.IsNullOrEmpty(metric.SkipReason))
                    {
                        logger.LogWarning(
                            "Dashboard metrics skipped for {Domain} {Chart}: {SkipReason}",
                            metric.Domain, metric.Chart, metric.SkipReason);
                    }
                }
            }
        }

        if (!incidentSchemaGate.IsProvisioned())
        {
            // QA and production JourneyStores do not run sql-init. Missing procedure must not
            // fail the timer after journeys have already been stored. Log once per process.
            if (Interlocked.Exchange(ref _incidentSchemaWarningLogged, 1) == 0)
            {
                logger.LogWarning(
                    "Exception ingestion skipped: incident.usp_IngestExceptions was not found. "
                    + "Apply Sql/incident/CreateIncidentSchema.sql. The next timer run checks again.");
            }

            return;
        }

        var incidentPass = await syncIncidentsService.SyncAsync(domain, cancellationToken);
        if (incidentPass.Stored)
        {
            logger.LogInformation(
                "Ingested {Domain} exceptions: {IncidentCount} incidents ({InsertedIncidents} new, {UpdatedIncidents} updated, {InsertedOccurrences} new occurrences).",
                incidentPass.Domain, incidentPass.IncidentCount, incidentPass.InsertedIncidents, incidentPass.UpdatedIncidents, incidentPass.InsertedOccurrences);
        }
        else
        {
            logger.LogWarning(
                "Exception ingestion skipped for {Domain}: {SkipReason}",
                incidentPass.Domain, incidentPass.SkipReason);
        }
    }
}
