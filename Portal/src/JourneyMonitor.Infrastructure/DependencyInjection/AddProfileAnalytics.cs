using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Core.UseCases;
using JourneyMonitor.Infrastructure.ProfileAnalytics;
using JourneyMonitor.Infrastructure.Sql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace JourneyMonitor.Infrastructure.DependencyInjection;

/// <summary>
/// Contract v6 §4.4 (MC3): the per-company dashboard metric sync and the profile-filtered analytics reads.
/// Called from the Functions composition root; registrations land here so each task owns its own file.
/// </summary>
public static class ProfileAnalyticsServiceCollectionExtensions
{
    public static IServiceCollection AddProfileAnalytics(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Without a journey store there is nothing to sync into or read from: analytics keeps its legacy path.
        if (!IsJourneyStoreConfigured(configuration)) return services;

        services.AddOptions<ProfileAnalyticsOptions>().Bind(configuration.GetSection(ProfileAnalyticsOptions.SectionName));
        services.TryAddSingleton<SqlDashboardMetricStore>();
        services.TryAddSingleton<IProfileDashboardMetricStore>(sp => sp.GetRequiredService<SqlDashboardMetricStore>());
        services.TryAddScoped<IProfileDashboardTelemetry, AppInsightsProfileDashboardTelemetry>();
        services.TryAddScoped<SyncProfileDashboardMetricsService>();
        return services;
    }

    internal static bool IsJourneyStoreConfigured(IConfiguration configuration) =>
        !string.IsNullOrWhiteSpace(configuration[JourneyStoreOptions.AppConfigKey])
        || !string.IsNullOrWhiteSpace(configuration.GetSection(JourneyStoreOptions.SectionName)["ConnectionString"]);
}