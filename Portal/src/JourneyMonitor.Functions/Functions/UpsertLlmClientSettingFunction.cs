using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Core.UseCases;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Functions.Functions;

/// <summary>
/// PUT /api/settings/llm-client — lets any portal user configure the Doctor Monkey's GitHub
/// Copilot token/model from the Settings tab instead of an environment-specific Ai:GitHubToken
/// value (see <see cref="JourneyMonitor.Core.Monitoring.LlmClientSettingsResolver"/>). Same
/// write-secret gate as <see cref="UpsertLatencySpikeSettingFunction"/> (X-Settings-Write-Secret,
/// fails CLOSED) since this is, if anything, a more sensitive write - see
/// <see cref="SettingsWriteSecretGuard"/>.
/// </summary>
public sealed class UpsertLlmClientSettingFunction(
    LlmClientSettingsService settingsService,
    IOptions<MonitoringOptions> monitoringOptions,
    ILogger<UpsertLlmClientSettingFunction> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Function("UpsertLlmClientSetting")]
    public async Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "settings/llm-client")] HttpRequestData req,
        CancellationToken ct)
    {
        var requiredSecret = monitoringOptions.Value.SettingsWriteSecret;
        if (!SettingsWriteSecretGuard.HasMatchingWriteSecret(req, requiredSecret))
        {
            return await WriteJsonAsync(req, HttpStatusCode.Unauthorized, new { error = "Missing or invalid X-Settings-Write-Secret header. Monitoring:SettingsWriteSecret must be configured for this endpoint to accept writes." });
        }

        try
        {
            var request = await JsonSerializer.DeserializeAsync<UpsertRequestBody>(req.Body, JsonOptions, ct);
            if (request is null)
            {
                return await WriteJsonAsync(req, HttpStatusCode.BadRequest, new { error = "Request body must be valid JSON." });
            }

            var (statusCode, result) = await settingsService.UpsertAsync(
                new LlmClientSettingsInput
                {
                    GitHubToken = request.GitHubToken,
                    ClearToken = request.ClearToken,
                    Model = request.Model,
                    Provider = request.Provider,
                    DoctorProvider = request.DoctorProvider,
                    ScoutProvider = request.ScoutProvider,
                    InspectorProvider = request.InspectorProvider,
                    FoundryEndpoint = request.FoundryEndpoint,
                    FoundryAuthMode = request.FoundryAuthMode,
                    FoundryApiKey = request.FoundryApiKey,
                    ClearFoundryApiKey = request.ClearFoundryApiKey,
                    FoundryChatDeployment = request.FoundryChatDeployment,
                    FoundryEmbeddingDeployment = request.FoundryEmbeddingDeployment,
                },
                request.ChangedBy,
                ct);

            return await WriteJsonAsync(req, (HttpStatusCode)statusCode, result);
        }
        catch (JsonException)
        {
            return await WriteJsonAsync(req, HttpStatusCode.BadRequest, new { error = "Request body must be valid JSON." });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "UpsertLlmClientSetting failed");
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

    private sealed class UpsertRequestBody
    {
        /// <summary>Omit/null to leave the stored token unchanged; set <see cref="ClearToken"/> to remove it instead.</summary>
        [JsonPropertyName("gitHubToken")]
        public string? GitHubToken { get; set; }

        [JsonPropertyName("clearToken")]
        public bool ClearToken { get; set; }

        [JsonPropertyName("model")]
        public string? Model { get; set; }

        [JsonPropertyName("provider")]
        public string? Provider { get; set; }

        /// <summary>Empty string clears the override back to "inherit the global provider".</summary>
        [JsonPropertyName("doctorProvider")]
        public string? DoctorProvider { get; set; }

        [JsonPropertyName("scoutProvider")]
        public string? ScoutProvider { get; set; }

        [JsonPropertyName("inspectorProvider")]
        public string? InspectorProvider { get; set; }

        [JsonPropertyName("foundryEndpoint")]
        public string? FoundryEndpoint { get; set; }

        [JsonPropertyName("foundryAuthMode")]
        public string? FoundryAuthMode { get; set; }

        /// <summary>Omit/null to leave the stored key unchanged; set <see cref="ClearFoundryApiKey"/> to remove it instead.</summary>
        [JsonPropertyName("foundryApiKey")]
        public string? FoundryApiKey { get; set; }

        [JsonPropertyName("clearFoundryApiKey")]
        public bool ClearFoundryApiKey { get; set; }

        [JsonPropertyName("foundryChatDeployment")]
        public string? FoundryChatDeployment { get; set; }

        [JsonPropertyName("foundryEmbeddingDeployment")]
        public string? FoundryEmbeddingDeployment { get; set; }

        [JsonPropertyName("changedBy")]
        public string? ChangedBy { get; set; }
    }
}
