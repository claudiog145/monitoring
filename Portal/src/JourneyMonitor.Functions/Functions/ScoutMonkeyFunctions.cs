using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.UseCases;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace JourneyMonitor.Functions.Functions;

/// <summary>
/// Latency Scout Monkey: the detection phase over the latency priority board. Every 5 minutes it
/// reads the per-club board from Application Insights, decides what deserves the L3 team, and
/// persists an InvestigationPackage for each - that handoff is what the Inspector consumes.
///
/// The cycle itself lives in <see cref="ScoutMonkeyRunner"/>/<c>ScoutMonkeyService</c> (Core),
/// shared with the exception scout and with the on-demand "Run Now" trigger, so a manual run
/// behaves identically to a scheduled one. Set ScoutLatency:Enabled to false to silence it without
/// a redeploy; the cycle is still recorded, so a disabled scout is visible rather than looking
/// like a dead one.
/// </summary>
public sealed class LatencyScoutMonkeyFunction(ScoutMonkeyRunner runner, ILogger<LatencyScoutMonkeyFunction> logger)
{
    [Function("LatencyScoutMonkey")]
    public async Task RunAsync([TimerTrigger("0 */5 * * * *")] TimerInfo timer, CancellationToken ct)
    {
        var summary = await runner.RunAsync(ScoutBoard.Latency, ct);
        logger.LogInformation("[LatencyScoutMonkey] {Packages} investigation package(s) handed off.", summary.NewFindings);
    }
}

/// <summary>
/// Exception Scout Monkey: the same pipeline as <see cref="LatencyScoutMonkeyFunction"/> over the
/// exception priority board, configured through ScoutException:*.
///
/// Same cron as the latency scout on purpose: the two boards are independent cross-resource
/// queries and running them together is what the split bought. The per-board overlap guard in
/// <see cref="ScoutMonkeyRunner"/> is what keeps each one from stacking on itself.
/// </summary>
public sealed class ExceptionScoutMonkeyFunction(ScoutMonkeyRunner runner, ILogger<ExceptionScoutMonkeyFunction> logger)
{
    [Function("ExceptionScoutMonkey")]
    public async Task RunAsync([TimerTrigger("0 */5 * * * *")] TimerInfo timer, CancellationToken ct)
    {
        var summary = await runner.RunAsync(ScoutBoard.Exception, ct);
        logger.LogInformation("[ExceptionScoutMonkey] {Packages} investigation package(s) handed off.", summary.NewFindings);
    }
}
