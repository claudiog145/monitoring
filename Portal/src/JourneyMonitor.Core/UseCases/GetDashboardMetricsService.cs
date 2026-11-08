using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Filtering;
using JourneyMonitor.Core.Monitoring.Kpi;

namespace JourneyMonitor.Core.UseCases;

public sealed record DashboardMetricsResult(
    string Source,
    string? Domain,
    string? Chart,
    int RowCount,
    IReadOnlyList<DashboardMetricPoint> Points,
    string? Error = null)
{
    /// <summary>Contract v6 §4.4: the company every point belongs to.</summary>
    public string? ProfileKey { get; init; }

    public DashboardLabels? Labels { get; init; }

    /// <summary>Services of the profile with their charts and steps, so the UI builds charts without fixed names.</summary>
    public IReadOnlyList<DashboardService>? Services { get; init; }

    public string? Warning { get; init; }
}

public sealed class GetDashboardMetricsService(
    IDashboardMetricStore metricStore,
    IProfileDashboardMetricStore? profileMetricStore = null,
    ICompanyProfileStore? profiles = null,
    KpiProfileResolver? profileResolver = null)
{
    private readonly KpiProfileResolver _resolver = profileResolver ?? new KpiProfileResolver(profileStore: profiles);

    /// <summary>
    /// Contract v6 §4.4 / §1: the points of ONE company - the named one, or the active one when
    /// <paramref name="profileKey"/> is blank. "*" is refused: a chart never mixes two companies.
    /// </summary>
    public async Task<(int StatusCode, DashboardMetricsResult Result)> GetForProfileAsync(
        string? profileKey,
        string? domain,
        string? chart,
        string? window,
        string? environment,
        string? clubId,
        CancellationToken cancellationToken)
    {
        if (profileMetricStore is null || profiles is null)
        {
            return await GetAsync(domain, chart, window, environment, clubId, cancellationToken);
        }

        if (string.Equals(profileKey?.Trim(), KpiProfiles.All, StringComparison.Ordinal))
        {
            return (400, new DashboardMetricsResult("error", Clean(domain), chart, 0, [], "profileKey must name one company"));
        }

        var scope = await _resolver.ResolveReadAsync(profileKey, cancellationToken);
        if (!scope.IsValid || scope.ProfileKey is null)
        {
            return (400, new DashboardMetricsResult("error", Clean(domain), chart, 0, [], scope.Error ?? "profileKey must name one company"));
        }

        var profile = await profiles.GetAsync(scope.ProfileKey, cancellationToken);
        if (profile is null)
        {
            return (400, new DashboardMetricsResult("error", Clean(domain), chart, 0, [], KpiProfileResolver.UnknownProfileMessage(scope.ProfileKey)));
        }

        var incumbent = SyncProfileDashboardMetricsService.IsIncumbent(profile.ProfileKey);
        window = Clean(window);
        environment = Clean(environment);
        clubId = Clean(clubId);
        if (!TrySince(window, out var since, out var windowError))
        {
            return (400, Describe(new DashboardMetricsResult("error", Clean(domain), chart, 0, [], windowError), profile, incumbent));
        }

        var selectedServices = profile.Services
            .Where(s => !string.IsNullOrWhiteSpace(s.Key))
            .ToList();
        if (!IsAll(domain))
        {
            var service = profile.FindService(domain);
            if (service is null)
            {
                return (400, Describe(
                    new DashboardMetricsResult("error", domain!.Trim(), chart, 0, [], $"domain must be a service of profile '{profile.ProfileKey}'"),
                    profile, incumbent));
            }
            selectedServices = [service];
        }

        string? warning = null;
        if (!incumbent && clubId is not null)
        {
            // Non-incumbent charts are stored per service and step, not per entity.
            warning = "Metrics are per service and step; the entity filter does not apply.";
            clubId = null;
        }

        var points = new List<DashboardMetricPoint>();
        foreach (var service in selectedServices)
        {
            if (ChartsFor(service, incumbent).Count == 0) continue;
            points.AddRange(await profileMetricStore.GetProfilePointsAsync(
                profile.ProfileKey, service.Key.Trim(), chart, since, environment, clubId, cancellationToken));
        }

        var result = new DashboardMetricsResult("sql", IsAll(domain) ? null : selectedServices[0].Key, chart, points.Count, points)
        {
            Warning = warning,
        };
        return (200, Describe(result, profile, incumbent));
    }

    private static DashboardMetricsResult Describe(DashboardMetricsResult result, CompanyProfile profile, bool incumbent) =>
        result with
        {
            ProfileKey = profile.ProfileKey,
            Labels = new DashboardLabels(profile.Labels?.Service ?? "Service", profile.Labels?.Step ?? "Step"),
            Services = profile.Services
                .Where(s => !string.IsNullOrWhiteSpace(s.Key))
                .Select(s => DashboardProfileCharts.Describe(s, ChartsFor(s, incumbent)))
                .ToList(),
        };

    /// <summary>The incumbent keeps its per-vertical chart set; every other company gets both charts on every service.</summary>
    private static IReadOnlyList<string> ChartsFor(ServiceDefinition service, bool incumbent) =>
        incumbent ? DashboardCharts.ForDomain(service.Key.Trim()) : DashboardProfileCharts.All;

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
