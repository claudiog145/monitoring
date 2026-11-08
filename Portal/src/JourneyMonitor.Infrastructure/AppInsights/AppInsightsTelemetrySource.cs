using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring;
using JourneyMonitor.Core.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Infrastructure.AppInsights;

/// <summary>
/// Executes the same KQL text the original Node app sent to Application Insights, via the
/// same "classic" REST endpoint (https://api.applicationinsights.io/v1/apps/{appId}/query; the base
/// is Insights:QueryEndpoint, which the demo stack points at its fake server).
///
/// Credential order (contracts-company §2.1):
/// 1. API key from Insights:QueryApiKeys for the app id (or its <c>*</c> wildcard entry).
/// 2. Stored token for the tenant (ITenantResolver key, from the company profile's TelemetryTenants;
///    tenant + domain first, then the tenant alone) while ExpiresAt &gt; now + 2 min. The cache entry
///    expires at ExpiresAt − 2 min, and a 401/403 evicts it and retries once with step 3.
/// 3. DefaultAzureCredential per tenant (cached: it renews itself).
///
/// Extension point: a future FabricTelemetrySource can implement ITelemetrySource against
/// Microsoft Fabric/Eventhouse without any change to Core use cases, mappers or calculators.
/// </summary>
public sealed class AppInsightsTelemetrySource : ITelemetrySource
{
    public const string PathApiKey = "api-key";
    public const string PathStoredToken = "stored-token";
    public const string PathDefaultCredential = "default-credential";

    private const string QueryScope = "https://api.applicationinsights.io/.default";
    private static readonly TimeSpan StoredTokenSkew = TimeSpan.FromMinutes(2);

    // DefaultAzureCredential renews its own tokens, so one per tenant is shared by every instance.
    private static readonly ConcurrentDictionary<string, TokenCredential> SharedDefaultCredentials = new();

    private readonly HttpClient _httpClient;
    private readonly IAppInsightsTokenService _tokenService;
    private readonly IOptions<InsightsOptions> _insightsOptions;
    private readonly ILogger<AppInsightsTelemetrySource> _logger;
    private readonly IReadOnlyDictionary<string, string> _apiKeyByAppId;
    private readonly Uri _queryHost;
    private readonly Func<string?, TokenCredential> _defaultCredentialFactory;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, TokenCredential> _defaultByTenant = new();
    private readonly ConcurrentDictionary<string, StoredAppInsightsToken> _storedByTenantKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly ITenantResolver? _tenantResolver;
    private readonly ConcurrentDictionary<string, string> _loggedResolutions = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="tenantResolver">Absent means no tenant map: a tenant's stored token is looked up under its own UUID.</param>
    [ActivatorUtilitiesConstructor]
    public AppInsightsTelemetrySource(
        HttpClient httpClient,
        IAppInsightsTokenService tokenService,
        IOptions<InsightsOptions> insightsOptions,
        ILogger<AppInsightsTelemetrySource> logger,
        ITenantResolver? tenantResolver = null)
        : this(httpClient, tokenService, insightsOptions, logger, defaultCredentialFactory: null, time: null, tenantResolver)
    {
    }

    /// <param name="defaultCredentialFactory">Builds the step-3 credential for a tenant id; tests inject a fake so Azure is never called.</param>
    public AppInsightsTelemetrySource(
        HttpClient httpClient,
        IAppInsightsTokenService tokenService,
        IOptions<InsightsOptions> insightsOptions,
        ILogger<AppInsightsTelemetrySource> logger,
        Func<string?, TokenCredential>? defaultCredentialFactory,
        TimeProvider? time = null,
        ITenantResolver? tenantResolver = null)
    {
        _httpClient = httpClient;
        _tokenService = tokenService;
        _insightsOptions = insightsOptions;
        _logger = logger;
        _tenantResolver = tenantResolver;
        _apiKeyByAppId = ParseApiKeys(insightsOptions.Value.QueryApiKeys);
        _queryHost = new Uri(insightsOptions.Value.ResolvedQueryEndpoint, UriKind.Absolute);
        _defaultCredentialFactory = defaultCredentialFactory
            ?? (tenantId => SharedDefaultCredentials.GetOrAdd(tenantId ?? string.Empty, _ => CreateDefaultCredential(tenantId)));
        _time = time ?? TimeProvider.System;
    }

    public async Task<IReadOnlyList<TelemetryJourneyRow>> QueryAsync(TelemetryQueryRequest request, CancellationToken cancellationToken)
    {
        var appId = request.AppInsightsAppId ?? "";
        if (_apiKeyByAppId.TryGetValue(appId, out var apiKey)
            || _apiKeyByAppId.TryGetValue(InsightsOptions.WildcardAppId, out apiKey))
        {
            LogPath(appId, PathApiKey);
            return await SendAsync(request, r => r.Headers.Add("X-Api-Key", apiKey), cancellationToken);
        }

        string? tenantKey = null;
        StoredAppInsightsToken? stored = null;
        if (!string.IsNullOrEmpty(request.TenantId))
        {
            var domain = DomainForAppId(appId);
            tenantKey = await ResolveTenantKeyAsync(request.TenantId, domain, cancellationToken);
            stored = await GetFreshStoredTokenAsync(tenantKey);

            // A domain-specific key with no token of its own falls back to the tenant's first key,
            // which is what every query of a shared tenant used before domains were declared.
            if (stored is null && domain is not null)
            {
                var tenantOnlyKey = await ResolveTenantKeyAsync(request.TenantId, null, cancellationToken);
                if (!string.Equals(tenantOnlyKey, tenantKey, StringComparison.OrdinalIgnoreCase))
                {
                    stored = await GetFreshStoredTokenAsync(tenantOnlyKey);
                    if (stored is not null) tenantKey = tenantOnlyKey;
                }
            }
        }

        if (stored is not null)
        {
            LogPath(appId, PathStoredToken);
            using var response = await SendRawAsync(request, r => SetBearer(r, stored.Token), cancellationToken);
            if (response.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden))
            {
                return await ReadRowsAsync(response, cancellationToken);
            }

            _storedByTenantKey.TryRemove(tenantKey!, out _);
            _logger.LogWarning(
                "App Insights rejected the stored token for tenant key {TenantKey} ({Status}); evicted, retrying once via {Path}.",
                tenantKey, (int)response.StatusCode, PathDefaultCredential);
        }

        LogPath(appId, PathDefaultCredential);
        var credential = _defaultByTenant.GetOrAdd(request.TenantId ?? string.Empty, _ => _defaultCredentialFactory(request.TenantId));
        var token = await credential.GetTokenAsync(new TokenRequestContext([QueryScope]), cancellationToken);
        return await SendAsync(request, r => SetBearer(r, token.Token), cancellationToken);
    }

    /// <summary>The cached or stored token for the key when it is valid beyond now + 2 min; otherwise null (never throws).</summary>
    private async Task<StoredAppInsightsToken?> GetFreshStoredTokenAsync(string tenantKey)
    {
        var cutoff = _time.GetUtcNow() + StoredTokenSkew;
        if (_storedByTenantKey.TryGetValue(tenantKey, out var cached))
        {
            if (cached.ExpiresAt > cutoff) return cached;
            _storedByTenantKey.TryRemove(tenantKey, out _);
        }

        StoredAppInsightsToken? stored;
        try
        {
            stored = await _tokenService.GetStoredTokenAsync(tenantKey);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not read the stored App Insights token for tenant key {TenantKey} ({ErrorType}); using {Path}.",
                tenantKey, ex.GetType().Name, PathDefaultCredential);
            return null;
        }

        if (stored is null || string.IsNullOrWhiteSpace(stored.Token) || stored.ExpiresAt <= cutoff) return null;
        _storedByTenantKey[tenantKey] = stored;
        return stored;
    }

    /// <summary>The stored-token key for the tenant; the tenant id itself when there is no resolver or it fails (never throws).</summary>
    private async Task<string> ResolveTenantKeyAsync(string tenantId, string? domain, CancellationToken cancellationToken)
    {
        if (_tenantResolver is null) return tenantId;

        string key;
        try
        {
            key = await _tenantResolver.ResolveTenantKeyAsync(tenantId, domain, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not resolve tenant {TenantId} to a token key ({ErrorType}); using the tenant id.",
                tenantId, ex.GetType().Name);
            return tenantId;
        }

        if (string.IsNullOrWhiteSpace(key)) key = tenantId;
        var logKey = $"{tenantId}|{domain}";
        if (!_loggedResolutions.TryGetValue(logKey, out var previous) || !string.Equals(previous, key, StringComparison.Ordinal))
        {
            _loggedResolutions[logKey] = key;
            _logger.LogInformation("Resolved tenant {TenantId} (domain {Domain}) to key '{TenantKey}'.",
                tenantId, domain ?? "-", key);
        }
        return key;
    }

    /// <summary>
    /// The InsightsOptions slot (lower-cased property name) whose app id - production or staging -
    /// is <paramref name="appId"/>; null when none is.
    /// </summary>
    private string? DomainForAppId(string appId)
    {
        if (string.IsNullOrWhiteSpace(appId)) return null;
        var options = _insightsOptions.Value;

        if (SameId(options.Hotels?.AppInsightsAppId, appId) || SameId(options.Hotels?.Staging?.AppInsightsAppId, appId)
            || SameId(options.AppInsightsAppId, appId))
            return nameof(InsightsOptions.Hotels).ToLowerInvariant();
        if (SameId(options.Air?.AppInsightsAppId, appId) || SameId(options.Air?.Staging?.AppInsightsAppId, appId))
            return nameof(InsightsOptions.Air).ToLowerInvariant();
        if (SameId(options.Cruises?.AppInsightsAppId, appId) || SameId(options.Cruises?.Staging?.AppInsightsAppId, appId))
            return nameof(InsightsOptions.Cruises).ToLowerInvariant();
        return null;
    }

    private static bool SameId(string? configured, string appId) =>
        !string.IsNullOrWhiteSpace(configured) && string.Equals(configured.Trim(), appId.Trim(), StringComparison.OrdinalIgnoreCase);

    private void LogPath(string appId, string path) =>
        _logger.LogDebug("App Insights query for app {AppId} authenticated via {AuthPath}.", appId, path);

    private static void SetBearer(HttpRequestMessage request, string token) =>
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

    private async Task<IReadOnlyList<TelemetryJourneyRow>> SendAsync(
        TelemetryQueryRequest request, Action<HttpRequestMessage> authorize, CancellationToken cancellationToken)
    {
        using var response = await SendRawAsync(request, authorize, cancellationToken);
        return await ReadRowsAsync(response, cancellationToken);
    }

    private Task<HttpResponseMessage> SendRawAsync(
        TelemetryQueryRequest request, Action<HttpRequestMessage> authorize, CancellationToken cancellationToken)
    {
        // A request message cannot be sent twice, so every attempt builds its own.
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, new Uri(_queryHost, $"/v1/apps/{request.AppInsightsAppId}/query"))
        {
            Content = JsonContent.Create(new { query = request.Kql }),
        };
        authorize(httpRequest);
        return SendAndDisposeRequestAsync(httpRequest, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAndDisposeRequestAsync(HttpRequestMessage httpRequest, CancellationToken cancellationToken)
    {
        using (httpRequest)
        {
            return await _httpClient.SendAsync(httpRequest, cancellationToken);
        }
    }

    private static async Task<IReadOnlyList<TelemetryJourneyRow>> ReadRowsAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var snippet = body.Length > 400 ? body[..400] : body;
            throw new InvalidOperationException($"App Insights query {(int)response.StatusCode}: {snippet}");
        }

        using var document = JsonDocument.Parse(body);
        return TableToRows(document.RootElement);
    }

    /// <summary>Parses <see cref="InsightsOptions.QueryApiKeys"/> (<c>appId=key;appId=key</c>, pairs also
    /// separated by newlines). The wildcard entry is <c>*=key</c> or <c>*:key</c> (the colon form is
    /// accepted only for <c>*</c>) and is stored under <see cref="InsightsOptions.WildcardAppId"/>. A malformed
    /// pair is skipped rather than thrown on: the Entra ID path below is the correct default, and an
    /// unparsable key must not take the whole platform down at startup.</summary>
    private static IReadOnlyDictionary<string, string> ParseApiKeys(string? configured)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(configured)) return map;

        foreach (var pair in configured.Split([';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator < 0 && pair.StartsWith(InsightsOptions.WildcardAppId, StringComparison.Ordinal))
            {
                var colon = pair.IndexOf(':');
                if (colon > 0 && pair[..colon].Trim() == InsightsOptions.WildcardAppId) separator = colon;
            }
            if (separator <= 0 || separator == pair.Length - 1) continue;

            var appId = pair[..separator].Trim();
            var key = pair[(separator + 1)..].Trim();
            if (appId.Length > 0 && key.Length > 0) map[appId] = key;
        }
        return map;
    }

    private static TokenCredential CreateDefaultCredential(string? tenantId)
    {
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

        return new DefaultAzureCredential(options);
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
