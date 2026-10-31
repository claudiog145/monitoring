using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using JourneyMonitor.Core.Monitoring;

namespace JourneyMonitor.Functions.Functions
{
    /// <summary>
    /// API endpoints for managing AppInsights tokens
    /// Users can paste their Azure tokens here to enable AppInsights access
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    [AllowAnonymous]  // Can add auth later if needed
    public class AppInsightsTokenController : ControllerBase
    {
        private readonly IAppInsightsTokenService _tokenService;
        private readonly ITenantResolver _tenantResolver;
        private readonly ILogger<AppInsightsTokenController> _logger;

        public AppInsightsTokenController(
            IAppInsightsTokenService tokenService,
            ITenantResolver tenantResolver,
            ILogger<AppInsightsTokenController> logger)
        {
            _tokenService = tokenService ?? throw new ArgumentNullException(nameof(tokenService));
            _tenantResolver = tenantResolver ?? throw new ArgumentNullException(nameof(tenantResolver));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Add a new AppInsights token for a tenant
        /// 
        /// HOW TO USE:
        /// 1. Open Terminal/PowerShell
        /// 2. Run: az login --tenant YOUR-TENANT-ID
        /// 3. Run: az account get-access-token --scope https://api.applicationinsights.io/.default
        /// 4. Copy the "accessToken" value (the long JWT string)
        /// 5. POST to this endpoint with the token
        /// 
        /// Example request:
        /// POST /api/appinsights/tokens
        /// {
        ///   "tenantKey": "arrivia",
        ///   "email": "cgareca@arrivia.com",
        ///   "token": "eyJ0eXAi..."
        /// }
        /// </summary>
        [HttpPost("tokens")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        public async Task<IActionResult> AddToken([FromBody] AddTokenRequest request)
        {
            try
            {
                // Validate request
                if (request == null)
                    return BadRequest("Request body is required");

                if (string.IsNullOrWhiteSpace(request.TenantKey))
                    return BadRequest("Tenant key is required (e.g., 'arrivia', 'ice')");

                if (string.IsNullOrWhiteSpace(request.Email))
                    return BadRequest("Email is required");

                if (string.IsNullOrWhiteSpace(request.Token))
                    return BadRequest("Token is required. Get it with: az account get-access-token --scope https://api.applicationinsights.io/.default");

                _logger.LogInformation(
                    $"AddToken request for tenant {request.TenantKey} by {request.Email}");

                // Save token (validates and encrypts it)
                await _tokenService.SaveTokenAsync(
                    request.TenantKey,
                    request.Email,
                    request.Token,
                    request.Notes);

                _logger.LogInformation(
                    $"Token saved successfully for tenant {request.TenantKey}");

                return Ok(new
                {
                    success = true,
                    message = $"Token saved successfully for tenant '{request.TenantKey}'",
                    tenant = request.TenantKey,
                    email = request.Email,
                    expiresIn = "~1 hour",
                    note = "Generate new token with: az account get-access-token --scope https://api.applicationinsights.io/.default"
                });
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning($"Token validation failed: {ex.Message}");
                return BadRequest(new
                {
                    success = false,
                    error = ex.Message,
                    hint = "Token might be invalid or expired. Generate a new one with: az account get-access-token --scope https://api.applicationinsights.io/.default"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error adding token: {ex}");
                return StatusCode(500, new
                {
                    success = false,
                    error = "An error occurred while saving the token",
                    details = ex.Message
                });
            }
        }

        /// <summary>
        /// Validate a token without saving it
        /// Useful for testing token validity before pasting
        /// </summary>
        [HttpPost("tokens/validate")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> ValidateToken([FromBody] ValidateTokenRequest request)
        {
            try
            {
                if (request == null || string.IsNullOrWhiteSpace(request.Token))
                    return BadRequest("Token is required");

                await _tokenService.ValidateTokenAsync(request.Token);

                return Ok(new
                {
                    valid = true,
                    message = "Token is valid"
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Token validation failed: {ex.Message}");
                return BadRequest(new
                {
                    valid = false,
                    error = ex.Message
                });
            }
        }

        /// <summary>
        /// Get all stored tokens (for management UI)
        /// Shows token status without exposing the actual tokens
        /// </summary>
        [HttpGet("tokens")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<IActionResult> GetAllTokens()
        {
            try
            {
                var tokens = await _tokenService.GetAllTokensAsync();

                // Don't expose encrypted tokens, just metadata
                var tokenMetadata = new List<object>();
                foreach (var token in tokens)
                {
                    tokenMetadata.Add(new
                    {
                        id = token.Id,
                        tenantKey = token.TenantKey,
                        email = token.Email,
                        createdAt = token.CreatedAt,
                        expiresAt = token.ExpiresAt,
                        isActive = token.IsActive,
                        isExpired = token.IsExpired,
                        isValid = token.IsValid,
                        usageCount = token.UsageCount,
                        lastUsedAt = token.LastUsedAt,
                        notes = token.Notes
                    });
                }

                return Ok(new
                {
                    success = true,
                    count = tokenMetadata.Count,
                    tokens = tokenMetadata
                });
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error getting tokens: {ex}");
                return StatusCode(500, new { error = "Failed to retrieve tokens" });
            }
        }

        /// <summary>
        /// Get available tenants (for UI dropdown)
        /// </summary>
        [HttpGet("tenants")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<IActionResult> GetTenants()
        {
            try
            {
                var tenants = await _tenantResolver.GetAllTenantsAsync();
                
                return Ok(new
                {
                    success = true,
                    count = tenants.Count,
                    tenants = tenants.Select(t => new
                    {
                        key = t.TenantKey,
                        displayName = t.TenantKey.ToUpper(),
                        subscriptionId = t.SubscriptionId,
                        resourceGroup = t.ResourceGroup,
                        appInsightsName = t.AppInsightsName
                    })
                });
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error getting tenants: {ex}");
                return StatusCode(500, new { error = "Failed to retrieve tenants" });
            }
        }

        /// <summary>
        /// Check if a tenant has a valid token configured
        /// </summary>
        [HttpGet("tokens/{tenantKey}/status")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<IActionResult> GetTokenStatus(string tenantKey)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(tenantKey))
                    return BadRequest("Tenant key is required");

                var hasValid = await _tokenService.HasValidTokenAsync(tenantKey);

                return Ok(new
                {
                    tenantKey,
                    hasValidToken = hasValid,
                    message = hasValid 
                        ? $"Token configured for {tenantKey}" 
                        : $"No valid token for {tenantKey}. Add one at /settings/appinsights"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error checking token status: {ex}");
                return StatusCode(500, new { error = "Failed to check token status" });
            }
        }

        /// <summary>
        /// Deactivate a token (don't delete, just disable it)
        /// </summary>
        [HttpPost("tokens/{tenantKey}/deactivate")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> DeactivateToken(string tenantKey)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(tenantKey))
                    return BadRequest("Tenant key is required");

                await _tokenService.DeactivateTokenAsync(tenantKey);

                return Ok(new
                {
                    success = true,
                    message = $"Token deactivated for {tenantKey}"
                });
            }
            catch (InvalidOperationException ex)
            {
                return NotFound(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error deactivating token: {ex}");
                return StatusCode(500, new { error = "Failed to deactivate token" });
            }
        }

        /// <summary>
        /// Health check endpoint
        /// </summary>
        [HttpGet("health")]
        public IActionResult Health()
        {
            return Ok(new
            {
                status = "healthy",
                service = "AppInsights Token Manager",
                timestamp = DateTime.UtcNow
            });
        }
    }

    /// <summary>
    /// Request model for adding a token
    /// </summary>
    public class AddTokenRequest
    {
        /// <summary>
        /// Tenant key (arrivia, ice, acme, etc.)
        /// </summary>
        public string TenantKey { get; set; }

        /// <summary>
        /// Email of user adding token (for audit trail)
        /// Example: cgareca@arrivia.com
        /// </summary>
        public string Email { get; set; }

        /// <summary>
        /// Azure access token from: az account get-access-token --scope https://management.azure.com/
        /// </summary>
        public string Token { get; set; }

        /// <summary>
        /// Optional notes
        /// </summary>
        public string Notes { get; set; }
    }

    /// <summary>
    /// Request model for validating a token
    /// </summary>
    public class ValidateTokenRequest
    {
        /// <summary>
        /// Azure access token to validate
        /// </summary>
        public string Token { get; set; }
    }
}
