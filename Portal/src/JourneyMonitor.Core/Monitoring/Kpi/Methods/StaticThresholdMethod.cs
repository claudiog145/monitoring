namespace JourneyMonitor.Core.Monitoring.Kpi.Methods;

internal static class StaticThresholdMethod
{
    public static (double? reference, double? deviation, string? notEvaluatedReason) Compute(double observed, double threshold)
        => (threshold, observed - threshold, null);
}
