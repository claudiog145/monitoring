using JourneyMonitor.Core.Domain;
using JourneyMonitor.Infrastructure.Sql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace JourneyMonitor.Infrastructure.DependencyInjection;

/// <summary>
/// Contract v6 §4.5 (MC3): the per-company overview composition.
/// Called from the Functions composition root; registrations land here so each task owns its own file.
/// </summary>
public static class ProfileOverviewServiceCollectionExtensions
{
    public static IServiceCollection AddProfileOverview(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Without a journey store there are no per-company rows to compose: the overview keeps its legacy path.
        if (!ProfileAnalyticsServiceCollectionExtensions.IsJourneyStoreConfigured(configuration)) return services;

        services.TryAddSingleton<IProfileOverviewReader, SqlOverviewReader>();
        return services;
    }
}