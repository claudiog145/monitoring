using System.Globalization;
using System.Text.Json;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Configuration;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Filtering;
using JourneyMonitor.Core.Options;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Core.UseCases;

public sealed record DashboardMetricSyncPass(string Domain, string Chart, bool Stored, int PointCount, string? SkipReason);

/// <summary>
/// Runs the copied dashboard-tile KQL (not the journey KQL) and writes the time-binned
/// points. Cars/Air are skipped until those tiles are mapped.
/// </summary>
public sealed class SyncDashboardMetricsService(
    ITelemetrySource telemetrySource,
    IKqlQueryTextProvider kqlProvider,
    IKqlSourceResolver sourceResolver,
    IDashboardMetricStore metricStore,
    ClubHostResolver clubHostResolver,
    IOptions<InsightsOptions> insightsOptions)
{
    public async Task<IReadOnlyList<DashboardMetricSyncPass>> SyncDomainAsync(
        JourneySyncScope scope,
        CancellationToken cancellationToken)
    {
        var charts = DashboardCharts.ForDomain(scope.Domain);
        if (charts.Count == 0)
        {
            return [new DashboardMetricSyncPass(scope.Domain, "", false, 0, "no dashboard charts mapped for this domain yet")];
        }

        var passes = new List<DashboardMetricSyncPass>();
        foreach (var chart in charts)
        {
            passes.Add(await SyncChartAsync(scope, chart, cancellationToken));
        }
        return passes;
    }

    /// <summary>
    /// One KQL call per chart for the whole vertical. Hotel points are keyed by host and copied
    /// onto every club on that host. Cruise points are keyed by partner id.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<DashboardMetricSyncPass>>> SyncVerticalAsync(
        string domain,
        string window,
        string environment,
        IReadOnlyList<ClubOption> clubs,
        CancellationToken cancellationToken)
    {
        var normalized = domain.Trim().ToLowerInvariant();
        var charts = DashboardCharts.ForDomain(normalized);
        var byClub = clubs.ToDictionary(
            club => club.ClubId,
            _ => (IReadOnlyList<DashboardMetricSyncPass>)[],
            StringComparer.Ordinal);
        if (charts.Count == 0 || clubs.Count == 0)
        {
            return byClub;
        }

        var passesByClub = clubs.ToDictionary(club => club.ClubId, _ => new List<DashboardMetricSyncPass>(), StringComparer.Ordinal);
        foreach (var chart in charts)
        {
            var chartPasses = await SyncVerticalChartAsync(normalized, chart, window, environment, clubs, cancellationToken);
            foreach (var (clubId, pass) in chartPasses)
            {
                passesByClub[clubId].Add(pass);
            }
        }

        return passesByClub.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<DashboardMetricSyncPass>)pair.Value,
            StringComparer.Ordinal);
    }

    private async Task<Dictionary<string, DashboardMetricSyncPass>> SyncVerticalChartAsync(
        string domain,
        string chart,
        string window,
        string environment,
        IReadOnlyList<ClubOption> clubs,
        CancellationToken cancellationToken)
    {
        var result = clubs.ToDictionary(club => club.ClubId, _ => new DashboardMetricSyncPass(domain, chart, false, 0, null), StringComparer.Ordinal);
        try
        {
            var source = InsightsSourceResolver.ResolveForDomain(insightsOptions.Value, domain, environment);
            var hosts = clubs.Select(c => c.Host).Where(ClubFilter.IsValidHost).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var partners = clubs.Select(c => BrandClubOptions.UsesPartnerId(domain) ? c.ClubId : c.PartnerId)
                .Where(ClubFilter.IsValidClubId)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var kql = ClubFilter.InjectVerticalDashboardKql(
                domain,
                kqlProvider.GetDashboardChartKql(domain, chart),
                window,
                hosts,
                partners,
                source.ClubResource,
                source.CoreResource);
            var rows = await telemetrySource.QueryAsync(
                new TelemetryQueryRequest(kql, source.AppInsightsAppId, source.TenantId),
                cancellationToken);
            var scope = new JourneySyncScope(domain, window, environment, clubs[0].ClubId, null);
            var pointsByKey = new Dictionary<string, List<DashboardMetricPoint>>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows)
            {
                var key = domain == "cruises" ? row.GetString("PartnerId") : row.GetString("Host");
                var point = MapPoint(scope, chart, row);
                if (string.IsNullOrEmpty(key) || point is null) continue;
                if (!pointsByKey.TryGetValue(key, out var list))
                {
                    list = [];
                    pointsByKey[key] = list;
                }
                list.Add(point);
            }

            foreach (var club in clubs)
            {
                var key = domain == "cruises" ? club.ClubId : club.Host;
                if (domain != "cruises" && !ClubFilter.IsValidHost(key))
                {
                    result[club.ClubId] = new DashboardMetricSyncPass(domain, chart, false, 0, $"no host for club {club.ClubId}");
                    continue;
                }

                var points = pointsByKey.TryGetValue(key, out var matched) ? matched : [];
                try
                {
                    await metricStore.ReplaceChartAsync(domain, chart, window, environment, club.ClubId, points, cancellationToken);
                    result[club.ClubId] = new DashboardMetricSyncPass(domain, chart, true, points.Count, null);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    result[club.ClubId] = new DashboardMetricSyncPass(domain, chart, false, 0, ex.Message);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            foreach (var club in clubs)
            {
                result[club.ClubId] = new DashboardMetricSyncPass(domain, chart, false, 0, ex.Message);
            }
        }

        return result;
    }

    private async Task<DashboardMetricSyncPass> SyncChartAsync(
        JourneySyncScope scope,
        string chart,
        CancellationToken cancellationToken)
    {
        try
        {
            var source = InsightsSourceResolver.ResolveForDomain(insightsOptions.Value, scope.Domain, scope.Environment);
            var host = await clubHostResolver.ResolveAsync(
                scope.Window, scope.ClubId, scope.Domain, scope.Environment, skipCache: true, cancellationToken)
                ?? "";
            if (string.IsNullOrEmpty(host) && ClubFilter.IsValidHost(scope.Host)) host = scope.Host!;
            if (string.IsNullOrEmpty(host))
            {
                return new DashboardMetricSyncPass(scope.Domain, chart, false, 0, $"no host for club {scope.ClubId}");
            }

            var resolvedPartnerId = await clubHostResolver.ResolvePartnerAsync(
                scope.ClubId, scope.Domain, cancellationToken);
            var partnerId = ClubFilter.IsValidClubId(resolvedPartnerId)
                ? resolvedPartnerId
                : ClubFilter.IsValidClubId(scope.PartnerId) ? scope.PartnerId : null;

            var kql = ClubFilter.InjectDashboardKql(
                kqlProvider.GetDashboardChartKql(scope.Domain, chart),
                scope.Window,
                scope.ClubId,
                host,
                source.ClubResource,
                source.CoreResource,
                partnerId);

            kql = await sourceResolver.ResolveAsync(kql, scope.Environment, cancellationToken);
            var rows = await telemetrySource.QueryAsync(
                new TelemetryQueryRequest(kql, source.AppInsightsAppId, source.TenantId),
                cancellationToken);

            var points = rows
                .Select(row => MapPoint(scope, chart, row))
                .Where(p => p is not null)
                .Select(p => p!)
                .ToList();

            await metricStore.ReplaceChartAsync(
                scope.Domain, chart, scope.Window, scope.Environment, scope.ClubId, points, cancellationToken);

            return new DashboardMetricSyncPass(scope.Domain, chart, true, points.Count, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new DashboardMetricSyncPass(scope.Domain, chart, false, 0, ex.Message);
        }
    }

    private static DashboardMetricPoint? MapPoint(JourneySyncScope scope, string chart, TelemetryJourneyRow row)
    {
        var series = FirstNonEmpty(row, "Series", "Stage", "Operation");
        var bin = ParseBin(row, "timestamp", "Time", "BinStart");
        var value = row.GetDouble("P95LatencyMs") ?? row.GetDouble("ExceptionCount") ?? row.GetDouble("Value");
        if (string.IsNullOrEmpty(series) || bin is null || value is null)
        {
            return null;
        }

        return new DashboardMetricPoint(
            scope.Domain,
            chart,
            series,
            bin.Value,
            Convert.ToDecimal(value.Value),
            scope.Window,
            scope.Environment,
            DateTimeOffset.UtcNow);
    }

    private static string FirstNonEmpty(TelemetryJourneyRow row, params string[] names)
    {
        foreach (var name in names)
        {
            var value = row.GetString(name);
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return "";
    }

    private static DateTimeOffset? ParseBin(TelemetryJourneyRow row, params string[] names)
    {
        foreach (var name in names)
        {
            var el = row.Get(name);
            if (el is null) continue;
            if (el.Value.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(el.Value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var fromString))
            {
                return fromString;
            }
            if (el.Value.ValueKind == JsonValueKind.Number && el.Value.TryGetInt64(out var ms))
            {
                // Kusto REST sometimes emits datetime as milliseconds.
                if (ms > 10_000_000_000) return DateTimeOffset.FromUnixTimeMilliseconds(ms);
            }
        }
        return null;
    }
}
