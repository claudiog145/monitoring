using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using Azure.Monitor.Query;

namespace JourneyMonitor.Core.Monitoring
{
    /// <summary>
    /// Interface for managing encrypted AppInsights tokens
    /// </summary>
    public interface IAppInsightsTokenService
    {
        /// <summary>
        /// Save a new token for a tenant
        /// Token is encrypted before storage
        /// </summary>
        Task SaveTokenAsync(string tenantKey, string email, string token, string notes = null);

        /// <summary>
        /// Get decrypted token for a tenant
        /// Throws if token doesn't exist or is expired
        /// </summary>
        Task<string> GetTokenAsync(string tenantKey);

        /// <summary>
        /// Validate that a token is valid (calls Azure to test)
        /// </summary>
        Task ValidateTokenAsync(string token);

        /// <summary>
        /// Get all stored tokens (for management UI)
        /// </summary>
        Task<List<AppInsightsToken>> GetAllTokensAsync();

        /// <summary>
        /// Deactivate a token without deleting it
        /// </summary>
        Task DeactivateTokenAsync(string tenantKey);

        /// <summary>
        /// Check if a token exists and is valid for a tenant
        /// </summary>
        Task<bool> HasValidTokenAsync(string tenantKey);

        /// <summary>
        /// Record token usage (update LastUsedAt and UsageCount)
        /// </summary>
        Task RecordTokenUsageAsync(string tenantKey);
    }

    /// <summary>
    /// Implementation of IAppInsightsTokenService
    /// Handles encryption, validation, and storage of Azure tokens
    /// </summary>
    public class AppInsightsTokenService : IAppInsightsTokenService
    {
        private readonly IAppInsightsTokenRepository _repository;
        private readonly IDataProtectionProvider _dataProtectionProvider;
        private readonly ILogger<AppInsightsTokenService> _logger;
        private IDataProtector _protector;

        public AppInsightsTokenService(
            IAppInsightsTokenRepository repository,
            IDataProtectionProvider dataProtectionProvider,
            ILogger<AppInsightsTokenService> logger)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _dataProtectionProvider = dataProtectionProvider ?? throw new ArgumentNullException(nameof(dataProtectionProvider));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            
            // Create protector for tokens
            _protector = _dataProtectionProvider.CreateProtector("AppInsights.Tokens");
        }

        public async Task SaveTokenAsync(string tenantKey, string email, string token, string notes = null)
        {
            if (string.IsNullOrWhiteSpace(tenantKey))
                throw new ArgumentException("Tenant key is required", nameof(tenantKey));
            if (string.IsNullOrWhiteSpace(email))
                throw new ArgumentException("Email is required", nameof(email));
            if (string.IsNullOrWhiteSpace(token))
                throw new ArgumentException("Token is required", nameof(token));

            try
            {
                // Clean token (remove quotes, whitespace)
                var cleanToken = token.Trim().Trim('"').Trim();
                
                _logger.LogInformation($"Validating token for tenant {tenantKey}");
                
                // Validate token before saving
                await ValidateTokenAsync(cleanToken);

                _logger.LogInformation($"Token validated. Encrypting for tenant {tenantKey}");
                
                // Encrypt token
                var encrypted = _protector.Protect(cleanToken);

                // Check if token already exists
                var existingToken = await _repository.GetByTenantKeyAsync(tenantKey);
                
                if (existingToken != null)
                {
                    // Update existing token
                    existingToken.Email = email;
                    existingToken.EncryptedToken = encrypted;
                    existingToken.ExpiresAt = DateTime.UtcNow.AddHours(1);
                    existingToken.IsActive = true;
                    existingToken.Notes = notes ?? $"Updated by {email} on {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}";
                    existingToken.LastUsedAt = null; // Reset usage tracking
                    
                    await _repository.UpdateAsync(existingToken);
                    _logger.LogInformation($"Token updated successfully for tenant {tenantKey}");
                }
                else
                {
                    // Create new token
                    var appInsightsToken = new AppInsightsToken
                    {
                        TenantKey = tenantKey,
                        Email = email,
                        EncryptedToken = encrypted,
                        ExpiresAt = DateTime.UtcNow.AddHours(1), // Azure tokens valid ~1 hour
                        CreatedAt = DateTime.UtcNow,
                        IsActive = true,
                        Notes = notes ?? $"Added by {email} on {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}",
                        UsageCount = 0
                    };

                    await _repository.SaveAsync(appInsightsToken);
                    _logger.LogInformation($"Token saved successfully for tenant {tenantKey}");
                }
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning($"Token validation failed for tenant {tenantKey}: {ex.Message}");
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error saving token for tenant {tenantKey}: {ex}");
                throw;
            }
        }

        public async Task<string> GetTokenAsync(string tenantKey)
        {
            if (string.IsNullOrWhiteSpace(tenantKey))
                throw new ArgumentException("Tenant key is required", nameof(tenantKey));

            var token = await _repository.GetByTenantKeyAsync(tenantKey);

            if (token == null)
                throw new InvalidOperationException(
                    $"No token configured for tenant '{tenantKey}'. " +
                    $"Go to Settings → AppInsights Tokens to add one.");

            if (!token.IsActive)
                throw new InvalidOperationException(
                    $"Token for tenant '{tenantKey}' is deactivated.");

            if (token.IsExpired)
                throw new InvalidOperationException(
                    $"Token for tenant '{tenantKey}' expired at {token.ExpiresAt:yyyy-MM-dd HH:mm:ss}. " +
                    $"Generate a new token with: az account get-access-token --scope https://api.applicationinsights.io/.default");

            try
            {
                // Decrypt and return
                return _protector.Unprotect(token.EncryptedToken);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error decrypting token for tenant {tenantKey}: {ex}");
                throw new InvalidOperationException(
                    $"Error accessing token for tenant '{tenantKey}'", ex);
            }
        }

        public async Task ValidateTokenAsync(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
                throw new ArgumentException("Token is required", nameof(token));

            try
            {
                // Clean token (remove quotes if present)
                var cleanToken = token.Trim().Trim('"').Trim();
                
                if (string.IsNullOrWhiteSpace(cleanToken))
                    throw new InvalidOperationException("Token is empty after cleanup");

                // Validate JWT format (3 parts separated by dots)
                var parts = cleanToken.Split('.');
                if (parts.Length != 3)
                    throw new InvalidOperationException($"Token format is invalid: expected 3 JWT parts, got {parts.Length}. Token must be a valid Bearer token from 'az account get-access-token'.");

                // Check that each part is not empty
                if (parts[0].Length == 0 || parts[1].Length == 0 || parts[2].Length == 0)
                    throw new InvalidOperationException("Token has empty parts. Token must be a complete JWT.");

                _logger.LogInformation("Token format validated successfully");
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Token validation failed: {ex.Message}");
                throw new InvalidOperationException(
                    "Token is invalid or expired. " +
                    "Generate a new token with: az account get-access-token --scope https://api.applicationinsights.io/.default", ex);
            }
        }

        public async Task<List<AppInsightsToken>> GetAllTokensAsync()
        {
            return await _repository.GetAllAsync();
        }

        public async Task DeactivateTokenAsync(string tenantKey)
        {
            if (string.IsNullOrWhiteSpace(tenantKey))
                throw new ArgumentException("Tenant key is required", nameof(tenantKey));

            var token = await _repository.GetByTenantKeyAsync(tenantKey);
            if (token == null)
                throw new InvalidOperationException($"Token not found for tenant {tenantKey}");

            token.IsActive = false;
            await _repository.UpdateAsync(token);
            
            _logger.LogInformation($"Token deactivated for tenant {tenantKey}");
        }

        public async Task<bool> HasValidTokenAsync(string tenantKey)
        {
            if (string.IsNullOrWhiteSpace(tenantKey))
                return false;

            var token = await _repository.GetByTenantKeyAsync(tenantKey);
            return token?.IsValid ?? false;
        }

        public async Task RecordTokenUsageAsync(string tenantKey)
        {
            var token = await _repository.GetByTenantKeyAsync(tenantKey);
            if (token != null)
            {
                token.LastUsedAt = DateTime.UtcNow;
                token.UsageCount++;
                await _repository.UpdateAsync(token);
            }
        }
    }

    /// <summary>
    /// Custom TokenCredential that uses a bearer token string
    /// </summary>
    public class BearerTokenCredential : Azure.Core.TokenCredential
    {
        private readonly string _token;

        public BearerTokenCredential(string token)
        {
            _token = token ?? throw new ArgumentNullException(nameof(token));
        }

        public override Azure.Core.AccessToken GetToken(
            Azure.Core.TokenRequestContext requestContext,
            System.Threading.CancellationToken cancellationToken)
        {
            return new Azure.Core.AccessToken(_token, DateTimeOffset.UtcNow.AddHours(1));
        }

        public override async System.Threading.Tasks.ValueTask<Azure.Core.AccessToken> GetTokenAsync(
            Azure.Core.TokenRequestContext requestContext,
            System.Threading.CancellationToken cancellationToken)
        {
            return await System.Threading.Tasks.Task.FromResult(
                GetToken(requestContext, cancellationToken)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Repository interface for AppInsightsToken persistence
    /// </summary>
    public interface IAppInsightsTokenRepository
    {
        Task SaveAsync(AppInsightsToken token);
        Task UpdateAsync(AppInsightsToken token);
        Task<AppInsightsToken> GetByTenantKeyAsync(string tenantKey);
        Task<List<AppInsightsToken>> GetAllAsync();
        Task DeleteAsync(int id);
    }
}
