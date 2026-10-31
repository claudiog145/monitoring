using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Infrastructure.Sql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace JourneyMonitor.Infrastructure.DependencyInjection;

/// <summary>
/// Opts a composition root into answering company questions from a stored
/// <see cref="CompanyProfile"/> instead of from code.
///
/// Kept as its own extension rather than folded into <c>AddJourneyMonitorMonitoring</c> so the
/// switch is a single explicit call a deployment can make or not make - which is what lets the
/// code-driven and profile-driven implementations be compared in the same build instead of one
/// replacing the other in a commit nobody can back out of.
/// </summary>
public static class ProfileDrivenCompanyServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="CompanyAdapterProvider"/> as the <see cref="ICompanyAdapterProvider"/>.
    /// Called last wins, so this overrides any earlier registration on purpose.
    ///
    /// Does NOT register <see cref="ICompanyProfileStore"/>: that is storage, it belongs with the
    /// other store registrations, and binding it here would give this method a second reason to
    /// change. <see cref="IActiveCompanyProfileStore"/> IS registered here, unlike the profile
    /// store, because it is not storage of a company - it is the resolution rule this extension
    /// exists to install, and the provider is its only consumer.
    /// </summary>
    public static IServiceCollection AddProfileDrivenCompany(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ProfileDrivenCompanyOptions>(configuration.GetSection(ProfileDrivenCompanyOptions.SectionName));

        // Harmless if AddCompanyProfileStore already did it - Configure is additive - and it keeps
        // this extension callable on its own rather than depending on call order.
        services.Configure<JourneyStoreOptions>(configuration.GetSection(JourneyStoreOptions.SectionName));

        // Same precedence as AddCompanyProfileStore: App Configuration's key first, then the local
        // section, with a blank AppConfig value counting as absent.
        var appConfigConnectionString = configuration[JourneyStoreOptions.AppConfigKey];
        var journeyStoreConnectionString = !string.IsNullOrWhiteSpace(appConfigConnectionString)
            ? appConfigConnectionString
            : configuration.GetSection(JourneyStoreOptions.SectionName)["ConnectionString"];

        // There is no in-memory counterpart on purpose. A process-local "active company" would be
        // a switch that silently un-flipped on restart and disagreed between the Functions host
        // and the BFF - worse than not offering the switch at all. Leaving the service
        // unregistered is a supported state: CompanyAdapterProvider takes it as an optional
        // dependency and keeps today's Company:DefaultProfileKey behaviour without it.
        var useSqlStore = !string.IsNullOrWhiteSpace(journeyStoreConnectionString)
            && SqlActiveCompanyProfileStore.IsProvisioned(journeyStoreConnectionString);

        if (!string.IsNullOrWhiteSpace(journeyStoreConnectionString) && !useSqlStore)
        {
            Console.Error.WriteLine(
                "[JourneyMonitor] JourneyStore:ConnectionString is set but monitoring.ActiveCompanyProfile " +
                "(Sql/monitoring/CreateMonitoringCompanyProfile.sql) was not found on that database - " +
                "the monitored company cannot be switched from the portal and falls back to " +
                "Company:DefaultProfileKey, which only a redeploy can change.");
        }

        if (useSqlStore)
        {
            services.AddSingleton<IActiveCompanyProfileStore, SqlActiveCompanyProfileStore>();
        }

        services.AddSingleton<ICompanyAdapterProvider, CompanyAdapterProvider>();
        return services;
    }
}

