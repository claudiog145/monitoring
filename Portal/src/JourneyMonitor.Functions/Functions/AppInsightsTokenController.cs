using System.Net;
using System.Text.Json;
using JourneyMonitor.Core.Monitoring;
using JourneyMonitor.Core.Monitoring.TokenRefresh;
using JourneyMonitor.Core.Options;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Functions.Functions;

/// <summary>
/// App Insights token management (contracts-company §2.4), fronted by the Web proxy. Fail-closed:
/// writes (save, refresh, deactivate) need X-Settings-Write-Secret; reads (list, status, tenants) need
/// X-Internal-Api-Secret because they expose emails, tenant ids and client ids. No response ever
/// carries a token, the encrypted token or a client secret.
/// </summary>
public sealed class AppInsightsTokenController(
    IAppInsightsTokenService tokenService,
    IAppInsightsTokenRefreshService refreshService,
    ITenantResolver tenantResolver,
    IOptions<MonitoringOptions> monitoringOptions,
    ILogger<AppInsightsTokenController> logger)
{
    private Task<HttpResponseData>? RequireWriteSecret(HttpRequestData req) =>
        SettingsWriteSecretGuard.HasMatchingWriteSecret(req, monitoringOptions.Value.SettingsWriteSecret)
            ? null
            : Spec1000Http.WriteJsonAsync(req, HttpStatusCode.Unauthorized, new
            {
                error = "Missing or invalid X-Settings-Write-Secret header. Monitoring:SettingsWriteSecret must be configured for this endpoint to accept writes.",
            });

    private Task<HttpResponseData>? RequireInternalSecret(HttpRequestData req) =>
        SettingsWriteSecretGuard.HasMatchingSecret(req, monitoringOptions.Value.InternalApiSecret, SettingsWriteSecretGuard.InternalApiSecretHeaderName)
            ? null
            : Spec1000Http.WriteJsonAsync(req, HttpStatusCode.Unauthorized, new
            {
                error = "Missing or invalid X-Internal-Api-Secret header. Monitoring:InternalApiSecret must be configured for this endpoint to accept requests.",
            });

    private static readonly JsonSerializerOptions BodyOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Metadata only: never EncryptedToken, a token or a secret.</summary>
    public static object ToView(AppInsightsToken token) => new
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
        notes = token.Notes,
        refreshMode = AppInsightsTokenRefreshModes.Normalize(token.RefreshMode) ?? AppInsightsTokenRefreshModes.Manual,
        aadTenantId = token.AadTenantId,
        clientId = token.ClientId,
        hasClientSecret = token.HasClientSecret,
        hasToken = !string.IsNullOrEmpty(token.EncryptedToken),
        lastRefreshAt = token.LastRefreshAt,
        lastRefreshError = token.LastRefreshError,
        refreshCount = token.RefreshCount,
    };

    [Function("GetAppInsightsTokens")]
    public async Task<HttpResponseData> GetTokensAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "appinsights/tokens")] HttpRequestData req)
    {
        var denied = RequireInternalSecret(req);
        if (denied is not null) return await denied;

        try
        {
            var tokens = await tokenService.GetAllTokensAsync();
            return await Spec1000Http.WriteJsonAsync(req, HttpStatusCode.OK, new
            {
                success = true,
                count = tokens.Count,
                tokens = tokens.Select(ToView).ToList(),
            });
        }
        catch (Exception ex)
        {
            logger.LogError("GetAppInsightsTokens failed: {ErrorType}", ex.GetType().Name);
            return await Spec1000Http.WriteJsonAsync(req, HttpStatusCode.InternalServerError, new { error = "Failed to retrieve tokens" });
        }
    }

    /// <summary>
    /// Save or update a token. Body: tenantKey, email (or userEmail), token, notes, refreshMode,
    /// aadTenantId, clientId, clientSecret. The token may be blank for azure-cli / service-principal.
    /// </summary>
    [Function("SaveAppInsightsToken")]
    public async Task<HttpResponseData> SaveTokenAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "appinsights/tokens")] HttpRequestData req,
        CancellationToken ct)
    {
        var denied = RequireWriteSecret(req);
        if (denied is not null) return await denied;

        TokenSaveBody? body;
        try
        {
            body = await JsonSerializer.DeserializeAsync<TokenSaveBody>(req.Body, BodyOptions, ct);
        }
        catch (JsonException)
        {
            body = null;
        }

        if (body is null)
            return await Spec1000Http.WriteJsonAsync(req, HttpStatusCode.BadRequest, new { success = false, error = "Request body must be a valid JSON object." });

        var email = string.IsNullOrWhiteSpace(body.Email) ? body.UserEmail : body.Email;
        if (string.IsNullOrWhiteSpace(body.TenantKey) || string.IsNullOrWhiteSpace(email))
            return await Spec1000Http.WriteJsonAsync(req, HttpStatusCode.BadRequest, new { success = false, error = "Missing required fields (tenantKey, email/userEmail)." });

        try
        {
            await tokenService.SaveTokenAsync(new AppInsightsTokenSaveRequest(
                body.TenantKey.Trim(), email.Trim(), body.Token, body.Notes, body.RefreshMode, body.AadTenantId, body.ClientId, body.ClientSecret));
            return await Spec1000Http.WriteJsonAsync(req, HttpStatusCode.OK, new
            {
                success = true,
                message = $"Token saved successfully for tenant '{body.TenantKey.Trim()}'",
                tenant = body.TenantKey.Trim(),
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return await Spec1000Http.WriteJsonAsync(req, HttpStatusCode.BadRequest, new { success = false, error = ex.Message });
        }
        catch (Exception ex)
        {
            logger.LogError("SaveAppInsightsToken failed: {ErrorType}", ex.GetType().Name);
            return await Spec1000Http.WriteJsonAsync(req, HttpStatusCode.InternalServerError, new { success = false, error = "An error occurred while saving the token" });
        }
    }

    /// <summary>200 with the new expiresAt, 404 unknown key, 409 manual / deactivated / changed meanwhile, 502 acquisition failure.</summary>
    [Function("RefreshAppInsightsToken")]
    public async Task<HttpResponseData> RefreshTokenAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "appinsights/tokens/{tenantKey}/refresh")] HttpRequestData req,
        string tenantKey,
        CancellationToken ct)
    {
        var denied = RequireWriteSecret(req);
        if (denied is not null) return await denied;

        var result = await refreshService.RefreshNowAsync(tenantKey, ct);
        return result.Status switch
        {
            TokenRefreshNowStatus.Refreshed => await Spec1000Http.WriteJsonAsync(req, HttpStatusCode.OK, new { success = true, tenantKey, expiresAt = result.ExpiresAt }),
            TokenRefreshNowStatus.NotFound => await Spec1000Http.WriteJsonAsync(req, HttpStatusCode.NotFound, new { success = false, error = result.Error }),
            TokenRefreshNowStatus.ManualRejected => await Spec1000Http.WriteJsonAsync(req, HttpStatusCode.Conflict, new { success = false, error = AppInsightsTokenRefreshService.RefreshManualRejectedMessage }),
            TokenRefreshNowStatus.Inactive => await Spec1000Http.WriteJsonAsync(req, HttpStatusCode.Conflict, new { success = false, error = AppInsightsTokenRefreshService.RefreshInactiveMessage }),
            TokenRefreshNowStatus.Changed => await Spec1000Http.WriteJsonAsync(req, HttpStatusCode.Conflict, new { success = false, error = AppInsightsTokenRefreshService.RefreshChangedMessage }),
            _ => await Spec1000Http.WriteJsonAsync(req, HttpStatusCode.BadGateway, new { success = false, error = result.Error }),
        };
    }

    [Function("DeactivateAppInsightsToken")]
    public async Task<HttpResponseData> DeactivateTokenAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "appinsights/tokens/{tenantKey}/deactivate")] HttpRequestData req,
        string tenantKey)
    {
        var denied = RequireWriteSecret(req);
        if (denied is not null) return await denied;

        try
        {
            await tokenService.DeactivateTokenAsync(tenantKey);
            return await Spec1000Http.WriteJsonAsync(req, HttpStatusCode.OK, new { success = true, message = $"Token deactivated for {tenantKey}" });
        }
        catch (InvalidOperationException ex)
        {
            return await Spec1000Http.WriteJsonAsync(req, HttpStatusCode.NotFound, new { error = ex.Message });
        }
        catch (Exception ex)
        {
            logger.LogError("DeactivateAppInsightsToken failed: {ErrorType}", ex.GetType().Name);
            return await Spec1000Http.WriteJsonAsync(req, HttpStatusCode.InternalServerError, new { error = "Failed to deactivate token" });
        }
    }

    [Function("GetAppInsightsTokenStatus")]
    public async Task<HttpResponseData> GetTokenStatusAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "appinsights/tokens/{tenantKey}/status")] HttpRequestData req,
        string tenantKey)
    {
        var denied = RequireInternalSecret(req);
        if (denied is not null) return await denied;

        try
        {
            var hasValid = await tokenService.HasValidTokenAsync(tenantKey);
            return await Spec1000Http.WriteJsonAsync(req, HttpStatusCode.OK, new
            {
                tenantKey,
                hasValidToken = hasValid,
                message = hasValid ? $"Token configured for {tenantKey}" : $"No valid token for {tenantKey}.",
            });
        }
        catch (Exception ex)
        {
            logger.LogError("GetAppInsightsTokenStatus failed: {ErrorType}", ex.GetType().Name);
            return await Spec1000Http.WriteJsonAsync(req, HttpStatusCode.InternalServerError, new { error = "Failed to check token status" });
        }
    }

    [Function("GetAppInsightsTenants")]
    public async Task<HttpResponseData> GetTenantsAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "appinsights/tenants")] HttpRequestData req)
    {
        var denied = RequireInternalSecret(req);
        if (denied is not null) return await denied;

        var tenants = await tenantResolver.GetAllTenantsAsync();
        return await Spec1000Http.WriteJsonAsync(req, HttpStatusCode.OK, new
        {
            success = true,
            count = tenants.Count,
            tenants = tenants.Select(t => new { key = t.TenantKey, displayName = t.TenantKey.ToUpperInvariant() }),
        });
    }

    public sealed class TokenSaveBody
    {
        public string? TenantKey { get; set; }
        public string? Email { get; set; }
        public string? UserEmail { get; set; }
        public string? Token { get; set; }
        public string? Notes { get; set; }
        public string? RefreshMode { get; set; }
        public string? AadTenantId { get; set; }
        public string? ClientId { get; set; }
        public string? ClientSecret { get; set; }
    }
}
