using System.Net;
using System.Text.Json;
using JourneyMonitor.Core.UseCases;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace JourneyMonitor.Functions.Functions;

/// <summary>
/// GET /api/settings/llm-client — the Settings tab's "LLM Client" section: whether a token is
/// configured, a masked hint of it, and the model in use. Never returns the real token (see
/// <see cref="LlmClientSettingsService"/>).
/// </summary>
public sealed class GetLlmClientSettingsFunction(
    LlmClientSettingsService settingsService,
    ILogger<GetLlmClientSettingsFunction> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Function("GetLlmClientSettings")]
    public async Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "settings/llm-client")] HttpRequestData req,
        CancellationToken ct)
    {
        try
        {
            var settings = await settingsService.GetAsync(ct);
            return await WriteJsonAsync(req, HttpStatusCode.OK, settings);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GetLlmClientSettings failed");
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
}
