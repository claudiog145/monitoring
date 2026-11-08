using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Domain;

namespace JourneyMonitor.Core.Monitoring.Evidence;

/// <summary>Which company a triage signal's evidence is read for.</summary>
public static class EvidenceProfile
{
    /// <summary>The signal's profile; a signal without one is the incumbent's, the same rule Triage applies.</summary>
    public static string KeyOf(TriageSignal signal) =>
        string.IsNullOrWhiteSpace(signal.ProfileKey) ? KpiProfiles.Default : signal.ProfileKey.Trim();

    public static bool IsIncumbent(string profileKey) =>
        string.Equals(profileKey, KpiProfiles.Default, StringComparison.OrdinalIgnoreCase);

    public static bool IsIncumbent(TriageSignal signal) => IsIncumbent(KeyOf(signal));
}
