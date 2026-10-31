using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Filtering;

namespace JourneyMonitor.Core.UseCases;

public sealed record DashboardMetricsResult(
    string Source,
    string? Domain,
    string? Chart,
    int RowCount,
    IReadOnlyList<DashboardMetricPoint> Points,
    string? Error = null);

public sealed class GetDashboardMetricsService(IDashboardMetricStore metricStore)
{
    public async Task<(int StatusCode, DashboardMetricsResult Result)> GetAsync(
        string? domain,
        string? chart,
        string? window,
        string? environment,
        string? clubId,
        CancellationToken cancellationToken)
    {
        window = Clean(window);
        environment = Clean(environment);
        clubId = Clean(clubId);
        if (!TrySince(window, out var since, out var windowError))
        {
            return (400, new DashboardMetricsResult("error", Clean(domain), chart, 0, [], windowError));
        }

        if (IsAll(domain))
        {
            var points = new List<DashboardMetricPoint>();
            foreach (var name in PortalDomains.All)
            {
                points.AddRange(await metricStore.GetAsync(name, chart, since, environment, clubId, cancellationToken));
            }

            return (200, new DashboardMetricsResult("sql", null, chart, points.Count, points));
        }

        domain = domain!.Trim().ToLowerInvariant();
        if (domain is not "hotels" and not "cruises" and not "cars" and not "air")
        {
            return (400, new DashboardMetricsResult("error", domain, chart, 0, [], "domain must be hotels, cars, cruises or air"));
        }

        var selected = await metricStore.GetAsync(domain, chart, since, environment, clubId, cancellationToken);
        return (200, new DashboardMetricsResult("sql", domain, chart, selected.Count, selected));
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool TrySince(string? window, out DateTimeOffset? since, out string? error)
    {
        since = null;
        error = null;
        if (window is null) return true;
        if (!ClubFilter.TryParseWindow(window, out var duration))
        {
            error = "win must look like 15m, 1h or 7d";
            return false;
        }

        since = DateTimeOffset.UtcNow - duration;
        return true;
    }

    private static bool IsAll(string? domain) =>
        string.IsNullOrWhiteSpace(domain) || domain.Trim().Equals("all", StringComparison.OrdinalIgnoreCase);
}
