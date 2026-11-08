namespace JourneyMonitor.Core.Company;

/// <summary>Profile keys used by KPI definitions (monitoring.CompanyProfile.ProfileKey).</summary>
public static class KpiProfiles
{
    /// <summary>The incumbent profile: KPI rows created before profiles existed belong to it.</summary>
    public const string Default = "arrivia";

    /// <summary>Query value that selects every profile.</summary>
    public const string All = "*";
}
