using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Options;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Functions.Functions;

/// <summary>Spec 1000 Business Health daemon. Inert while HealthMonitoring:Enabled is false; the cycle lives in <see cref="IPillarHealthMonitor"/>.</summary>
public sealed class BusinessHealthMonitorFunction(
    IPillarHealthMonitor monitor,
    IOptions<HealthMonitoringOptions> options,
    ILogger<BusinessHealthMonitorFunction> logger)
{
    [Function("BusinessHealthMonitor")]
    public Task RunAsync([TimerTrigger("%spec1000_monitor_cron%")] TimerInfo timer, CancellationToken ct) =>
        PillarMonitorTimer.RunAsync(monitor, options.Value, KpiPillar.Business, logger, ct);
}
