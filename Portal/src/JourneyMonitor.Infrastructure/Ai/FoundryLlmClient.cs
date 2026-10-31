using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Monitoring;
using JourneyMonitor.Core.Options;
using Microsoft.Extensions.Logging;

namespace JourneyMonitor.Infrastructure.Ai;

/// <summary>
/// Calls Microsoft Foundry over its OpenAI-compatible <c>/openai/v1</c> route, which uses implicit
/// versioning (no <c>api-version</c> parameter). The request/response bodies are built and parsed by
/// <see cref="OpenAiChatProtocol"/>, shared with <see cref="GitHubCopilotLlmClient"/> - the only
/// real differences are the base URL, the credential, and that <c>model</c> carries a *deployment*
/// name rather than a model id.
///
/// Authentication is either a resource API key or, preferably, keyless via
/// <see cref="DefaultAzureCredential"/>. Managed identity is the better option here for a reason
/// this repo already paid for: an API key has to be encrypted at rest, and the Data Protection key
/// ring that does that has already caused one QA outage.
///
/// Never throws: an unconfigured or unreachable provider comes back as an empty completion / an
/// <see cref="LlmCompletion.Unavailable"/>, which callers already treat as "unknown" rather than
/// fabricating an answer.
/// </summary>
public sealed class FoundryLlmClient
{
    private const string ProviderTag = "Foundry";

    /// <summary>Refresh a little early so a long investigation never starts a call with a token about to expire.</summary>
    private static readonly TimeSpan TokenRefreshMargin = TimeSpan.FromMinutes(5);

    private readonly HttpClient _http;
    private readonly ILlmClientSettingsResolver _settingsResolver;
    private readonly ILogger<FoundryLlmClient> _logger;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    private TokenCredential? _credential;
    private string? _cachedToken;
    private DateTimeOffset _tokenExpiresOn = DateTimeOffset.MinValue;

    public FoundryLlmClient(HttpClient http, ILlmClientSettingsResolver settingsResolver, ILogger<FoundryLlmClient> logger)
    {
        _http = http;
        _http.Timeout = TimeSpan.FromMinutes(5);
        _settingsResolver = settingsResolver;
        _logger = logger;
    }

    // -----------------------------------------------------------------------------------------
    // Single-prompt completion (Doctor / Scout)
    // -----------------------------------------------------------------------------------------

    public async Task<(string Content, int InputTokens, int OutputTokens, string ModelUsed)> CompleteAsync(
        ResolvedLlmClientSettings settings, string prompt, int maxTokens, CancellationToken ct = default)
    {
        var deployment = settings.FoundryChatDeployment;
        if (string.IsNullOrWhiteSpace(deployment))
        {
            _logger.LogWarning("[{Provider}] No chat deployment configured (Settings tab or Foundry:ChatDeployment) — skipping LLM call.", ProviderTag);
            return ("", 0, 0, "none");
        }

        var request = await BuildRequestAsync(settings, "chat/completions", ct);
        if (request is null) return ("", 0, 0, "none");

        // stream:false on purpose - the non-streaming shape returns a real `usage` block, so token
        // counts are measured rather than estimated from character counts.
        var body = JsonSerializer.Serialize(new
        {
            model = deployment,
            messages = new[] { new { role = "user", content = prompt } },
            max_tokens = maxTokens,
            temperature = 0.2,
            stream = false,
        }, OpenAiChatProtocol.WebJson);

        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        try
        {
            using (request)
            using (var response = await _http.SendAsync(request, ct))
            {
                var responseBody = await response.Content.ReadAsStringAsync(ct);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("[{Provider}] HTTP {Code} from chat/completions for deployment {Deployment}: {Body}",
                        ProviderTag, (int)response.StatusCode, deployment, Trim(responseBody));
                    return ("", 0, 0, "none");
                }

                var completion = OpenAiChatProtocol.ParseToolCallingResponse(deployment, responseBody, _logger, ProviderTag);
                return (completion.Content, completion.TokensIn, completion.TokensOut, deployment);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "[{Provider}] chat/completions call failed.", ProviderTag);
            return ("", 0, 0, "none");
        }
    }

    // -----------------------------------------------------------------------------------------
    // Tool-calling completion (Inspector)
    // -----------------------------------------------------------------------------------------

    public async Task<LlmCompletion> CompleteAsync(
        ResolvedLlmClientSettings settings,
        string model,
        IReadOnlyList<LlmMessage> messages,
        IReadOnlyList<LlmToolDefinition> tools,
        int maxTokens,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(settings.FoundryEndpoint))
        {
            return LlmCompletion.Unavailable(model, "No Foundry endpoint is configured (Settings tab or Foundry:Endpoint).");
        }

        string requestJson;
        try
        {
            requestJson = OpenAiChatProtocol.BuildToolCallingRequestJson(model, messages, tools, maxTokens);
        }
        catch (JsonException ex)
        {
            // A tool's schema is author-written, not model-written: failing here is a bug in our own
            // tool definition, so name it rather than let the provider reject the whole request.
            return LlmCompletion.Unavailable(model, $"A tool's ParametersJsonSchema is not valid JSON: {ex.Message}");
        }

        var request = await BuildRequestAsync(settings, "chat/completions", ct);
        if (request is null)
        {
            return LlmCompletion.Unavailable(model, DescribeCredentialFailure(settings));
        }

        request.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");

        try
        {
            using (request)
            using (var response = await _http.SendAsync(request, ct))
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                if (!response.IsSuccessStatusCode)
                {
                    var detail = string.IsNullOrWhiteSpace(body) ? "empty response body" : Trim(body);

                    // The roster carries deployment names on this provider; a 404 almost always means
                    // the roster still holds model ids from another provider, so say that outright.
                    var hint = response.StatusCode == System.Net.HttpStatusCode.NotFound
                        ? $" '{model}' must be the name of a deployment that exists in this Foundry resource, not a model id from another provider."
                        : "";

                    _logger.LogWarning("[{Provider}] HTTP {Code} from chat/completions for deployment {Deployment}: {Body}",
                        ProviderTag, (int)response.StatusCode, model, detail);
                    return LlmCompletion.Unavailable(model, $"Foundry returned HTTP {(int)response.StatusCode} ({detail}).{hint}");
                }

                return OpenAiChatProtocol.ParseToolCallingResponse(model, body, _logger, ProviderTag);
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "[{Provider}] Could not parse the tool-calling response for deployment {Deployment}.", ProviderTag, model);
            return LlmCompletion.Unavailable(model, $"Unreadable response from Foundry: {ex.Message}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "[{Provider}] Tool-calling chat/completions call failed for deployment {Deployment}.", ProviderTag, model);
            return LlmCompletion.Unavailable(model, $"The request to Foundry failed: {ex.Message}");
        }
    }

    // -----------------------------------------------------------------------------------------
    // Embeddings (knowledge base)
    // -----------------------------------------------------------------------------------------

    public async Task<EmbeddingResult> GetEmbeddingAsync(ResolvedLlmClientSettings settings, string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return EmbeddingResult.Unavailable;

        var deployment = settings.FoundryEmbeddingDeployment;
        if (string.IsNullOrWhiteSpace(deployment))
        {
            _logger.LogWarning("[{Provider}] No embedding deployment configured (Settings tab or Foundry:EmbeddingDeployment) — skipping embedding.", ProviderTag);
            return EmbeddingResult.Unavailable;
        }

        var request = await BuildRequestAsync(settings, "embeddings", ct);
        if (request is null) return EmbeddingResult.Unavailable;

        var input = text.Length > 8000 ? text[..8000] : text;
        request.Content = new StringContent(
            JsonSerializer.Serialize(new { model = deployment, input }, OpenAiChatProtocol.WebJson),
            Encoding.UTF8,
            "application/json");

        try
        {
            using (request)
            using (var response = await _http.SendAsync(request, ct))
            {
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("[{Provider}] HTTP {Code} from embeddings for deployment {Deployment}.",
                        ProviderTag, (int)response.StatusCode, deployment);
                    return EmbeddingResult.Unavailable;
                }

                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
                {
                    return EmbeddingResult.Unavailable;
                }
                if (!data[0].TryGetProperty("embedding", out var arr) || arr.ValueKind != JsonValueKind.Array)
                {
                    return EmbeddingResult.Unavailable;
                }

                // Tagged with the deployment, not the underlying model name: two deployments of the
                // same model are still two independently re-pointable things, and the vector is only
                // guaranteed comparable against others produced by this exact deployment.
                return new EmbeddingResult(arr.EnumerateArray().Select(e => (float)e.GetDouble()).ToArray(), $"foundry:{deployment}");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "[{Provider}] embeddings call failed.", ProviderTag);
            return EmbeddingResult.Unavailable;
        }
    }

    // -----------------------------------------------------------------------------------------
    // Model catalogue (Settings picker)
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Lists the deployments a candidate credential can reach. Uncached and built from the probe's
    /// own values, never the stored ones - the caller may be testing a key it has not saved yet.
    /// </summary>
    public async Task<LlmModelCatalogResult> ListDeploymentsAsync(LlmModelProbe probe, CancellationToken ct = default)
    {
        var endpointError = FoundryEndpointValidator.Validate(probe.FoundryEndpoint);
        if (endpointError is not null)
        {
            return LlmModelCatalogResult.Failed(endpointError);
        }

        var usesManagedIdentity = string.Equals(
            FoundryAuthModes.NormalizeOrDefault(probe.FoundryAuthMode), FoundryAuthModes.ManagedIdentity, StringComparison.Ordinal);

        if (!usesManagedIdentity && string.IsNullOrWhiteSpace(probe.FoundryApiKey))
        {
            return LlmModelCatalogResult.Failed("A Foundry API key is required when the authentication mode is not managed identity.");
        }

        string? bearer;
        try
        {
            bearer = usesManagedIdentity
                ? await GetEntraTokenAsync(ct)
                : probe.FoundryApiKey;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return LlmModelCatalogResult.Failed($"Could not acquire a managed-identity token for {FoundryOptions.EntraScope}: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(bearer))
        {
            return LlmModelCatalogResult.Failed($"Could not acquire a managed-identity token for {FoundryOptions.EntraScope}.");
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{BuildBaseUrl(probe.FoundryEndpoint)}/models");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);

            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                return LlmModelCatalogResult.Failed($"Foundry returned HTTP {(int)response.StatusCode} from /models.");
            }

            var ids = OpenAiChatProtocol.ReadModelIds(await response.Content.ReadAsStringAsync(ct));
            return ids is null
                ? LlmModelCatalogResult.Failed("Unexpected response shape from /models (no 'data' array).")
                : LlmModelCatalogResult.Ok(ids);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "[{Provider}] /models call failed.", ProviderTag);
            return LlmModelCatalogResult.Failed("The request to Foundry failed - see server logs.");
        }
    }

    // -----------------------------------------------------------------------------------------
    // Transport
    // -----------------------------------------------------------------------------------------

    /// <summary>Null when the request cannot be authenticated or addressed - the caller reports that honestly instead of sending a doomed request.</summary>
    private async Task<HttpRequestMessage?> BuildRequestAsync(ResolvedLlmClientSettings settings, string route, CancellationToken ct)
    {
        // Defense in depth: the save path already rejects an untrusted host, but a connection
        // string edited directly in the database (or a future regression in that validation)
        // must not turn into sending this process's own Entra token to an arbitrary server.
        if (FoundryEndpointValidator.Validate(settings.FoundryEndpoint) is { } endpointError)
        {
            _logger.LogWarning("[{Provider}] {Error}", ProviderTag, endpointError);
            return null;
        }

        string? bearer;
        if (settings.UsesManagedIdentity)
        {
            try
            {
                bearer = await GetEntraTokenAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "[{Provider}] Could not acquire a managed-identity token for {Scope}.", ProviderTag, FoundryOptions.EntraScope);
                return null;
            }
        }
        else
        {
            bearer = settings.FoundryApiKey;
            if (string.IsNullOrWhiteSpace(bearer))
            {
                _logger.LogWarning("[{Provider}] No API key configured and the auth mode is not managed identity.", ProviderTag);
                return null;
            }
        }

        if (string.IsNullOrWhiteSpace(bearer)) return null;

        var request = new HttpRequestMessage(HttpMethod.Post, $"{BuildBaseUrl(settings.FoundryEndpoint)}/{route}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return request;
    }

    private static string DescribeCredentialFailure(ResolvedLlmClientSettings settings) =>
        settings.UsesManagedIdentity
            ? $"Could not acquire a managed-identity token for {FoundryOptions.EntraScope}. The identity needs an inference role on the Foundry resource."
            : "No Foundry API key is configured (Settings tab or Foundry:ApiKey).";

    /// <summary>
    /// Accepts the endpoint with or without the <c>/openai/v1</c> route already on it - both forms
    /// are what people copy out of the portal, and appending it twice is a 404 that looks like a
    /// missing deployment.
    /// </summary>
    internal static string BuildBaseUrl(string endpoint)
    {
        var trimmed = endpoint.Trim().TrimEnd('/');
        return trimmed.EndsWith($"/{FoundryOptions.InferenceRoute}", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : $"{trimmed}/{FoundryOptions.InferenceRoute}";
    }

    private async Task<string?> GetEntraTokenAsync(CancellationToken ct)
    {
        if (_cachedToken is not null && DateTimeOffset.UtcNow < _tokenExpiresOn - TokenRefreshMargin)
        {
            return _cachedToken;
        }

        await _tokenLock.WaitAsync(ct);
        try
        {
            if (_cachedToken is not null && DateTimeOffset.UtcNow < _tokenExpiresOn - TokenRefreshMargin)
            {
                return _cachedToken;
            }

            _credential ??= new DefaultAzureCredential();
            var token = await _credential.GetTokenAsync(new TokenRequestContext([FoundryOptions.EntraScope]), ct);

            _cachedToken = token.Token;
            _tokenExpiresOn = token.ExpiresOn;
            return _cachedToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private static string Trim(string body) => body.Length > 500 ? body[..500] : body;
}
