using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring;
using JourneyMonitor.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Infrastructure.AppInsights;

/// <summary>
/// Executes the same KQL text the original Node app sent to Application Insights, via the
/// same "classic" REST endpoint (https://api.applicationinsights.io/v1/apps/{appId}/query).
/// 
/// Token resolution order (Option 1 - External Tokens):
/// 1. Check if user has stored a token for this tenant in the database (AppInsightsTokenService)
/// 2. Fall back to DefaultAzureCredential (az CLI / Managed Identity / Visual Studio)
///
/// This allows users to provide their own Azure tokens externally without requiring complex
/// SSO or service principal setup.
///
/// Extension point: a future FabricTelemetrySource can implement ITelemetrySource against
/// Microsoft Fabric/Eventhouse without any change to Core use cases, mappers or calculators.
/// </summary>
public sealed class AppInsightsTelemetrySource : ITelemetrySource
{
    private const string QueryScope = "https://api.applicationinsights.io/.default";
    private static readonly Uri QueryHost = new("https://api.applicationinsights.io");

    private readonly HttpClient _httpClient;
    private readonly IAppInsightsTokenService _tokenService;
    private readonly IOptions<InsightsOptions> _insightsOptions;
    private readonly Dictionary<string, TokenCredential> _credentialByTenant = new();
    private readonly ILogger<AppInsightsTelemetrySource> _logger;

    public AppInsightsTelemetrySource(
        HttpClient httpClient,
        IAppInsightsTokenService tokenService,
        IOptions<InsightsOptions> insightsOptions,
        ILogger<AppInsightsTelemetrySource> logger)
    {
        _httpClient = httpClient;
        _tokenService = tokenService;
        _insightsOptions = insightsOptions;
        _logger = logger;
    }

    public async Task<IReadOnlyList<TelemetryJourneyRow>> QueryAsync(TelemetryQueryRequest request, CancellationToken cancellationToken)
    {
        var credential = await GetCredentialAsync(request.TenantId, cancellationToken);
        var token = await credential.GetTokenAsync(new TokenRequestContext([QueryScope]), cancellationToken);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, new Uri(QueryHost, $"/v1/apps/{request.AppInsightsAppId}/query"))
        {
            Content = JsonContent.Create(new { query = request.Kql }),
        };
        httpRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Token);

        using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var snippet = body.Length > 400 ? body[..400] : body;
            throw new InvalidOperationException($"App Insights query {(int)response.StatusCode}: {snippet}");
        }

        using var document = JsonDocument.Parse(body);
        return TableToRows(document.RootElement);
    }

    /// <summary>Get credential for tenant: first try stored token (Option 1), then DefaultAzureCredential</summary>
    private async Task<TokenCredential> GetCredentialAsync(string? tenantId, CancellationToken cancellationToken)
    {
        var key = tenantId ?? string.Empty;
        if (_credentialByTenant.TryGetValue(key, out var existing)) return existing;

        // Try Option 1: Stored token from user
        // First, resolve the tenant UUID to a domain name / tenant key for token lookup
        if (!string.IsNullOrEmpty(tenantId))
        {
            try
            {
                var tenantKey = ResolveTenantKey(tenantId);
                _logger.LogInformation($"Resolved tenant UUID {tenantId} to key '{tenantKey}'");
                
                var storedToken = await _tokenService.GetTokenAsync(tenantKey);
                if (!string.IsNullOrWhiteSpace(storedToken))
                {
                    _logger.LogInformation($"Using stored token for tenant {tenantId} (key: {tenantKey})");
                    var bearerCredential = new BearerTokenCredential(storedToken);
                    _credentialByTenant[key] = bearerCredential;
                    return bearerCredential;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Failed to retrieve stored token for tenant {tenantId}: {ex.Message}. Falling back to DefaultAzureCredential.");
            }
        }

        // Fallback: DefaultAzureCredential
        var options = new DefaultAzureCredentialOptions();
        if (!string.IsNullOrEmpty(tenantId))
        {
            options.TenantId = tenantId;
        }

        // Local Docker has DOTNET_RUNNING_IN_CONTAINER but no IMDS. Skip Managed Identity there so
        // Azure CLI / service-principal env vars can run without a long IMDS timeout. Azure Container
        // Apps sets IDENTITY_ENDPOINT / MSI_ENDPOINT, so Managed Identity still runs in Azure.
        var inContainer = string.Equals(
            Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"),
            "true",
            StringComparison.OrdinalIgnoreCase);
        var hasManagedIdentityEndpoint =
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("IDENTITY_ENDPOINT"))
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MSI_ENDPOINT"));
        if (inContainer && !hasManagedIdentityEndpoint)
        {
            options.ExcludeManagedIdentityCredential = true;
        }

        var credential = new DefaultAzureCredential(options);
        _credentialByTenant[key] = credential;
        return credential;
    }

    /// <summary>
    /// Resolve a tenant UUID to a domain name or company key for token lookup.
    /// Maps Azure AD tenant GUIDs to their configured domains and company names.
    /// Tries multiple possible keys: domain name first, then company names for that domain.
    /// </summary>
    private string ResolveTenantKey(string tenantUuid)
    {
        var insightsOpts = _insightsOptions.Value;
        
        // Map Hotels/Arrivia (currently saved as "arrivia" in the database, but should eventually be "hotels")
        if (!string.IsNullOrEmpty(insightsOpts.Hotels?.TenantId) && 
            insightsOpts.Hotels.TenantId.Equals(tenantUuid, StringComparison.OrdinalIgnoreCase))
        {
            // Return "arrivia" because that's the current convention in the database
            // Hotels domain maps to Arrivia company in Azure
            return "arrivia"; 
        }
        
        // Map Air/Ice
        if (!string.IsNullOrEmpty(insightsOpts.Air?.TenantId) && 
            insightsOpts.Air.TenantId.Equals(tenantUuid, StringComparison.OrdinalIgnoreCase))
        {
            // Air domain maps to Ice company
            return "ice";
        }
        
        // Map Cruises
        if (!string.IsNullOrEmpty(insightsOpts.Cruises?.TenantId) && 
            insightsOpts.Cruises.TenantId.Equals(tenantUuid, StringComparison.OrdinalIgnoreCase))
        {
            // Cruises - same tenant as Hotels but different domain
            return "cruises";
        }
        
        // Fallback: return the UUID itself (might be used as a direct key)
        _logger.LogWarning($"Could not resolve tenant UUID {tenantUuid} to a known domain. Falling back to UUID as key.");
        return tenantUuid;
    }

    /// <summary>Mirrors tableToRows()/maybeParseJson() in query-appinsights.js: first result table,
    /// columns zipped with row values, embedded JSON-looking strings parsed into objects/arrays.</summary>
    private static List<TelemetryJourneyRow> TableToRows(JsonElement root)
    {
        var rows = new List<TelemetryJourneyRow>();
        if (!root.TryGetProperty("tables", out var tables) || tables.ValueKind != JsonValueKind.Array || tables.GetArrayLength() == 0)
        {
            return rows;
        }
        var table = tables[0];
        if (!table.TryGetProperty("columns", out var columns) || !table.TryGetProperty("rows", out var dataRows))
        {
            return rows;
        }
        var names = columns.EnumerateArray().Select(c => c.GetProperty("name").GetString() ?? "").ToList();

        foreach (var row in dataRows.EnumerateArray())
        {
            var telemetryRow = new TelemetryJourneyRow();
            var i = 0;
            foreach (var cell in row.EnumerateArray())
            {
                if (i >= names.Count) break;
                telemetryRow.Extra[names[i]] = MaybeParseJson(cell);
                i++;
            }
            rows.Add(telemetryRow);
        }
        return rows;
    }

    private static JsonElement MaybeParseJson(JsonElement cell)
    {
        if (cell.ValueKind != JsonValueKind.String) return cell.Clone();
        var text = cell.GetString();
        if (string.IsNullOrEmpty(text)) return cell.Clone();
        var trimmed = text.TrimStart();
        if (trimmed.Length == 0 || (trimmed[0] != '[' && trimmed[0] != '{')) return cell.Clone();
        try
        {
            using var parsed = JsonDocument.Parse(text);
            return parsed.RootElement.Clone();
        }
        catch (JsonException)
        {
            return cell.Clone();
        }
    }
}
