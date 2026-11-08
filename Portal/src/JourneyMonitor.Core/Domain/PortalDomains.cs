namespace JourneyMonitor.Core.Domain;

/// <summary>
/// Domains the portal Domain filter treats as "All": Hotels, Cruises, and Cars.
/// Air stays addressable when a caller names it, but it is not part of All.
/// </summary>
public static class PortalDomains
{
    public static readonly string[] All = ["hotels", "cruises", "cars"];
}
