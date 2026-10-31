using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Onboarding;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace JourneyMonitor.Infrastructure.DependencyInjection;

/// <summary>
/// Registers the onboarding telemetry profiler. Kept out of
/// <see cref="ServiceCollectionExtensions"/> so a composition root can leave it off: it is an
/// onboarding tool, not part of the monitoring loop, and the endpoint it backs issues outbound
/// queries against a caller-named resource.
///
/// Depends on <c>ITelemetrySource</c>, <c>ITelemetrySourceStore</c> and <c>IKqlSourceResolver</c>
/// being registered already (AddJourneyMonitorInfrastructure / AddJourneyMonitorMonitoring).
/// </summary>
public static class TelemetryDiscoveryServiceCollectionExtensions
{
    public static IServiceCollection AddTelemetryDiscovery(this IServiceCollection services)
    {
        // Scoped, matching every other ITelemetrySource consumer here: the Application Insights
        // source is an HttpClient-typed transient, and holding one for the process lifetime would
        // pin a single handler. The explicit factory keeps TimeProvider optional rather than
        // forcing a registration another track may also want to own.
        services.AddScoped<ITelemetryResourceProfiler>(sp => new TelemetryResourceProfiler(
            sp.GetRequiredService<ITelemetrySource>(),
            sp.GetRequiredService<ITelemetrySourceStore>(),
            sp.GetRequiredService<IKqlSourceResolver>(),
            sp.GetRequiredService<ILogger<TelemetryResourceProfiler>>(),
            sp.GetService<TimeProvider>()));

        return services;
    }
}
