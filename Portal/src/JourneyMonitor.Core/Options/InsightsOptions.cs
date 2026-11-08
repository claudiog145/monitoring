namespace JourneyMonitor.Core.Options;

/// <summary>
/// Strongly-typed configuration mirroring local.config.json / local.config.example.json.
/// Only Application Insights **application ids** (not instrumentation keys) and Azure AD
/// **tenant ids**, plus the optional <see cref="QueryApiKeys"/> escape hatch below. Bound via
/// IConfiguration/Options pattern (section "Insights").
/// </summary>
public sealed class InsightsOptions
{
    public const string SectionName = "Insights";

    /// <summary>Legacy top-level Hotel App Insights App ID (back-compat with local.config.json).</summary>
    public string? AppInsightsAppId { get; set; }

    /// <summary>
    /// Optional, opt-in: <c>appId=key</c> pairs separated by <c>;</c>, used instead of an Entra ID
    /// token for those app ids. For environments where the deployment identity cannot be granted
    /// Monitoring Reader (no Owner/User Access Administrator available to create the role
    /// assignment), since an Application Insights API key can be minted by anyone holding
    /// Application Insights Component Contributor on the component itself.
    ///
    /// A single string rather than a dictionary because app ids are GUIDs and az containerapp
    /// rejects env var names containing hyphens, so <c>Insights__QueryApiKeys__{guid}</c> cannot
    /// be set in the first place.
    ///
    /// Hard limit, verified against the live API on 2026-10-20: a key only authorizes **its own**
    /// component. A query whose KQL reaches another resource through <c>app('...')</c> still fails
    /// with 400 InsufficientAccessToResource, because the key carries no identity for that second
    /// resource. Every Scout and Inspector board is a cross-resource union, so this does NOT
    /// substitute for Monitoring Reader there - it only covers single-resource queries.
    ///
    /// Values are secrets: supply them through a Container App secret / Key Vault reference, never
    /// through appsettings.json or any committed file.
    ///
    /// Wildcard: an entry whose app id is <c>*</c> (<c>*=key</c>, or <c>*:key</c> as the demo
    /// stack writes it) is used for every app id that has no key of its own; a specific
    /// <c>appId=key</c> always wins over it. Example: <c>*=demo-key;{guid}=real-key</c>.
    /// </summary>
    public string? QueryApiKeys { get; set; }

    /// <summary>The wildcard app id in <see cref="QueryApiKeys"/>.</summary>
    public const string WildcardAppId = "*";

    public const string DefaultQueryEndpoint = "https://api.applicationinsights.io";

    /// <summary>
    /// Base URL of the App Insights query REST API (<c>{QueryEndpoint}/v1/apps/{appId}/query</c>).
    /// Empty means <see cref="DefaultQueryEndpoint"/>; the demo stack points it at its fake server.
    /// </summary>
    public string? QueryEndpoint { get; set; }

    public string ResolvedQueryEndpoint =>
        string.IsNullOrWhiteSpace(QueryEndpoint) ? DefaultQueryEndpoint : QueryEndpoint.Trim();

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
