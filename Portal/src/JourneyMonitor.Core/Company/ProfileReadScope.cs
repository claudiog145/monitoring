using JourneyMonitor.Core.Monitoring.Kpi;

namespace JourneyMonitor.Core.Company;

/// <summary>
/// Contract v6 §1/§5: the company an existing portal read is about. <see cref="ProfileKey"/> null means
/// every company (<c>?profileKey=*</c>, or a host with no notion of an active company).
/// A row stored without a profile (written before the column existed) belongs to the incumbent, the same
/// rule §3 applies to the tables that gained a ProfileKey.
/// </summary>
public sealed record ProfileReadScope(string? ProfileKey)
{
    public static readonly ProfileReadScope Everything = new((string?)null);

    public bool IsEverything => ProfileKey is null;

    /// <summary>True for the incumbent and for "every company": the legacy pipelines only ever held incumbent data.</summary>
    public bool IncludesIncumbent => ProfileKey is null || IsIncumbentKey(ProfileKey);

    /// <summary>The value echoed back to the caller: the profile key, or "*" for every company.</summary>
    public string Label => ProfileKey ?? KpiProfiles.All;

    public static bool IsIncumbentKey(string? profileKey) =>
        string.Equals(OwnerOf(profileKey), KpiProfiles.Default, StringComparison.OrdinalIgnoreCase);

    /// <summary>The company a stored row belongs to: blank is the incumbent.</summary>
    public static string OwnerOf(string? rowProfileKey) =>
        string.IsNullOrWhiteSpace(rowProfileKey) ? KpiProfiles.Default : rowProfileKey.Trim();

    /// <summary>Whether a company-owned row (blank = incumbent) belongs to this scope.</summary>
    public bool Includes(string? rowProfileKey) =>
        ProfileKey is null || string.Equals(OwnerOf(rowProfileKey), ProfileKey, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a row that may be global (blank = shown to every company) belongs to this scope.</summary>
    public bool IncludesOrGlobal(string? rowProfileKey) =>
        ProfileKey is null
        || string.IsNullOrWhiteSpace(rowProfileKey)
        || string.Equals(rowProfileKey.Trim(), ProfileKey, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves <c>?profileKey=</c>: missing → the active company, "*" → every company, otherwise the named
    /// company if it exists (unknown → <see cref="KpiProfileScope.Error"/>). Without a resolver the host has no
    /// active company, so a missing key keeps the pre-multi-company meaning (every company) and a named key is
    /// taken as-is.
    /// </summary>
    public static async Task<(ProfileReadScope? Scope, string? Error)> ResolveAsync(
        KpiProfileResolver? resolver, string? requested, CancellationToken ct = default)
    {
        var key = (requested ?? "").Trim();
        if (resolver is null)
        {
            return (key.Length == 0 || key == KpiProfiles.All ? Everything : new ProfileReadScope(key), null);
        }

        var resolved = await resolver.ResolveReadAsync(key, ct).ConfigureAwait(false);
        return resolved.IsValid ? (new ProfileReadScope(resolved.ProfileKey), null) : (null, resolved.Error);
    }
}
