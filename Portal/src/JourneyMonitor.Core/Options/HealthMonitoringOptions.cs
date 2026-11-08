namespace JourneyMonitor.Core.Options;

public sealed class HealthMonitoringOptions
{
    public const string SectionName = "HealthMonitoring";
    public bool Enabled { get; set; }
    /// <summary>Pillars whose daemon is active. Default: Application only.</summary>
    public List<string> Pillars { get; set; } = ["Application"];
    public int MaxRowsPerCycle { get; set; } = 20_000;
    public int MaxKpisPerCycle { get; set; } = 50;
    public int SampleRetentionDays { get; set; } = 7;
}
