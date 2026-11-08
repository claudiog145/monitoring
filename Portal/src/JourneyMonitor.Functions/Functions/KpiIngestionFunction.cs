using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Options;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Functions.Functions;

/// <summary>
/// Spec 1000 KPI ingestion: pulls closed buckets from every enabled KPI source into the KPI store.
/// Inert while HealthMonitoring:Enabled is false. The cycle lives in <see cref="IKpiIngestionRunner"/> (Core).
/// </summary>
public sealed class KpiIngestionFunction(
    IKpiIngestionRunner runner,
    IOptions<HealthMonitoringOptions> options,
    ILogger<KpiIngestionFunction> logger)
{
    [Function("KpiIngestion")]
    public async Task RunAsync([TimerTrigger("%spec1000_ingestion_cron%")] TimerInfo timer, CancellationToken ct)
    {
        if (!options.Value.Enabled)
        {
            logger.LogDebug("[KpiIngestion] HealthMonitoring:Enabled is false - nothing to do.");
            return;
        }

        var result = await runner.RunAsync(ct);
        if (result.KpisProcessed == 0 && result.Skipped == 0)
        {
            logger.LogInformation("[KpiIngestion] Up to date — nothing to ingest.");
            return;
        }
        logger.LogInformation(
            "[KpiIngestion] {Kpis} KPI(s) processed, {Buckets} bucket(s) and {Samples} sample(s) written, {Skipped} skipped.",
            result.KpisProcessed, result.BucketsWritten, result.SamplesWritten, result.Skipped);
    }
}
