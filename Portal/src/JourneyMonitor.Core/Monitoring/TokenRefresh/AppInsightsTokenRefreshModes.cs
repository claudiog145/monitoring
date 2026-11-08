namespace JourneyMonitor.Core.Monitoring.TokenRefresh;

/// <summary>How a stored App Insights token is renewed (contracts-company §2.2 RefreshMode).</summary>
public static class AppInsightsTokenRefreshModes
{
    public const string Manual = "manual";
    public const string AzureCli = "azure-cli";
    public const string ServicePrincipal = "service-principal";

    public static readonly IReadOnlyList<string> All = [Manual, AzureCli, ServicePrincipal];

    /// <summary>Canonical value for a known mode (case-insensitive), null for blank or unknown text.</summary>
    public static string? Normalize(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode)) return null;
        var trimmed = mode.Trim();
        return All.FirstOrDefault(m => string.Equals(m, trimmed, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsAutomatic(string? mode)
    {
        var normalized = Normalize(mode);
        return normalized is not null && normalized != Manual;
    }

    /// <summary>
    /// Save-time validation (contracts-company §2.4): service-principal needs a tenant, a client id and a
    /// stored or new secret; azure-cli needs a tenant. Returns the errors, empty when valid.
    /// </summary>
    public static IReadOnlyList<string> Validate(string? mode, string? aadTenantId, string? clientId, bool hasSecret)
    {
        var errors = new List<string>();
        var normalized = Normalize(mode);
        if (normalized is null)
        {
            errors.Add($"refreshMode must be one of: {string.Join(", ", All)}.");
            return errors;
        }

        if (normalized == AzureCli && string.IsNullOrWhiteSpace(aadTenantId))
        {
            errors.Add("aadTenantId is required for refreshMode 'azure-cli'.");
        }

        if (normalized == ServicePrincipal)
        {
            if (string.IsNullOrWhiteSpace(aadTenantId)) errors.Add("aadTenantId is required for refreshMode 'service-principal'.");
            if (string.IsNullOrWhiteSpace(clientId)) errors.Add("clientId is required for refreshMode 'service-principal'.");
            if (!hasSecret) errors.Add("clientSecret is required for refreshMode 'service-principal'.");
        }

        return errors;
    }
}
