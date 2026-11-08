using System.Runtime.ExceptionServices;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring.Kpi;
using JourneyMonitor.Core.Options;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Functions.Functions;

/// <summary>
/// Spec 1000 Application Health daemon. Inert while HealthMonitoring:Enabled is false; the cycle lives in <see cref="IPillarHealthMonitor"/>.
/// After the pillar monitor, the same tick runs <see cref="IEntityDetectionMonitor"/> (contract v4 §3); a failure of either part
/// does not prevent the other from running.
/// </summary>
public sealed class ApplicationHealthMonitorFunction(
    IPillarHealthMonitor monitor,
    IOptions<HealthMonitoringOptions> options,
    ILogger<ApplicationHealthMonitorFunction> logger,
    IEntityDetectionMonitor? entityMonitor = null)
{
    [Function("ApplicationHealthMonitor")]
    public async Task RunAsync([TimerTrigger("%spec1000_monitor_cron%")] TimerInfo timer, CancellationToken ct)
    {
        ExceptionDispatchInfo? pillarFailure = null;
        try
        {
            await PillarMonitorTimer.RunAsync(monitor, options.Value, KpiPillar.Application, logger, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            pillarFailure = ExceptionDispatchInfo.Capture(ex);
        }

        if (entityMonitor is not null && options.Value.Enabled)
        {
            try
            {
                var result = await entityMonitor.RunAsync(ct);
                if (result.Definitions > 0)
                {
                    logger.LogInformation("[ApplicationHealthMonitor] Entity detection: {Definitions} KPI(s), {Series} series evaluated, {Anomalous} anomalous, {Triggered} triggered.",
                        result.Definitions, result.SeriesEvaluated, result.Anomalous, result.Triggered);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "[ApplicationHealthMonitor] Entity detection failed; the pillar result is kept.");
            }
        }

        pillarFailure?.Throw();
    }
}

/// <summary>Shared body of the three pillar daemons: gate on HealthMonitoring:Enabled, dispatch, log.</summary>
internal static class PillarMonitorTimer
{
    internal static async Task RunAsync(IPillarHealthMonitor monitor, HealthMonitoringOptions options, KpiPillar pillar, ILogger logger, CancellationToken ct)
    {
        if (!options.Enabled)
        {
            logger.LogDebug("[{Pillar}HealthMonitor] HealthMonitoring:Enabled is false - nothing to do.", pillar);
            return;
        }

        var result = await monitor.RunAsync(pillar, ct);
        if (result.KpisEvaluated == 0 && result.Triggered == 0 && result.Suppressed == 0)
        {
            logger.LogInformation("[{Pillar}HealthMonitor] Up to date — nothing to evaluate ({Skipped} KPI(s) not due or without a new bucket).",
                pillar, result.Skipped);
            return;
        }
        logger.LogInformation(
            "[{Pillar}HealthMonitor] {Evaluated} KPI(s) evaluated, {Triggered} triggered, {Suppressed} suppressed, {Skipped} skipped.",
            pillar, result.KpisEvaluated, result.Triggered, result.Suppressed, result.Skipped);
    }
}
