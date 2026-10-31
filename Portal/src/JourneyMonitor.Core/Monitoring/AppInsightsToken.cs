using System;

namespace JourneyMonitor.Core.Monitoring
{
    /// <summary>
    /// Stores encrypted Azure access tokens for AppInsights by tenant
    /// Users generate tokens externally and paste them here
    /// </summary>
    public class AppInsightsToken
    {
        public int Id { get; set; }

        /// <summary>
        /// Tenant key: "arrivia", "ice", "acme", etc.
        /// </summary>
        public string TenantKey { get; set; }

        /// <summary>
        /// Email of the user who added this token (audit trail)
        /// Example: cgareca@arrivia.com
        /// </summary>
        public string Email { get; set; }

        /// <summary>
        /// Azure access token ENCRYPTED using Data Protection API
        /// Generated with: az account get-access-token --scope https://api.applicationinsights.io/.default
        /// </summary>
        public string EncryptedToken { get; set; }

        /// <summary>
        /// When this token expires (Azure tokens valid for ~3600 seconds)
        /// </summary>
        public DateTime ExpiresAt { get; set; }

        /// <summary>
        /// When token was added
        /// </summary>
        public DateTime CreatedAt { get; set; }

        /// <summary>
        /// When token was last used (for audit and monitoring)
        /// </summary>
        public DateTime? LastUsedAt { get; set; }

        /// <summary>
        /// How many times this token was used (helps identify active tokens)
        /// </summary>
        public int UsageCount { get; set; }

        /// <summary>
        /// Is this token currently active (can be deactivated without deleting)
        /// </summary>
        public bool IsActive { get; set; } = true;

        /// <summary>
        /// Optional notes for audit (e.g., "Generated on 2024-10-30")
        /// </summary>
        public string Notes { get; set; }

        /// <summary>
        /// Check if token has expired
        /// </summary>
        public bool IsExpired => DateTime.UtcNow > ExpiresAt;

        /// <summary>
        /// Check if token is still valid (not expired and active)
        /// </summary>
        public bool IsValid => IsActive && !IsExpired;
    }
}
