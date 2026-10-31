namespace JourneyMonitor.Core.Domain;

/// <summary>
/// Domains the portal Domain filter treats as "All": Hotels and Cruises.
/// Air and Cars stay addressable when a caller names them, but they are not part of All.
/// </summary>
public static class PortalDomains
{
    public static readonly string[] All = ["hotels", "cruises"];
}
