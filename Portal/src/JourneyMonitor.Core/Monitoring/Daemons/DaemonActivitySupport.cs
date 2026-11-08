using System.Globalization;
using JourneyMonitor.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace JourneyMonitor.Core.Monitoring.Daemons;

/// <summary>Shared bits of the daemon instrumentation: message time format and the best-effort end of a run.</summary>
public static class DaemonActivitySupport
{
    /// <summary>UTC "HH:mm", culture invariant (the format every activity message uses).</summary>
    public static string Hm(DateTimeOffset value) => value.UtcDateTime.ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>Flushes the run and records the last run on the tracker. Never throws.</summary>
    public static async Task FinishAsync(IDaemonRun run, IMonkeyRunTracker? tracker, string daemonKey, DateTimeOffset at, ILogger logger)
    {
        await run.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        if (tracker is null) return;
        try
        {
            await tracker.RecordRunAsync(daemonKey, at, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not record the last run of {DaemonKey} on the monkey run tracker.", daemonKey);
        }
    }
}
