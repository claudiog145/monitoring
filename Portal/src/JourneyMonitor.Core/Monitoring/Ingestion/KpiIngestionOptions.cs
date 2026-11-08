namespace JourneyMonitor.Core.Monitoring.Ingestion;

/// <summary>KPI ingestion tuning that is not part of the frozen <c>HealthMonitoringOptions</c> contract.</summary>
public sealed class KpiIngestionOptions
{
    public const string SectionName = "HealthMonitoring:Ingestion";

    /// <summary>Delay before a closed bucket is ingested, so App Insights ingestion latency (2-5 min) does not truncate it.</summary>
    public int LagMinutes { get; set; } = 5;
}
