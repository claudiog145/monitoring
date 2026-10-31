using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Options;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Functions.Functions;

/// <summary>
/// POST /api/settings/llm-client/models — ad-hoc lookup for the Settings tab's model picker: given
/// a candidate credential (not necessarily saved yet), returns what it can reach via
/// <see cref="ILlmModelCatalog"/> - GitHub Copilot model ids, or Foundry deployment names. Nothing
/// is persisted here - see <see cref="UpsertLlmClientSettingFunction"/> for the actual save.
///
/// Requires <see cref="MonitoringOptions.InternalApiSecret"/> via the X-Internal-Api-Secret
/// header (fails CLOSED, see SettingsWriteSecretGuard): this Function is
/// <see cref="AuthorizationLevel.Anonymous"/> like every endpoint in this app, but unlike a plain
/// read it makes a real outbound call using whatever credential the caller supplies - reachable
/// directly (bypassing the Web proxy's Entra check) it would let anyone use this Function as a
/// proxy to probe arbitrary credentials against either provider.
/// </summary>
public sealed class ListLlmClientModelsFunction(
    ILlmModelCatalog modelCatalog,
    IOptions<MonitoringOptions> monitoringOptions,
    ILogger<ListLlmClientModelsFunction> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Function("ListLlmClientModels")]
    public async Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "settings/llm-client/models")] HttpRequestData req,
        CancellationToken ct)
    {
        if (!SettingsWriteSecretGuard.HasMatchingSecret(req, monitoringOptions.Value.InternalApiSecret, SettingsWriteSecretGuard.InternalApiSecretHeaderName))
        {
            return await WriteJsonAsync(req, HttpStatusCode.Unauthorized, new { error = "Missing or invalid X-Internal-Api-Secret header. Monitoring:InternalApiSecret must be configured for this endpoint to respond." });
        }

        try
        {
            var request = await JsonSerializer.DeserializeAsync<ListModelsRequestBody>(req.Body, JsonOptions, ct);
            var provider = LlmProviders.NormalizeOrDefault(request?.Provider, LlmProviders.Default);

            // Each provider needs a different minimum before a probe is even worth attempting;
            // saying which one is missing beats a generic 502 from the provider.
            if (string.Equals(provider, LlmProviders.Foundry, StringComparison.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(request?.FoundryEndpoint))
                {
                    return await WriteJsonAsync(req, HttpStatusCode.BadRequest, new { error = "foundryEndpoint is required when provider is 'foundry'." });
                }
            }
            else if (string.IsNullOrWhiteSpace(request?.GitHubToken))
            {
                return await WriteJsonAsync(req, HttpStatusCode.BadRequest, new { error = "gitHubToken is required when provider is 'github-copilot'." });
            }

            var result = await modelCatalog.ListAsync(new LlmModelProbe
            {
                Provider = provider,
                GitHubToken = request?.GitHubToken,
                FoundryEndpoint = request?.FoundryEndpoint,
                FoundryAuthMode = request?.FoundryAuthMode,
                FoundryApiKey = request?.FoundryApiKey,
            }, ct);

            return result.Success
                ? await WriteJsonAsync(req, HttpStatusCode.OK, new { provider, models = result.Models })
                : await WriteJsonAsync(req, HttpStatusCode.BadGateway, new { error = result.Error });
        }
        catch (JsonException)
        {
            return await WriteJsonAsync(req, HttpStatusCode.BadRequest, new { error = "Request body must be valid JSON." });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ListLlmClientModels failed");
            return await WriteJsonAsync(req, HttpStatusCode.InternalServerError, new { error = "Unexpected server error." });
        }
    }

    private static async Task<HttpResponseData> WriteJsonAsync(HttpRequestData req, HttpStatusCode statusCode, object body)
    {
        var response = req.CreateResponse(statusCode);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        response.Headers.Add("Cache-Control", "no-store");
        await response.WriteStringAsync(JsonSerializer.Serialize(body, JsonOptions));
        return response;
    }

    private sealed class ListModelsRequestBody
    {
        [JsonPropertyName("provider")]
        public string? Provider { get; set; }

        [JsonPropertyName("gitHubToken")]
        public string? GitHubToken { get; set; }

        [JsonPropertyName("foundryEndpoint")]
        public string? FoundryEndpoint { get; set; }

        [JsonPropertyName("foundryAuthMode")]
        public string? FoundryAuthMode { get; set; }

        [JsonPropertyName("foundryApiKey")]
        public string? FoundryApiKey { get; set; }
    }
}
