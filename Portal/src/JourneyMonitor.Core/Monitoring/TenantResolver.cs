using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace JourneyMonitor.Core.Monitoring
{
    /// <summary>
    /// Configuration for a single tenant's AppInsights resources
    /// </summary>
    public class AppInsightsTenant
    {
        /// <summary>
        /// Unique identifier for tenant (e.g., "arrivia", "ice")
        /// </summary>
        public string TenantKey { get; set; }

        /// <summary>
        /// Azure Subscription ID where AppInsights is located
        /// </summary>
        public string SubscriptionId { get; set; }

        /// <summary>
        /// Azure Resource Group containing AppInsights
        /// </summary>
        public string ResourceGroup { get; set; }

        /// <summary>
        /// Name of Application Insights resource
        /// </summary>
        public string AppInsightsName { get; set; }

        /// <summary>
        /// Azure AD tenant ID (for reference)
        /// </summary>
        public string AadTenantId { get; set; }

        /// <summary>
        /// Full resource ID for Azure API calls
        /// Format: /subscriptions/{subId}/resourceGroups/{rg}/providers/microsoft.insights/components/{name}
        /// </summary>
        public string GetResourceId()
        {
            return $"/subscriptions/{SubscriptionId}" +
                   $"/resourceGroups/{ResourceGroup}" +
                   $"/providers/microsoft.insights/components/{AppInsightsName}";
        }
    }

    /// <summary>
    /// Maps tenant keys to AppInsights configuration
    /// </summary>
    public interface ITenantResolver
    {
        /// <summary>
        /// Get AppInsights configuration for a tenant
        /// </summary>
        Task<AppInsightsTenant> GetTenantConfigAsync(string tenantKey);

        /// <summary>
        /// Get all available tenants (for UI dropdown)
        /// </summary>
        Task<List<AppInsightsTenant>> GetAllTenantsAsync();
    }

    /// <summary>
    /// In-memory implementation of ITenantResolver
    /// In production, this could load from database or configuration
    /// </summary>
    public class TenantResolver : ITenantResolver
    {
        // TODO: Load from appsettings.json or database
        private static readonly Dictionary<string, AppInsightsTenant> TenantConfig =
            new Dictionary<string, AppInsightsTenant>
            {
                {
                    "arrivia", new AppInsightsTenant
                    {
                        TenantKey = "arrivia",
                        SubscriptionId = "YOUR-ARRIVIA-SUBSCRIPTION-ID",
                        ResourceGroup = "rg-arrivia-prod",
                        AppInsightsName = "ai-arrivia-prod",
                        AadTenantId = "YOUR-ARRIVIA-AAD-TENANT-ID"
                    }
                },
                {
                    "ice", new AppInsightsTenant
                    {
                        TenantKey = "ice",
                        SubscriptionId = "YOUR-ICE-SUBSCRIPTION-ID",
                        ResourceGroup = "rg-ice-prod",
                        AppInsightsName = "ai-ice-prod",
                        AadTenantId = "YOUR-ICE-AAD-TENANT-ID"
                    }
                }
                // Add more tenants as needed
                // {
                //     "acme", new AppInsightsTenant
                //     {
                //         TenantKey = "acme",
                //         SubscriptionId = "YOUR-ACME-SUBSCRIPTION-ID",
                //         ResourceGroup = "rg-acme-prod",
                //         AppInsightsName = "ai-acme-prod",
                //         AadTenantId = "YOUR-ACME-AAD-TENANT-ID"
                //     }
                // }
            };

        /// <summary>
        /// Get AppInsights configuration for a tenant
        /// </summary>
        public Task<AppInsightsTenant> GetTenantConfigAsync(string tenantKey)
        {
            if (string.IsNullOrWhiteSpace(tenantKey))
                throw new ArgumentException("Tenant key is required", nameof(tenantKey));

            if (!TenantConfig.TryGetValue(tenantKey.ToLower(), out var config))
                throw new KeyNotFoundException(
                    $"Tenant configuration not found for key: {tenantKey}. " +
                    $"Available tenants: {string.Join(", ", TenantConfig.Keys)}");

            return Task.FromResult(config);
        }

        /// <summary>
        /// Get all available tenants
        /// </summary>
        public Task<List<AppInsightsTenant>> GetAllTenantsAsync()
        {
            return Task.FromResult(TenantConfig.Values.ToList());
        }
    }
}
