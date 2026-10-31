using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using JourneyMonitor.Core.Monitoring;
using JourneyMonitor.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Infrastructure.Search;

/// <summary>
/// Shared transport for the Azure DevOps search indexes. Exists so the token handling - which has
/// one non-obvious rule that cost an afternoon - lives in one place rather than in every client.
/// </summary>
public sealed class AzureDevOpsSearchTransport(
    HttpClient http,
    IOptions<AzureDevOpsSearchOptions> options,
    IAppInsightsTokenService tokenService,
    ILogger<AzureDevOpsSearchTransport> logger)
{
    /// <summary>The fixed Azure DevOps application id. Tokens must be requested for this resource, not for the ARM one.</summary>
    private const string AzureDevOpsResource = "499b84ac-1321-427f-aa17-267ca6975798/.default";

    /// <summary>
    /// Key this transport's out-of-band token is filed under in the shared encrypted token store
    /// (the same store the Application Insights tokens use - it is a key/value store, the key is
    /// free-form). Deliberately not one of the tenant keys: an Application Insights token is
    /// issued for a different resource and would be rejected here with a login redirect.
    /// </summary>
    private const string StoredTokenKey = "azure-devops";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private TokenCredential? _credential;

    public AzureDevOpsSearchOptions Settings => options.Value;

    /// <summary>
    /// Posts to one search index. Never throws for a transport failure: a search that could not run
    /// has to be reported as "failed", because an investigation must not die because a wiki was
    /// briefly unavailable - and "failed" must never be readable as "found nothing".
    /// </summary>
    public async Task<SearchTransportResult> PostAsync(
        string endpoint, Dictionary<string, object?> payload, CancellationToken ct)
    {
        var settings = options.Value;
        string token;
        try
        {
            token = await GetTokenAsync(settings, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "[InspectorMonkey] could not acquire an Azure DevOps token.");
            return SearchTransportResult.Failed($"could not acquire an Azure DevOps token: {ex.Message}");
        }

        var url = $"https://almsearch.dev.azure.com/{settings.Organization}/{settings.Project}/_apis/search/{endpoint}?api-version=7.1";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(payload, options: JsonOptions),
            };
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            using var response = await http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                var detail = response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden
                    ? $" {TokenGuidance(settings, "the token was rejected")}"
                    : string.Empty;
                return SearchTransportResult.Failed($"{endpoint} returned HTTP {(int)response.StatusCode}: {Truncate(body, 300)}.{detail}");
            }

            // A token minted for the wrong resource is not refused: Azure DevOps answers 200 with a
            // sign-in page, which would otherwise surface as an opaque JSON parse error.
            if (body.TrimStart().StartsWith('<'))
            {
                return SearchTransportResult.Failed($"{endpoint} answered with a sign-in page instead of results. {TokenGuidance(settings, "the token is not valid for Azure DevOps")}");
            }

            return SearchTransportResult.Ok(JsonDocument.Parse(body));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return SearchTransportResult.Failed($"{endpoint} timed out after {settings.ResolvedTimeoutSeconds}s.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "[InspectorMonkey] {Endpoint} failed.", endpoint);
            return SearchTransportResult.Failed($"{endpoint} failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Resolution order: stored token, then the configured token, then
    /// <see cref="DefaultAzureCredential"/>. The stored token comes first because it is the only
    /// one an operator can refresh while the container keeps running - Azure tokens last about an
    /// hour, and an expired stored token simply falls through to the next source.
    ///
    /// Azure DevOps lives in a different tenant from the Application Insights resources this
    /// platform reads. Without the explicit tenant the token comes back for the home tenant and the
    /// call answers with a redirect to a login page rather than with an error anyone would notice.
    /// </summary>
    private async Task<string> GetTokenAsync(AzureDevOpsSearchOptions settings, CancellationToken ct)
    {
        var stored = await TryGetStoredTokenAsync();
        if (stored is not null)
        {
            return stored;
        }

        var fromConfiguration = settings.AccessToken;
        if (!string.IsNullOrWhiteSpace(fromConfiguration))
        {
            return fromConfiguration.Trim().Trim('"').Trim();
        }

        _credential ??= CreateCredential(settings);
        try
        {
            var token = await _credential.GetTokenAsync(new TokenRequestContext([AzureDevOpsResource]), ct);
            return token.Token;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(TokenGuidance(settings, ex.Message), ex);
        }
    }

    /// <summary>
    /// A missing, deactivated or expired token is a normal state here, not a fault: it only means
    /// the next source in the chain gets its turn, so it must not surface as a failed search.
    /// </summary>
    private async Task<string?> TryGetStoredTokenAsync()
    {
        try
        {
            var stored = await tokenService.GetTokenAsync(StoredTokenKey);
            return string.IsNullOrWhiteSpace(stored) ? null : stored;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "[InspectorMonkey] no usable stored Azure DevOps token under key '{Key}'.", StoredTokenKey);
            return null;
        }
    }

    private static DefaultAzureCredential CreateCredential(AzureDevOpsSearchOptions settings)
    {
        var credentialOptions = new DefaultAzureCredentialOptions();
        if (!string.IsNullOrWhiteSpace(settings.TenantId))
        {
            credentialOptions.TenantId = settings.TenantId;
        }

        // Local Docker has DOTNET_RUNNING_IN_CONTAINER but no IMDS, so Managed Identity spends its
        // whole retry budget on 169.254.169.254 before the rest of the chain is even tried, and the
        // search ends up reported as failed. Azure Container Apps sets IDENTITY_ENDPOINT /
        // MSI_ENDPOINT, so Managed Identity still runs where it actually exists.
        var inContainer = string.Equals(
            Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"),
            "true",
            StringComparison.OrdinalIgnoreCase);
        var hasManagedIdentityEndpoint =
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("IDENTITY_ENDPOINT"))
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MSI_ENDPOINT"));
        if (inContainer && !hasManagedIdentityEndpoint)
        {
            credentialOptions.ExcludeManagedIdentityCredential = true;
        }

        return new DefaultAzureCredential(credentialOptions);
    }

    /// <summary>
    /// Spells out the scope literally. An earlier incident in this repo was caused by instructions
    /// that named the ARM or Application Insights scope: a token minted for those is a valid token,
    /// so the failure arrives as an HTML login page from Azure DevOps rather than as an auth error.
    /// </summary>
    private static string TokenGuidance(AzureDevOpsSearchOptions settings, string reason)
    {
        var tenant = string.IsNullOrWhiteSpace(settings.TenantId) ? "<azure-devops-tenant-id>" : settings.TenantId;
        return $"{Truncate(reason, 300).TrimEnd('.', ' ')}. Supply one out-of-band instead: "
            + $"`az account get-access-token --tenant {tenant} --scope {AzureDevOpsResource} --query accessToken -o tsv`, "
            + $"then store it under tenant key '{StoredTokenKey}' via POST /api/appinsights/tokens (Settings -> AppInsights Tokens) "
            + $"or set AzureDevOpsSearch__AccessToken. The token must be minted for scope {AzureDevOpsResource} - "
            + "a token for ARM or for Application Insights is accepted by the credential chain and then rejected by Azure DevOps.";
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}

public sealed record SearchTransportResult(bool Succeeded, string? Error, JsonDocument? Document) : IDisposable
{
    public static SearchTransportResult Ok(JsonDocument document) => new(true, null, document);

    public static SearchTransportResult Failed(string error) => new(false, error, null);

    public void Dispose() => Document?.Dispose();
}
