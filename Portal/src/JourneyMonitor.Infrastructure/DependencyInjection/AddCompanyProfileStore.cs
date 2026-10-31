using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Infrastructure.Monitoring;
using JourneyMonitor.Infrastructure.Sql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace JourneyMonitor.Infrastructure.DependencyInjection;

/// <summary>
/// Registers <see cref="ICompanyProfileStore"/>. Split into its own extension rather than folded
/// into AddJourneyMonitorMonitoring so it can be called independently; a composition root calls
/// this once, after AddJourneyMonitorMonitoring.
/// </summary>
public static class CompanyProfileServiceCollectionExtensions
{
    /// <summary>
    /// SQL-backed when JourneyStore:ConnectionString is configured AND
    /// monitoring.CompanyProfile has actually been provisioned on that database; in-memory
    /// otherwise, so a fresh clone still runs.
    /// </summary>
    public static IServiceCollection AddCompanyProfileStore(this IServiceCollection services, IConfiguration configuration)
    {
        // Harmless if a caller already did it - Configure is additive, and binding the same
        // section twice produces the same options. Registering it here keeps this extension
        // callable on its own rather than silently depending on call order.
        services.Configure<JourneyStoreOptions>(configuration.GetSection(JourneyStoreOptions.SectionName));

        // Same precedence as AddSqlJourneyStore and AddJourneyMonitorMonitoring: App
        // Configuration's key first, then the local section. A blank AppConfig value (env var
        // present but empty) counts as absent - "??" alone would stop at it.
        var appConfigConnectionString = configuration[JourneyStoreOptions.AppConfigKey];
        var journeyStoreConnectionString = !string.IsNullOrWhiteSpace(appConfigConnectionString)
            ? appConfigConnectionString
            : configuration.GetSection(JourneyStoreOptions.SectionName)["ConnectionString"];

        // A connection string being set is not proof the script ran - see MonitoringSchemaGate.
        // Connectivity/auth failures are deliberately NOT caught: a transient outage at startup
        // must not silently pin this process to in-memory profiles forever.
        var useSqlStore = !string.IsNullOrWhiteSpace(journeyStoreConnectionString)
            && MonitoringSchemaGate.IsCompanyProfileProvisioned(journeyStoreConnectionString);

        if (!string.IsNullOrWhiteSpace(journeyStoreConnectionString) && !useSqlStore)
        {
            Console.Error.WriteLine(
                "[JourneyMonitor] JourneyStore:ConnectionString is set but monitoring.CompanyProfile " +
                "(Sql/monitoring/CreateMonitoringCompanyProfile.sql) was not found on that database - " +
                "company profiles are process-local, a company onboarded in the portal is lost on " +
                "restart, and there is no audit of who changed the mapping that classifies every alert.");
        }

        if (useSqlStore)
        {
            services.AddSingleton<ICompanyProfileStore, SqlCompanyProfileStore>();
        }
        else
        {
            services.AddSingleton<ICompanyProfileStore, InMemoryCompanyProfileStore>();
        }

        return services;
    }
}
