namespace JourneyMonitor.Core.Monitoring;

/// <summary>
/// One stage's spike cluster as computed by a detector's dry-run <c>PreviewAsync</c>, without any
/// Finding/repository side effects. Used by <see cref="CompositeLatencySpikeDetector"/> to log a
/// shadow-mode comparison between the KQL and SQL detectors for a domain
/// (<see cref="Options.MonitoringOptions.ShadowDomains"/>) - see the KQL-to-DB migration plan.
/// </summary>
public sealed record SpikeGroupPreview(string Stage, int ObservedCount, double MaxDurationMs);
