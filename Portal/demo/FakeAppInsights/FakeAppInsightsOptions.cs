namespace FakeAppInsights;

/// <summary>Configuration section <c>FakeAppInsights</c> (contract v5 §2).</summary>
public sealed class FakeAppInsightsOptions
{
    public const string SectionName = "FakeAppInsights";

    /// <summary>Kustainer base URL, e.g. <c>http://kustainer:8080</c>.</summary>
    public string? KustoUrl { get; set; }

    /// <summary>Value the <c>X-Api-Key</c> header must carry. Empty rejects every request.</summary>
    public string? ApiKey { get; set; }

    /// <summary>How long the list of databases and the appId registry are cached.</summary>
    public TimeSpan CatalogCacheDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Minimum time between two refreshes forced by an unknown appId.</summary>
    public TimeSpan CatalogForcedRefreshInterval { get; set; } = TimeSpan.FromSeconds(2);
}
