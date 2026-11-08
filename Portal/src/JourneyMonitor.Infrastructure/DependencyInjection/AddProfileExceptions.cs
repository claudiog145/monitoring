using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring.Kpi;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Core.UseCases;
using JourneyMonitor.Infrastructure.ProfileExceptions;
using JourneyMonitor.Infrastructure.Sql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace JourneyMonitor.Infrastructure.DependencyInjection;

/// <summary>
/// Contract v6 §4.3 (MC2): per-company exception ingestion and the profile-filtered incident reads.
/// Called from the Functions composition root; registrations land here so each task owns its own file.
/// The exception source is scoped because it reads through the scoped <c>ITelemetrySource</c>.
/// </summary>
public static class ProfileExceptionsServiceCollectionExtensions
{
    public static IServiceCollection AddProfileExceptions(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Without an incident store there is nothing to ingest into or read from: every company reads empty.
        if (!IsIncidentStoreConfigured(configuration)) return services;

        services.Configure<ProfileExceptionsOptions>(configuration.GetSection(ProfileExceptionsOptions.SectionName));
        services.TryAddSingleton<IProfileIncidentStore, SqlIncidentStore>();
        services.TryAddSingleton<IProfileIncidentReader, SqlIncidentReader>();
        services.TryAddScoped<IProfileExceptionSource, AppInsightsProfileExceptionSource>();
        services.TryAddScoped<KpiProfileResolver>();
        services.TryAddScoped<SyncProfileExceptionsService>();
        return services;
    }

    private static bool IsIncidentStoreConfigured(IConfiguration configuration) =>
        !string.IsNullOrWhiteSpace(configuration[JourneyStoreOptions.AppConfigKey])
        || !string.IsNullOrWhiteSpace(configuration.GetSection(JourneyStoreOptions.SectionName)["ConnectionString"]);
}