using JourneyMonitor.Core.Domain;

namespace JourneyMonitor.Core.Abstractions;

public static class DashboardCharts
{
    public const string StepLatencyP95 = "step_latency_p95";
    public const string ExceptionsByStep = "exceptions_by_step";

    /// <summary>
    /// Charts ingested today. Cars/Air stay empty until those tiles are copied in;
    /// the SQL table already accepts those domains.
    /// </summary>
    public static IReadOnlyList<string> ForDomain(string domain) =>
        domain.ToLowerInvariant() switch
        {
            "hotels" or "cruises" => [StepLatencyP95, ExceptionsByStep],
            _ => [],
        };
}

public sealed record DashboardMetricPoint(
    string Domain,
    string Chart,
    string Series,
    DateTimeOffset BinStart,
    decimal Value,
    string Window,
    string? Environment,
    DateTimeOffset FetchedAt);

public interface IDashboardMetricStore
{
    Task ReplaceChartAsync(
        string domain,
        string chart,
        string window,
        string? environment,
        string? clubId,
        IReadOnlyList<DashboardMetricPoint> points,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<DashboardMetricPoint>> GetAsync(
        string? domain,
        string? chart,
        DateTimeOffset? since,
        string? environment,
        string? clubId,
        CancellationToken cancellationToken);
}
