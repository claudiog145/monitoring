namespace JourneyMonitor.Core.Monitoring.TokenRefresh;

/// <summary>
/// The OAuth scope a stored token is minted for, chosen by its tenant key. Almost every row is an
/// App Insights query token; the "azure-devops" row feeds the DocsAgent/CodeAgent search and must be
/// minted for Azure DevOps, because an App Insights token is rejected there (401) even though it is
/// a perfectly valid token for the same user and tenant.
/// </summary>
public static class StoredTokenScopes
{
    public const string AppInsights = "https://api.applicationinsights.io/.default";

    /// <summary>Azure DevOps resource id (499b84ac-…), the same for every organization.</summary>
    public const string AzureDevOps = "499b84ac-1321-427f-aa17-267ca6975798/.default";

    public const string AzureDevOpsTenantKey = "azure-devops";

    public static string ForTenantKey(string? tenantKey) =>
        string.Equals(tenantKey?.Trim(), AzureDevOpsTenantKey, StringComparison.OrdinalIgnoreCase)
            ? AzureDevOps
            : AppInsights;
}
