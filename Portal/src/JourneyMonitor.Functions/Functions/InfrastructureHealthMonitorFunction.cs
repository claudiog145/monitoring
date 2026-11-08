using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Options;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Functions.Functions;

/// <summary>Spec 1000 Infrastructure Health daemon. Inert while HealthMonitoring:Enabled is false; the cycle lives in <see cref="IPillarHealthMonitor"/>.</summary>
public sealed class InfrastructureHealthMonitorFunction(
    IPillarHealthMonitor monitor,
    IOptions<HealthMonitoringOptions> options,
    ILogger<InfrastructureHealthMonitorFunction> logger)
{
    [Function("InfrastructureHealthMonitor")]
    public Task RunAsync([TimerTrigger("%spec1000_monitor_cron%")] TimerInfo timer, CancellationToken ct) =>
        PillarMonitorTimer.RunAsync(monitor, options.Value, KpiPillar.Infrastructure, logger, ct);
}
