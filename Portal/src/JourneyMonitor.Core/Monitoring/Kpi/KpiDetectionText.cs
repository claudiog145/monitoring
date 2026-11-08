using System.Globalization;
using JourneyMonitor.Core.Domain;

namespace JourneyMonitor.Core.Monitoring.Kpi;

public static class KpiDetectionText
{
    private const string SignedOneDecimal = "+0.0;-0.0;+0.0";

    /// <summary>
    /// "{count} consecutive {increases|decreases|deviations}: {Kpi} {observed} vs reference {reference} ({deviation})".
    /// For StaticThreshold the reference is the threshold.
    /// </summary>
    public static string Format(KpiDefinition definition, int consecutiveCount, double observed, double reference, double deviation)
    {
        var inv = CultureInfo.InvariantCulture;
        var noun = definition.Direction switch
        {
            KpiDirection.Increase => "increases",
            KpiDirection.Decrease => "decreases",
            _ => "deviations",
        };
        var deviationText = definition.DetectionMethod switch
        {
            DetectionMethodKind.BaselinePercent => deviation.ToString(SignedOneDecimal, inv) + "%",
            DetectionMethodKind.RobustZScore => "z=" + deviation.ToString(SignedOneDecimal, inv),
            _ => "threshold " + reference.ToString("0.##", inv),
        };
        return string.Create(inv,
            $"{consecutiveCount} consecutive {noun}: {definition.Kpi} {observed.ToString("0.##", inv)} vs reference {reference.ToString("0.##", inv)} ({deviationText})");
    }
}
