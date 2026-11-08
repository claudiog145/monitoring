using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Domain;
using Microsoft.Extensions.Logging;

namespace JourneyMonitor.Core.Monitoring.Kpi;

/// <summary>
/// Contract v4 §5 (AG1): a KPI's Domain must be a <c>Services[].Key</c> of its own profile (case-insensitive).
///
/// Amendment A4 incumbent exception: for <see cref="KpiProfiles.Default"/> only, <see cref="KpiDomains.All"/> stays
/// accepted. One of the incumbent's seed domains predates its profile and adding it as a service is impossible without
/// changing its Scout board: the validator requires every service to declare at least one step with a non-blank
/// matcher, and every such step becomes a new arm of the profile-wide classifier the board renders.
/// </summary>
public static class KpiDomainPolicy
{
    /// <summary>monitoring.KpiDefinition.Domain and usp_UpsertKpiDefinition @Domain are nvarchar(32).</summary>
    public const int MaxStoredLength = 32;

    public const string TooLongMessage = "Domain must be at most 32 characters.";

    public static string NotAServiceMessage(string domain, string profileKey) =>
        $"Domain '{domain}' is not a service of profile '{profileKey}'.";

    public static string NotAServiceWarning(KpiDefinition definition) =>
        $"{definition.Kpi}/{definition.Domain} [{definition.ProfileKey}]: domain is not a service of the profile";

    public static bool IsIncumbentSeedDomain(string? profileKey, string? domain) =>
        string.Equals(profileKey?.Trim(), KpiProfiles.Default, StringComparison.OrdinalIgnoreCase)
        && KpiDomains.All.Contains(domain?.Trim() ?? "", StringComparer.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="domain"/> is a service of <paramref name="profile"/> (null = no such profile).</summary>
    public static bool IsAllowed(CompanyProfile? profile, string? profileKey, string? domain)
    {
        if (IsIncumbentSeedDomain(profileKey, domain)) return true;
        var key = domain?.Trim() ?? "";
        if (key.Length == 0 || profile?.Services is null) return false;
        return profile.Services.Any(s => s is not null && string.Equals(s.Key?.Trim(), key, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// Checks KPI domains against <see cref="ICompanyProfileStore"/>, reading each profile once per instance (one per run).
/// Without a store (bare test hosts) every domain is accepted, as before AG1.
/// </summary>
public sealed class KpiDomainGuard(ICompanyProfileStore? profiles, ILogger? logger = null)
{
    private readonly Dictionary<string, CompanyProfile?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public async Task<bool> IsAllowedAsync(string? profileKey, string? domain, CancellationToken ct = default)
    {
        if (profiles is null) return true;
        if (KpiDomainPolicy.IsIncumbentSeedDomain(profileKey, domain)) return true;

        var key = profileKey?.Trim() ?? "";
        if (!_cache.TryGetValue(key, out var profile))
        {
            try
            {
                profile = key.Length == 0 ? null : await profiles.GetAsync(key, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Fail open: an unreadable profile store must not turn into "unknown domain" for every KPI.
                logger?.LogWarning("KPI domain check skipped: the company profile store could not be read ({ErrorType}).", ex.GetType().Name);
                return true;
            }
            _cache[key] = profile;
        }
        return KpiDomainPolicy.IsAllowed(profile, key, domain);
    }

    public Task<bool> IsAllowedAsync(KpiDefinition definition, CancellationToken ct = default) =>
        IsAllowedAsync(definition.ProfileKey, definition.Domain, ct);
}
