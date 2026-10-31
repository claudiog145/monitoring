using JourneyMonitor.Core.UseCases;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace JourneyMonitor.Functions.Functions;

/// <summary>
/// Inspector Monkey: the L3 analysis phase. Every minute it claims whatever the Scout handed off
/// and runs the multi-agent investigation over it.
///
/// A minute rather than the Scout's five because this is a queue consumer, not a detector: the
/// tick costs one cheap claim attempt when there is nothing to do, and a shorter tick is what
/// keeps the backlog from sitting idle after a burst. What bounds the spend is
/// Inspector:MaxInvestigationsPerDay and Inspector:Concurrency, never the timer.
///
/// The cycle lives in <see cref="InspectorMonkeyRunner"/>/<c>InspectorMonkeyService</c> (Core),
/// shared with the on-demand trigger, so a manual run behaves identically to a scheduled one.
/// </summary>
public sealed class InspectorMonkeyFunction(InspectorMonkeyRunner runner, ILogger<InspectorMonkeyFunction> logger)
{
    [Function("InspectorMonkey")]
    public async Task RunAsync([TimerTrigger("0 */1 * * * *")] TimerInfo timer, CancellationToken ct)
    {
        var summary = await runner.RunAsync(ct);
        logger.LogInformation(
            "[InspectorMonkey] {Leased} package(s) claimed, {Investigated} investigated.",
            summary.NewFindings, summary.Diagnosed);
    }
}
