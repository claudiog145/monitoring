using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Domain;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Core.Monitoring.Kpi;

/// <summary>The profile a KPI/triage HTTP request is about, or why it was rejected.</summary>
/// <param name="ProfileKey">The profile to filter on; null means every profile (<c>?profileKey=*</c>).</param>
public sealed record KpiProfileScope(string? ProfileKey, string? Error)
{
    public bool IsValid => Error is null;
}

/// <summary>
/// Contract v3 §1.5: KPI settings, pillar health, anomalies and triage default to the active company
/// profile (IActiveCompanyProfileStore, then Company:DefaultProfileKey, then the incumbent profile).
/// An explicitly named profile must exist in ICompanyProfileStore. "*" selects every profile on reads.
/// The daemons do not use this: they always process every profile.
/// </summary>
public sealed class KpiProfileResolver(
    IOptions<ProfileDrivenCompanyOptions>? options = null,
    IActiveCompanyProfileStore? activeStore = null,
    ICompanyProfileStore? profileStore = null)
{
    public static string UnknownProfileMessage(string profileKey) => $"Unknown profileKey '{profileKey}'.";

    /// <summary>The active profile key, never blank.</summary>
    public async Task<string> GetActiveAsync(CancellationToken ct = default)
    {
        if (activeStore is not null)
        {
            var active = (await activeStore.GetActiveAsync(ct).ConfigureAwait(false) ?? "").Trim();
            if (active.Length > 0) return active;
        }

        var configured = (options?.Value.DefaultProfileKey ?? "").Trim();
        return configured.Length > 0 ? configured : KpiProfiles.Default;
    }

    /// <summary>Read scope: missing → active profile, "*" → every profile, otherwise the named profile if it exists.</summary>
    public async Task<KpiProfileScope> ResolveReadAsync(string? requested, CancellationToken ct = default)
    {
        var key = (requested ?? "").Trim();
        if (key == KpiProfiles.All) return new KpiProfileScope(null, null);
        return await ResolveWriteAsync(key, ct).ConfigureAwait(false);
    }

    /// <summary>Write scope: missing → active profile, otherwise the named profile if it exists. Never "every profile".</summary>
    public async Task<KpiProfileScope> ResolveWriteAsync(string? requested, CancellationToken ct = default)
    {
        var key = (requested ?? "").Trim();
        if (key.Length == 0) return new KpiProfileScope(await GetActiveAsync(ct).ConfigureAwait(false), null);
        if (key == KpiProfiles.All) return new KpiProfileScope(null, UnknownProfileMessage(key));
        return await ExistsAsync(key, ct).ConfigureAwait(false)
            ? new KpiProfileScope(key, null)
            : new KpiProfileScope(null, UnknownProfileMessage(key));
    }

    /// <summary>
    /// POST /api/settings/kpis: an explicit profileKey is validated (and may move an existing KPI, which the store audits);
    /// without one an existing KPI keeps its stored profile and only a new KPI takes the active profile.
    /// </summary>
    public async Task<KpiProfileScope> ResolveUpsertAsync(string? requested, string? storedProfileKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(requested) && !string.IsNullOrWhiteSpace(storedProfileKey))
            return new KpiProfileScope(storedProfileKey.Trim(), null);
        return await ResolveWriteAsync(requested, ct).ConfigureAwait(false);
    }

    /// <summary>The stored KPI with the same Pillar/Kpi/Domain/Stage, preferring the one of <paramref name="preferredProfileKey"/>.</summary>
    public static KpiDefinition? FindByNaturalKey(IReadOnlyList<KpiDefinition> stored, KpiDefinition candidate, string? preferredProfileKey)
    {
        var matches = stored.Where(d => d.Pillar == candidate.Pillar
                && string.Equals(d.Kpi, candidate.Kpi, StringComparison.OrdinalIgnoreCase)
                && string.Equals(d.Domain, candidate.Domain, StringComparison.OrdinalIgnoreCase)
                && string.Equals(d.Stage, candidate.Stage ?? "", StringComparison.OrdinalIgnoreCase))
            .OrderBy(d => d.KpiDefinitionId)
            .ToList();
        return matches.FirstOrDefault(d => string.Equals(d.ProfileKey, preferredProfileKey, StringComparison.OrdinalIgnoreCase))
            ?? matches.FirstOrDefault();
    }

    private async Task<bool> ExistsAsync(string key, CancellationToken ct)
    {
        // Without a profile store (bare test hosts) only the incumbent and the active profile are known.
        if (profileStore is null)
        {
            return string.Equals(key, KpiProfiles.Default, StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, await GetActiveAsync(ct).ConfigureAwait(false), StringComparison.OrdinalIgnoreCase);
        }

        return await profileStore.GetAsync(key, ct).ConfigureAwait(false) is not null;
    }
}
