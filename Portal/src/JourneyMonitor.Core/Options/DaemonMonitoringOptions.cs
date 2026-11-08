namespace JourneyMonitor.Core.Options;

public sealed class DaemonMonitoringOptions
{
    public const string SectionName = "DaemonMonitoring";
    /// <summary>A daemon whose last run is older than this is "stale".</summary>
    public int StaleAfterMinutes { get; set; } = 5;
    /// <summary>A run-started without run-completed younger than this is "running".</summary>
    public int RunningWindowMinutes { get; set; } = 3;
    public int RetentionDays { get; set; } = 7;
    /// <summary>Shown on the cards; filled from the cron app settings by DI.</summary>
    public string IngestionSchedule { get; set; } = "0 */1 * * * *";
    public string MonitorSchedule { get; set; } = "30 */1 * * * *";
    public string TriageSchedule { get; set; } = "0 */1 * * * *";
}
