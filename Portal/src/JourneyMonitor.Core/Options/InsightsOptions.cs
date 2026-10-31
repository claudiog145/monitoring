namespace JourneyMonitor.Core.Options;

/// <summary>
/// Strongly-typed configuration mirroring local.config.json / local.config.example.json.
/// No secrets: only Application Insights **application ids** (not instrumentation keys)
/// and Azure AD **tenant ids**. Bound via IConfiguration/Options pattern (section "Insights").
/// </summary>
public sealed class InsightsOptions
{
    public const string SectionName = "Insights";

    /// <summary>Legacy top-level Hotel App Insights App ID (back-compat with local.config.json).</summary>
    public string? AppInsightsAppId { get; set; }

    public HotelInsightsOptions Hotels { get; set; } = new();
    public AirInsightsOptions Air { get; set; } = new();
    public CruiseInsightsOptions Cruises { get; set; } = new();
}

public class DomainInsightsSlot
{
    public string? TenantId { get; set; }
    public string? AppInsightsAppId { get; set; }
}

public sealed class HotelInsightsOptions : DomainInsightsSlot
{
    public DomainInsightsSlot Staging { get; set; } = new();
}

public sealed class AirInsightsOptions : DomainInsightsSlot
{
    public string? UiResource { get; set; }
    public string? ApiResource { get; set; }
    public AirStagingSlot Staging { get; set; } = new();
}

public sealed class AirStagingSlot : DomainInsightsSlot
{
    public string? UiResource { get; set; }
    public string? ApiResource { get; set; }
}

public sealed class CruiseInsightsOptions : DomainInsightsSlot
{
    public string? ClubResource { get; set; }
    public string? CoreResource { get; set; }
    public string? DefaultHost { get; set; }
    public CruiseStagingSlot Staging { get; set; } = new();
}

public sealed class CruiseStagingSlot : DomainInsightsSlot
{
    public string? ClubResource { get; set; }
    public string? CoreResource { get; set; }
    public string? DefaultHost { get; set; }
}

/// <summary>Resolved App Insights source for one (domain, environment) pair. Output of InsightsSourceResolver.</summary>
public sealed class InsightsSource
{
    public required string Domain { get; set; }
    public required string Environment { get; set; }
    public string TenantId { get; set; } = "";
    public required string AppInsightsAppId { get; set; }
    public string? UiResource { get; set; }
    public string? ApiResource { get; set; }
    public string? ClubResource { get; set; }
    public string? CoreResource { get; set; }
    public string? DefaultHost { get; set; }
}
