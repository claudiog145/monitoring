using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Company;

namespace JourneyMonitor.Core.Domain;

/// <summary>
/// Contract v6 §4.4. dashboard.MetricPoint rows of ONE company: <c>Domain</c> is the profile's service key
/// and <c>Series</c> its step key. Every call names the profile, so two companies never share a read.
/// </summary>
public interface IProfileDashboardMetricStore
{
    Task ReplaceProfileChartAsync(
        string profileKey,
        string serviceKey,
        string chart,
        string window,
        string? environment,
        IReadOnlyList<DashboardMetricPoint> points,
        CancellationToken cancellationToken);

    /// <summary>Points of <paramref name="profileKey"/> only; a null service reads every service of that profile.</summary>
    Task<IReadOnlyList<DashboardMetricPoint>> GetProfilePointsAsync(
        string profileKey,
        string? serviceKey,
        string? chart,
        DateTimeOffset? since,
        string? environment,
        string? clubId,
        CancellationToken cancellationToken);
}

/// <summary>One time-binned value of one step of one service, as the telemetry returned it.</summary>
public sealed record ProfileStepMetric(string Chart, string StepKey, DateTimeOffset BinStart, decimal Value);

/// <summary>Contract v6 §4.4: the telemetry half of the per-company dashboard sync (KQL rendered with the profile).</summary>
public interface IProfileDashboardTelemetry
{
    /// <summary>
    /// P95 latency per step and exceptions per step of one service, classified by the profile's own
    /// first-match-wins step cascade. Throws when the service has no readable telemetry source.
    /// </summary>
    Task<IReadOnlyList<ProfileStepMetric>> QueryServiceAsync(
        CompanyProfile profile,
        ServiceDefinition service,
        DateTimeOffset fromInclusive,
        DateTimeOffset toExclusive,
        TimeSpan binSize,
        string environment,
        CancellationToken cancellationToken);
}

/// <summary>Contract v6 §4.4 response: one step of a service, in display order.</summary>
public sealed record DashboardStep(string Key, string DisplayName, int Order);

/// <summary>Contract v6 §4.4 response: a service of the profile, its charts and steps, so the UI needs no fixed names.</summary>
public sealed record DashboardService(
    string Key,
    string DisplayName,
    IReadOnlyList<string> Charts,
    IReadOnlyList<DashboardStep> Steps);

/// <summary>What the profile calls a service and a step (column and axis titles).</summary>
public sealed record DashboardLabels(string Service, string Step);

/// <summary>Outcome of one chart of one service in one sync run.</summary>
public sealed record ProfileDashboardSyncPass(
    string ProfileKey,
    string ServiceKey,
    string Chart,
    bool Stored,
    int PointCount,
    string? SkipReason);

/// <summary>Contract v6 §4.4: knobs of the per-company dashboard sync (section <c>ProfileAnalytics</c>).</summary>
public sealed class ProfileAnalyticsOptions
{
    public const string SectionName = "ProfileAnalytics";

    public bool Enabled { get; set; } = true;

    /// <summary>How far back each run reads. Older bins stay stored (the store merges).</summary>
    public string Window { get; set; } = "6h";

    public string BinSize { get; set; } = "5m";
}

public static class DashboardProfileCharts
{
    /// <summary>Both charts exist for every service of a non-incumbent profile.</summary>
    public static readonly IReadOnlyList<string> All = [DashboardCharts.StepLatencyP95, DashboardCharts.ExceptionsByStep];

    /// <summary>dashboard.MetricPoint.Domain is nvarchar(32): a longer service key cannot be stored.</summary>
    public const int MaxServiceKeyLength = 32;

    /// <summary>dashboard.MetricPoint.Series is nvarchar(128).</summary>
    public const int MaxStepKeyLength = 128;

    public static DashboardService Describe(ServiceDefinition service, IReadOnlyList<string> charts) =>
        new(
            service.Key,
            string.IsNullOrWhiteSpace(service.DisplayName) ? service.Key : service.DisplayName,
            charts,
            (service.Flows ?? [])
                .SelectMany(flow => flow.Steps ?? [])
                .Where(step => !string.IsNullOrWhiteSpace(step.Key))
                .GroupBy(step => step.Key, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(step => step.Order)
                .Select(step => new DashboardStep(step.Key, string.IsNullOrWhiteSpace(step.DisplayName) ? step.Key : step.DisplayName, step.Order))
                .ToList());
}
