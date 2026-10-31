using JourneyMonitor.Core.UseCases;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace JourneyMonitor.Functions.Functions;

/// <summary>
/// Latency Spike Monkey: runs on a timer, detects per-stage latency spike clusters for every
/// domain in <see cref="LatencySpikeMonkeyRunner.Domains"/> and chains straight into the Doctor
/// Monkey for every NEW finding. The actual detect-then-diagnose logic lives in
/// <see cref="LatencySpikeMonkeyRunner"/> (Core), shared with the on-demand "Run Now" trigger
/// (see <c>RunMonkeyNowFunction</c>) - this Function only parses the trigger and logs the result.
/// </summary>
public sealed class LatencySpikeMonkeyFunction(LatencySpikeMonkeyRunner runner, ILogger<LatencySpikeMonkeyFunction> logger)
{
    [Function("LatencySpikeMonkey")]
    public async Task RunAsync([TimerTrigger("0 */5 * * * *")] TimerInfo timer, CancellationToken ct)
    {
        var summary = await runner.RunAsync(ct);
        logger.LogInformation("[LatencySpikeMonkey] {NewFindings} new finding(s), {Diagnosed} diagnosed.", summary.NewFindings, summary.Diagnosed);
    }
}
