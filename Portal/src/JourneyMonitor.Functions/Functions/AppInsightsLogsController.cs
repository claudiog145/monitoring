using System;
using System.Threading.Tasks;
using Azure.Monitor.Query.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using JourneyMonitor.Core.Monitoring;

namespace JourneyMonitor.Functions.Functions
{
    /// <summary>
    /// API endpoints for querying AppInsights logs
    /// Uses tokens stored via AppInsightsTokenController
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    [AllowAnonymous]
    public class AppInsightsLogsController : ControllerBase
    {
        private readonly IAppInsightsQueryService _queryService;
        private readonly IAppInsightsTokenService _tokenService;
        private readonly ILogger<AppInsightsLogsController> _logger;

        public AppInsightsLogsController(
            IAppInsightsQueryService queryService,
            IAppInsightsTokenService tokenService,
            ILogger<AppInsightsLogsController> logger)
        {
            _queryService = queryService ?? throw new ArgumentNullException(nameof(queryService));
            _tokenService = tokenService ?? throw new ArgumentNullException(nameof(tokenService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Query logs from AppInsights for a specific tenant
        /// Returns logs for the tenant whose token is stored
        /// 
        /// Example request:
        /// POST /api/appinsights/logs
        /// {
        ///   "tenantKey": "arrivia",
        ///   "query": "customEvents | where name == 'scout-run-started' | take 100",
        ///   "timeRange": {
        ///     "startTime": "2024-10-23T00:00:00Z",
        ///     "endTime": "2024-10-30T00:00:00Z"
        ///   }
        /// }
        /// </summary>
        [HttpPost("logs")]
        [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> QueryLogs([FromBody] QueryLogsRequest request)
        {
            try
            {
                // Validate request
                if (request == null)
                    return BadRequest(new { error = "Request body is required" });

                if (string.IsNullOrWhiteSpace(request.TenantKey))
                    return BadRequest(new { error = "Tenant key is required" });

                if (string.IsNullOrWhiteSpace(request.Query))
                    return BadRequest(new { error = "KQL query is required" });

                _logger.LogInformation($"Query logs request for tenant {request.TenantKey}");

                // Check if token exists for this tenant
                var hasToken = await _tokenService.HasValidTokenAsync(request.TenantKey);
                if (!hasToken)
                    return NotFound(new
                    {
                        error = $"No valid token configured for tenant '{request.TenantKey}'",
                        hint = "Add a token at /api/appinsights/tokens"
                    });

                // Query AppInsights
                var result = await _queryService.QueryLogsAsync(
                    request.TenantKey,
                    request.Query,
                    request.TimeRange);

                _logger.LogInformation($"Query succeeded for tenant {request.TenantKey}");

                return Ok(new
                {
                    success = true,
                    tenantKey = request.TenantKey,
                    resultCount = result.Count,
                    results = result
                });
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning($"Token error: {ex.Message}");
                return BadRequest(new
                {
                    success = false,
                    error = ex.Message,
                    hint = "Token might be expired. Generate new one with: az account get-access-token --scope https://api.applicationinsights.io/.default"
                });
            }
            catch (Azure.RequestFailedException ex) when (ex.Status == 404)
            {
                _logger.LogWarning($"AppInsights resource not found: {ex.Message}");
                return NotFound(new
                {
                    success = false,
                    error = "AppInsights resource not found",
                    details = ex.Message
                });
            }
            catch (Exception ex)
            {
                _logger.LogError($"Query failed: {ex}");
                return StatusCode(500, new
                {
                    success = false,
                    error = "Failed to query AppInsights",
                    details = ex.Message
                });
            }
        }

        /// <summary>
        /// Get metrics from AppInsights for a specific tenant
        /// 
        /// Common metrics:
        /// - requests/count
        /// - requests/failed
        /// - requests/duration
        /// - exceptions/count
        /// - customEvents/count
        /// 
        /// <summary>
        /// Get sample KQL queries for testing
        /// </summary>
        [HttpGet("samples")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public IActionResult GetSampleQueries()
        {
            return Ok(new
            {
                samples = new[]
                {
                    new
                    {
                        name = "Latest 10 custom events",
                        query = "customEvents | order by timestamp desc | take 10"
                    },
                    new
                    {
                        name = "Failed requests in last 24h",
                        query = "requests | where timestamp > ago(24h) and success == false | summarize count() by tostring(resultCode)"
                    },
                    new
                    {
                        name = "Average response time by operation",
                        query = "requests | where timestamp > ago(7d) | summarize avg(duration) by operation_Name | order by avg_duration desc"
                    },
                    new
                    {
                        name = "Exceptions in last 24h",
                        query = "exceptions | where timestamp > ago(24h) | order by timestamp desc | take 20"
                    },
                    new
                    {
                        name = "Custom events by name",
                        query = "customEvents | where timestamp > ago(7d) | summarize count() by name"
                    },
                    new
                    {
                        name = "Performance chart (requests/min)",
                        query = "requests | where timestamp > ago(24h) | summarize count() by bin(timestamp, 1m) | render timechart"
                    }
                }
            });
        }

        /// <summary>
        /// Health check
        /// </summary>
        [HttpGet("health")]
        public IActionResult Health()
        {
            return Ok(new
            {
                status = "healthy",
                service = "AppInsights Logs Query",
                timestamp = DateTime.UtcNow
            });
        }
    }

    /// <summary>
    /// Request model for querying logs
    /// </summary>
    public class QueryLogsRequest
    {
        /// <summary>
        /// Tenant key (arrivia, ice, acme, etc.)
        /// </summary>
        public string TenantKey { get; set; }

        /// <summary>
        /// KQL (Kusto Query Language) query
        /// Example: customEvents | where name == "scout-run-started" | take 100
        /// </summary>
        public string Query { get; set; }

        /// <summary>
        /// Time range for query
        /// If null, defaults to last 24 hours
        /// </summary>
        public Azure.Monitor.Query.QueryTimeRange TimeRange { get; set; }
    }
}
