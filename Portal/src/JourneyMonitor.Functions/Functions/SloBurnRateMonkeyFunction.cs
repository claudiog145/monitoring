using JourneyMonitor.Core.UseCases;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace JourneyMonitor.Functions.Functions;

/// <summary>
/// SLO Burn-Rate Monkey: runs on the same 5-minute cadence as the Latency Spike Monkey. The
/// collect-evaluate-diagnose-notify cycle lives in <see cref="SloBurnRateMonkeyRunner"/> (Core),
/// shared with the on-demand "Run Now" trigger (see <c>RunMonkeyNowFunction</c>) - this Function
/// only parses the trigger and logs the result.
/// </summary>
public sealed class SloBurnRateMonkeyFunction(SloBurnRateMonkeyRunner runner, ILogger<SloBurnRateMonkeyFunction> logger)
{
    [Function("SloBurnRateMonkey")]
    public async Task RunAsync([TimerTrigger("0 */5 * * * *")] TimerInfo timer, CancellationToken ct)
    {
        var summary = await runner.RunAsync(ct);
        logger.LogInformation("[SloBurnRateMonkey] {NewFindings} new finding(s), {Diagnosed} diagnosed, {Notified} notified.", summary.NewFindings, summary.Diagnosed, summary.Notified);
    }
}

