using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Configuration;
using JourneyMonitor.Core.Options;

namespace JourneyMonitor.Core.UseCases;

/// <summary>A profile's telemetry cannot be read (no readable source, or no app to run the query against).</summary>
public sealed class ProfileTelemetryUnavailableException(string message) : Exception(message);

/// <summary>
/// What a profile may read: the profile cut down to the services whose every alias is readable, and
/// the app the query is executed against. <paramref name="SkippedServices"/> explains each service left out.
/// </summary>
public sealed record ProfileTelemetryScope(
    CompanyProfile Profile,
    string AppId,
    string? TenantId,
    IReadOnlyList<string> SkippedServices);

/// <summary>
/// Contract v6 §4.1/§4.2 source resolution for per-profile reads. A service is readable when it
/// declares aliases and each one is owned by the profile or shared (alias ownership, FX1), and is
/// registered, enabled and an Application Insights resource in the environment - so
/// <c>app("{{SOURCE:alias}}")</c> resolves. The query runs against the AppId of the first readable
/// alias; only the incumbent profile may fall back to <see cref="InsightsOptions"/>.
/// </summary>
public static class ProfileJourneyTelemetryAccess
{
    public static async Task<ProfileTelemetryScope> ResolveAsync(
        CompanyProfile profile,
        TelemetrySourceOwnershipCheck ownership,
        string? environment,
        InsightsOptions? insights,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(ownership);

        string env;
        try
        {
            env = InsightsEnvironment.Normalize(environment);
        }
        catch (InsightsConfigurationException ex)
        {
            throw new ProfileTelemetryUnavailableException(ex.Message);
        }

        IReadOnlyList<TelemetrySourceEntry> catalog;
        try
        {
            catalog = await ownership.GetCatalogAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ProfileTelemetryUnavailableException($"The telemetry source catalog could not be read: {ex.Message}");
        }

        return Resolve(profile, catalog, env, insights);
    }

    public static ProfileTelemetryScope Resolve(
        CompanyProfile profile,
        IReadOnlyList<TelemetrySourceEntry> catalog,
        string environment,
        InsightsOptions? insights)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(catalog);

        var usable = new List<ServiceDefinition>();
        var skipped = new List<string>();
        foreach (var service in profile.Services ?? [])
        {
            var problem = ProblemOf(profile.ProfileKey, service, catalog, environment);
            if (problem is null) usable.Add(service);
            else skipped.Add($"{service.Key}: {problem}");
        }

        if (usable.Count == 0)
        {
            throw new ProfileTelemetryUnavailableException(
                $"No readable telemetry source for profile '{profile.ProfileKey}'" +
                (skipped.Count > 0 ? " (" + string.Join("; ", skipped) + ")." : "."));
        }

        var scoped = profile with { Services = usable };
        foreach (var alias in usable.SelectMany(s => s.TelemetrySourceAliases).Select(a => a.Trim()))
        {
            var entry = EntryOf(catalog, alias, environment);
            if (entry is not null && !string.IsNullOrWhiteSpace(entry.AppId))
            {
                return new ProfileTelemetryScope(
                    scoped,
                    entry.AppId.Trim(),
                    string.IsNullOrWhiteSpace(entry.TenantId) ? null : entry.TenantId.Trim(),
                    skipped);
            }
        }

        if (insights is not null && string.Equals(profile.ProfileKey?.Trim(), KpiProfiles.Default, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var service in usable)
            {
                try
                {
                    var source = InsightsSourceResolver.ResolveForDomain(insights, service.Key.Trim().ToLowerInvariant(), environment);
                    if (!string.IsNullOrWhiteSpace(source.AppInsightsAppId))
                    {
                        return new ProfileTelemetryScope(
                            scoped,
                            source.AppInsightsAppId.Trim(),
                            string.IsNullOrWhiteSpace(source.TenantId) ? null : source.TenantId.Trim(),
                            skipped);
                    }
                }
                catch (InsightsConfigurationException)
                {
                    // Not configured for this service; try the next one.
                }
            }
        }

        throw new ProfileTelemetryUnavailableException(
            $"No telemetry source of profile '{profile.ProfileKey}' has an Application Insights AppId in environment '{environment}'.");
    }

    private static string? ProblemOf(string profileKey, ServiceDefinition service, IReadOnlyList<TelemetrySourceEntry> catalog, string environment)
    {
        var aliases = (service.TelemetrySourceAliases ?? [])
            .Select(a => a?.Trim() ?? "")
            .Where(a => a.Length > 0)
            .ToList();
        if (aliases.Count == 0) return "declares no telemetry source alias";

        var foreign = TelemetrySourceOwnership.FindForeignOwners(catalog, profileKey, aliases);
        if (foreign.Count > 0) return string.Join(" ", foreign.Select(f => f.Message));

        foreach (var alias in aliases)
        {
            var entry = EntryOf(catalog, alias, environment);
            if (entry is null) return $"alias \"{alias}\" is not registered for environment \"{environment}\"";
            if (!entry.Enabled) return $"alias \"{alias}\" is disabled for environment \"{environment}\"";
            if (!InsightsSourceResolver.IsInsightsResourceId(entry.ResourceId))
                return $"alias \"{alias}\" ({environment}) does not hold an Application Insights resource id";
        }
        return null;
    }

    private static TelemetrySourceEntry? EntryOf(IReadOnlyList<TelemetrySourceEntry> catalog, string alias, string environment) =>
        catalog.FirstOrDefault(s =>
            string.Equals(s.Alias?.Trim(), alias, StringComparison.OrdinalIgnoreCase)
            && string.Equals(s.Environment?.Trim(), environment, StringComparison.OrdinalIgnoreCase));
}
