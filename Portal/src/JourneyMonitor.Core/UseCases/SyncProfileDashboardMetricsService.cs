using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Filtering;
using JourneyMonitor.Core.Options;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Core.UseCases;

/// <summary>
/// Contract v6 §4.4: for every enabled company profile, P95 latency per step and exceptions per step of
/// each of its services land in dashboard.MetricPoint under that profile's key.
///
/// The incumbent is skipped: its charts are written per club by <see cref="SyncDashboardMetricsService"/>
/// (also under the incumbent's key), and service-wide rows next to those would be counted twice by an
/// unfiltered read. One service failing never stops the others, nor another company.
/// </summary>
public sealed class SyncProfileDashboardMetricsService(
    ICompanyProfileStore profiles,
    IProfileDashboardTelemetry telemetry,
    IProfileDashboardMetricStore store,
    IOptions<ProfileAnalyticsOptions> options,
    IOptions<JourneySyncOptions> syncOptions,
    TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public async Task<IReadOnlyList<ProfileDashboardSyncPass>> SyncAllAsync(CancellationToken cancellationToken)
    {
        var passes = new List<ProfileDashboardSyncPass>();
        foreach (var profile in await profiles.GetAllAsync(cancellationToken))
        {
            if (!profile.Enabled || IsIncumbent(profile.ProfileKey)) continue;
            passes.AddRange(await SyncProfileAsync(profile, cancellationToken));
        }
        return passes;
    }

    /// <summary>One profile, by key. Unknown or disabled profiles produce no pass.</summary>
    public async Task<IReadOnlyList<ProfileDashboardSyncPass>> SyncProfileAsync(string profileKey, CancellationToken cancellationToken)
    {
        var profile = await profiles.GetAsync(profileKey, cancellationToken);
        if (profile is null || !profile.Enabled) return [];
        if (IsIncumbent(profile.ProfileKey))
        {
            return [new ProfileDashboardSyncPass(profile.ProfileKey, "", "", false, 0, "the incumbent's charts are synced per club by the journey sync")];
        }
        return await SyncProfileAsync(profile, cancellationToken);
    }

    public static bool IsIncumbent(string? profileKey) =>
        string.Equals(profileKey?.Trim(), KpiProfiles.Default, StringComparison.OrdinalIgnoreCase);

    private async Task<IReadOnlyList<ProfileDashboardSyncPass>> SyncProfileAsync(CompanyProfile profile, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var window = string.IsNullOrWhiteSpace(settings.Window) ? "6h" : settings.Window.Trim();
        if (!ClubFilter.TryParseWindow(window, out var span)) span = TimeSpan.FromHours(6);
        if (!ClubFilter.TryParseWindow(settings.BinSize ?? "", out var bin) || bin <= TimeSpan.Zero) bin = TimeSpan.FromMinutes(5);

        var now = _time.GetUtcNow();
        var to = KpiTime.FloorToBucket(now, TimeSpan.FromMinutes(1)).AddMinutes(1);
        var from = KpiTime.FloorToBucket(to - span, bin);
        var environment = string.IsNullOrWhiteSpace(syncOptions.Value.Environment) ? "production" : syncOptions.Value.Environment.Trim();

        var passes = new List<ProfileDashboardSyncPass>();
        foreach (var service in profile.Services)
        {
            var serviceKey = service.Key?.Trim() ?? "";
            if (serviceKey.Length == 0) continue;
            if (serviceKey.Length > DashboardProfileCharts.MaxServiceKeyLength)
            {
                passes.AddRange(Skipped(profile, serviceKey, $"service key is longer than {DashboardProfileCharts.MaxServiceKeyLength} characters"));
                continue;
            }

            IReadOnlyList<ProfileStepMetric> metrics;
            try
            {
                metrics = await telemetry.QueryServiceAsync(profile, service, from, to, bin, environment, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                passes.AddRange(Skipped(profile, serviceKey, ex.Message));
                continue;
            }

            var knownSteps = DashboardProfileCharts.Describe(service, DashboardProfileCharts.All).Steps
                .Select(step => step.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var fetchedAt = _time.GetUtcNow();
            foreach (var chart in DashboardProfileCharts.All)
            {
                var points = metrics
                    .Where(m => string.Equals(m.Chart, chart, StringComparison.Ordinal)
                        && !string.IsNullOrWhiteSpace(m.StepKey)
                        && m.StepKey.Length <= DashboardProfileCharts.MaxStepKeyLength
                        && (knownSteps.Contains(m.StepKey) || IsFallbackStep(service, m.StepKey)))
                    .Select(m => new DashboardMetricPoint(serviceKey, chart, m.StepKey, m.BinStart, m.Value, window, environment, fetchedAt))
                    .ToList();
                try
                {
                    await store.ReplaceProfileChartAsync(profile.ProfileKey, serviceKey, chart, window, environment, points, cancellationToken);
                    passes.Add(new ProfileDashboardSyncPass(profile.ProfileKey, serviceKey, chart, true, points.Count, null));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    passes.Add(new ProfileDashboardSyncPass(profile.ProfileKey, serviceKey, chart, false, 0, ex.Message));
                }
            }
        }
        return passes;
    }

    private static bool IsFallbackStep(ServiceDefinition service, string stepKey) =>
        !string.IsNullOrWhiteSpace(service.FallbackStep)
        && string.Equals(service.FallbackStep.Trim(), stepKey, StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<ProfileDashboardSyncPass> Skipped(CompanyProfile profile, string serviceKey, string reason) =>
        DashboardProfileCharts.All.Select(chart => new ProfileDashboardSyncPass(profile.ProfileKey, serviceKey, chart, false, 0, reason));
}
