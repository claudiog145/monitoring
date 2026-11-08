using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using JourneyMonitor.Core.Monitoring.TokenRefresh;

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
        /// Save or update a token with its automatic refresh settings (contracts-company §2.4).
        /// A blank token is allowed for azure-cli / service-principal: the refresh daemon acquires one.
        /// A blank client secret keeps the stored one. Validation failures throw InvalidOperationException.
        /// </summary>
        Task SaveTokenAsync(AppInsightsTokenSaveRequest request);

        /// <summary>
        /// The decrypted active token for a tenant with its expiry, or null when there is none (never throws
        /// for a missing, inactive, empty or undecryptable token). The caller decides whether it is still fresh.
        /// </summary>
        Task<StoredAppInsightsToken?> GetStoredTokenAsync(string tenantKey);

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
        public const string ProtectorPurpose = "AppInsights.Tokens";

        private readonly IAppInsightsTokenRepository _repository;
        private readonly ILogger<AppInsightsTokenService> _logger;
        private readonly ITokenSecretProtector _protector;

        public AppInsightsTokenService(
            IAppInsightsTokenRepository repository,
            ITokenSecretProtector protector,
            ILogger<AppInsightsTokenService> logger)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _protector = protector ?? throw new ArgumentNullException(nameof(protector));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task SaveTokenAsync(AppInsightsTokenSaveRequest request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));
            if (string.IsNullOrWhiteSpace(request.TenantKey))
                throw new ArgumentException("Tenant key is required", nameof(request));
            if (string.IsNullOrWhiteSpace(request.Email))
                throw new ArgumentException("Email is required", nameof(request));

            var tenantKey = request.TenantKey.Trim();
            var existing = await _repository.GetByTenantKeyAsync(tenantKey);

            string? mode;
            if (string.IsNullOrWhiteSpace(request.RefreshMode))
            {
                mode = AppInsightsTokenRefreshModes.Normalize(existing?.RefreshMode) ?? AppInsightsTokenRefreshModes.Manual;
            }
            else
            {
                mode = AppInsightsTokenRefreshModes.Normalize(request.RefreshMode);
                if (mode == null)
                    throw new InvalidOperationException($"refreshMode must be one of: {string.Join(", ", AppInsightsTokenRefreshModes.All)}.");
            }

            var aadTenantId = string.IsNullOrWhiteSpace(request.AadTenantId) ? existing?.AadTenantId : request.AadTenantId.Trim();
            var clientId = string.IsNullOrWhiteSpace(request.ClientId) ? existing?.ClientId : request.ClientId.Trim();
            var hasNewSecret = !string.IsNullOrWhiteSpace(request.ClientSecret);
            var hasSecret = hasNewSecret || existing?.HasClientSecret == true;

            var errors = AppInsightsTokenRefreshModes.Validate(mode, aadTenantId, clientId, hasSecret);
            if (errors.Count > 0)
                throw new InvalidOperationException(string.Join(" ", errors));

            var now = DateTime.UtcNow;
            string encryptedToken;
            DateTime expiresAt;
            if (!string.IsNullOrWhiteSpace(request.Token))
            {
                var cleanToken = request.Token.Trim().Trim('"').Trim();
                await ValidateTokenAsync(cleanToken);
                encryptedToken = _protector.Protect(cleanToken);
                expiresAt = TryReadJwtExpiry(cleanToken) ?? now.AddHours(1);
            }
            else if (mode == AppInsightsTokenRefreshModes.Manual && string.IsNullOrEmpty(existing?.EncryptedToken))
            {
                throw new InvalidOperationException("Token is required for refreshMode 'manual'.");
            }
            else if (existing != null && !string.IsNullOrEmpty(existing.EncryptedToken))
            {
                encryptedToken = existing.EncryptedToken;
                expiresAt = existing.ExpiresAt;
            }
            else
            {
                // No token yet: the row is due immediately, so the next refresh run acquires one.
                encryptedToken = string.Empty;
                expiresAt = now;
            }

            var encryptedSecret = hasNewSecret ? _protector.Protect(request.ClientSecret!.Trim()) : existing?.EncryptedClientSecret;

            if (existing != null)
            {
                existing.Email = request.Email;
                existing.EncryptedToken = encryptedToken;
                existing.ExpiresAt = expiresAt;
                existing.IsActive = true;
                existing.Notes = request.Notes ?? $"Updated by {request.Email} on {now:yyyy-MM-dd HH:mm:ss}";
                existing.RefreshMode = mode;
                existing.AadTenantId = aadTenantId;
                existing.ClientId = clientId;
                existing.EncryptedClientSecret = encryptedSecret;
                await _repository.UpdateAsync(existing);
                _logger.LogInformation("Token updated for tenant {TenantKey} (refresh mode {RefreshMode})", tenantKey, mode);
            }
            else
            {
                await _repository.SaveAsync(new AppInsightsToken
                {
                    TenantKey = tenantKey,
                    Email = request.Email,
                    EncryptedToken = encryptedToken,
                    ExpiresAt = expiresAt,
                    CreatedAt = now,
                    IsActive = true,
                    Notes = request.Notes ?? $"Added by {request.Email} on {now:yyyy-MM-dd HH:mm:ss}",
                    UsageCount = 0,
                    RefreshMode = mode,
                    AadTenantId = aadTenantId,
                    ClientId = clientId,
                    EncryptedClientSecret = encryptedSecret,
                });
                _logger.LogInformation("Token saved for tenant {TenantKey} (refresh mode {RefreshMode})", tenantKey, mode);
            }
        }

        public async Task<StoredAppInsightsToken?> GetStoredTokenAsync(string tenantKey)
        {
            if (string.IsNullOrWhiteSpace(tenantKey))
                return null;

            var token = await _repository.GetByTenantKeyAsync(tenantKey);
            if (token == null || !token.IsActive || string.IsNullOrEmpty(token.EncryptedToken))
                return null;

            try
            {
                var plain = _protector.Unprotect(token.EncryptedToken);
                var expiresAt = new DateTimeOffset(DateTime.SpecifyKind(token.ExpiresAt, DateTimeKind.Utc));
                return new StoredAppInsightsToken(plain, expiresAt);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Stored token for tenant {TenantKey} could not be decrypted ({ErrorType}); it is ignored.", tenantKey, ex.GetType().Name);
                return null;
            }
        }

        /// <summary>Reads the "exp" claim of a JWT. Null when the token is not a readable JWT.</summary>
        public static DateTime? TryReadJwtExpiry(string token)
        {
            try
            {
                var parts = token.Split('.');
                if (parts.Length != 3) return null;
                var payload = parts[1].Replace('-', '+').Replace('_', '/');
                payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
                using var document = System.Text.Json.JsonDocument.Parse(Convert.FromBase64String(payload));
                if (document.RootElement.TryGetProperty("exp", out var exp) && exp.TryGetInt64(out var seconds))
                    return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
            }
            catch (Exception)
            {
                // Not a readable JWT: the caller falls back to the 1-hour default.
            }
            return null;
        }

        public Task SaveTokenAsync(string tenantKey, string email, string token, string notes = null)
        {
            if (string.IsNullOrWhiteSpace(token))
                throw new ArgumentException("Token is required", nameof(token));

            // Keeps the stored refresh settings: a pasted token never switches an automatic row back to manual.
            return SaveTokenAsync(new AppInsightsTokenSaveRequest(tenantKey, email, token, notes));
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

            if (string.IsNullOrEmpty(token.EncryptedToken))
                throw new InvalidOperationException(
                    $"No token acquired yet for tenant '{tenantKey}'.");

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

    /// <summary>Body of a token save/update. ClientSecret is write-only; blank keeps the stored one.</summary>
    public sealed record AppInsightsTokenSaveRequest(
        string TenantKey,
        string Email,
        string? Token,
        string? Notes = null,
        string? RefreshMode = null,
        string? AadTenantId = null,
        string? ClientId = null,
        string? ClientSecret = null);

    /// <summary>A decrypted stored token and when it expires.</summary>
    public sealed record StoredAppInsightsToken(string Token, DateTimeOffset ExpiresAt);

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

        /// <summary>
        /// Writes only the refresh columns (EncryptedToken, ExpiresAt, LastRefreshAt, RefreshCount, LastRefreshError=null)
        /// and only while the row still matches <paramref name="readState"/> (token, mode, tenant, client, secret, active).
        /// Returns false when a user edit landed in between; nothing is written then.
        /// </summary>
        Task<bool> TryUpdateRefreshedTokenAsync(AppInsightsToken refreshed, AppInsightsToken readState);

        /// <summary>Sets only LastRefreshError; every other column (and a concurrent user edit) is left alone.</summary>
        Task UpdateRefreshErrorAsync(string tenantKey, string? error);
    }
}
