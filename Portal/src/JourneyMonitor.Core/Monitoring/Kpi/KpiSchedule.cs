namespace JourneyMonitor.Core.Monitoring.Kpi;

/// <summary>
/// When a KPI is due for evaluation. The monitors run on a cron, and the previous run's
/// LastEvaluatedAt lands a few milliseconds after the tick, so a strict "now - last &lt; frequency"
/// check made a 1-minute KPI skip every other run (and a 5-minute KPI wait a sixth minute).
/// </summary>
public static class KpiSchedule
{
    public static readonly TimeSpan DueTolerance = TimeSpan.FromSeconds(5);

    public static bool IsDue(DateTimeOffset? lastEvaluatedAt, DateTimeOffset now, TimeSpan frequency) =>
        lastEvaluatedAt is not { } last || now - last >= frequency - DueTolerance;
}
