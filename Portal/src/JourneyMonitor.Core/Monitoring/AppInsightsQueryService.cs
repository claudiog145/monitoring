using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Azure.Core;
using Azure.Monitor.Query;
using Azure.Monitor.Query.Models;
using Microsoft.Extensions.Logging;

namespace JourneyMonitor.Core.Monitoring
{
    /// <summary>
    /// Service to query AppInsights using stored tokens
    /// This is the UPDATED version that uses stored tokens from IAppInsightsTokenService
    /// </summary>
    public interface IAppInsightsQueryService
    {
        /// <summary>
        /// Query logs from AppInsights for a specific tenant
        /// Uses the token stored in database for that tenant
        /// </summary>
        Task<IReadOnlyList<LogsTable>> QueryLogsAsync(
            string tenantKey,
            string query,
            QueryTimeRange timeRange);
    }

    /// <summary>
    /// Implementation using stored tokens
    /// </summary>
    public class AppInsightsQueryService : IAppInsightsQueryService
    {
        private readonly IAppInsightsTokenService _tokenService;
        private readonly ITenantResolver _tenantResolver;
        private readonly ILogger<AppInsightsQueryService> _logger;

        public AppInsightsQueryService(
            IAppInsightsTokenService tokenService,
            ITenantResolver tenantResolver,
            ILogger<AppInsightsQueryService> logger)
        {
            _tokenService = tokenService ?? throw new ArgumentNullException(nameof(tokenService));
            _tenantResolver = tenantResolver ?? throw new ArgumentNullException(nameof(tenantResolver));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<IReadOnlyList<LogsTable>> QueryLogsAsync(
            string tenantKey,
            string query,
            QueryTimeRange timeRange)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(tenantKey))
                    throw new ArgumentException("Tenant key is required", nameof(tenantKey));
                if (string.IsNullOrWhiteSpace(query))
                    throw new ArgumentException("Query is required", nameof(query));

                _logger.LogInformation($"Querying logs for tenant {tenantKey}");

                // Get tenant configuration (maps to subscription/resource group)
                var config = await _tenantResolver.GetTenantConfigAsync(tenantKey);

                // Get stored token for this tenant
                var token = await _tokenService.GetTokenAsync(tenantKey);

                _logger.LogDebug($"Using token for tenant {tenantKey}, querying {config.AppInsightsName}");

                // Create credential from stored token
                var credential = new BearerTokenCredential(token);
                var client = new LogsQueryClient(credential);

                // Build resource ID
                var resourceId = config.GetResourceId();

                _logger.LogDebug($"Resource ID: {resourceId}");
                _logger.LogDebug($"Query: {query}");

                // Execute query
                var result = await client.QueryResourceAsync(
                    new ResourceIdentifier(resourceId),
                    query: query,
                    timeRange: timeRange);

                // Record usage
                await _tokenService.RecordTokenUsageAsync(tenantKey);

                _logger.LogInformation(
                    $"Query succeeded for tenant {tenantKey}. " +
                    $"Results: {result.Value.AllTables.Count} tables");

                return result.Value.AllTables;
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning($"Token error for tenant {tenantKey}: {ex.Message}");
                throw;
            }
            catch (Azure.RequestFailedException ex) when (ex.Status == 403)
            {
                _logger.LogError($"Access denied to AppInsights for tenant {tenantKey}");
                throw new Exception(
                    $"Access denied to AppInsights for tenant {tenantKey}. " +
                    $"Verify token is still valid.", ex);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Query failed for tenant {tenantKey}: {ex}");
                throw new Exception(
                    $"Failed to query AppInsights for tenant {tenantKey}", ex);
            }
        }
    }
}
