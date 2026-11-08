using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Core.UseCases;
using JourneyMonitor.Infrastructure.ProfileJourneys;
using JourneyMonitor.Infrastructure.Sql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace JourneyMonitor.Infrastructure.DependencyInjection;

/// <summary>
/// Contract v6 §4.1 and §4.2 (MC1): the per-company journey sync, IProfileJourneyStore, IProfileJourneyReader and IEntityCatalog.
/// Called from the Functions composition root; registrations land here so each task owns its own file.
/// </summary>
public static class ProfileJourneysServiceCollectionExtensions
{
    /// <summary>
    /// Registers nothing when no journey store is configured (JourneyStore:ConnectionString or the App
    /// Configuration key): the endpoints and the timer then answer "not configured" themselves, like the
    /// other stored reads. With a store, journeys are kept in monitoring.ProfileJourney when that table is
    /// provisioned and in process memory otherwise.
    /// </summary>
    public static IServiceCollection AddProfileJourneys(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var appConfigConnectionString = configuration[JourneyStoreOptions.AppConfigKey];
        var connectionString = !string.IsNullOrWhiteSpace(appConfigConnectionString)
            ? appConfigConnectionString
            : configuration.GetSection(JourneyStoreOptions.SectionName)["ConnectionString"];
        if (string.IsNullOrWhiteSpace(connectionString)) return services;

        services.Configure<ProfileJourneyOptions>(configuration.GetSection(ProfileJourneyOptions.SectionName));
        services.Configure<JourneyStoreOptions>(configuration.GetSection(JourneyStoreOptions.SectionName));
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<TelemetrySourceOwnershipCheck>();

        if (MonitoringSchemaGate.IsProfileJourneyProvisioned(connectionString))
        {
            services.AddSingleton<IProfileJourneyStore, SqlProfileJourneyStore>();
        }
        else
        {
            Console.Error.WriteLine(
                "[JourneyMonitor] monitoring.ProfileJourney (Sql/monitoring/CreateMonitoringProfileJourney.sql) was not found - " +
                "per-company journeys are kept in process memory and lost on restart.");
            services.AddSingleton<IProfileJourneyStore, InMemoryProfileJourneyStore>();
        }

        services.AddSingleton<EntityCatalogCache>();
        services.AddScoped<ProfileJourneySyncService>();
        services.AddScoped<ProfileJourneyReader>();
        services.AddScoped<IProfileJourneyReader>(sp => sp.GetRequiredService<ProfileJourneyReader>());
        services.AddScoped<EntityCatalogService>();
        services.AddScoped<IEntityCatalog>(sp => sp.GetRequiredService<EntityCatalogService>());
        return services;
    }
}
