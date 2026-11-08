using JourneyMonitor.Core.Domain;

namespace JourneyMonitor.Core.Monitoring.Kpi.Methods;

internal static class KpiStatistics
{
    /// <summary>Median; for an even count, the mean of the two central values.</summary>
    public static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        if (sorted.Length == 0) throw new ArgumentException("Median requires at least one value.", nameof(values));
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    public static double? Observed(KpiBucket bucket, KpiAggregation aggregation) => aggregation switch
    {
        KpiAggregation.P95 => bucket.P95,
        KpiAggregation.P50 => bucket.P50,
        KpiAggregation.Mean => bucket.Mean,
        KpiAggregation.Count => bucket.TotalCount,
        KpiAggregation.Value => bucket.Value,
        _ => null,
    };
}
