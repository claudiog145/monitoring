using JourneyMonitor.Core.Company;

namespace JourneyMonitor.Core.Domain;

/// <summary>
/// The company a <see cref="Finding"/> belongs to (MCRV H1). Findings are raised over the incumbent's
/// legacy journey pipeline (hotels/cruises/cars/air) - <c>GetFindingsFunction</c> answers "not applicable"
/// for every other company - so a finding is the incumbent's whichever company is active in the portal,
/// and the Doctor's precedents and diagnoses are read from and filed under that company.
/// </summary>
public static class FindingOwnership
{
    /// <param name="incumbentRegistered">
    /// Whether the company registry knows the incumbent. A registry that does not has no legacy pipeline
    /// owner to attribute the finding to, so it stays with the active company (the behaviour before
    /// findings had an owner); an unreadable registry is not that case and keeps the incumbent.
    /// </param>
    /// <param name="activeProfileKey">The active company, or null when none was activated.</param>
    public static string OwnerOf(Finding finding, bool incumbentRegistered, string? activeProfileKey)
    {
        ArgumentNullException.ThrowIfNull(finding);
        if (incumbentRegistered || string.IsNullOrWhiteSpace(activeProfileKey)) return KpiProfiles.Default;
        return activeProfileKey.Trim();
    }
}
