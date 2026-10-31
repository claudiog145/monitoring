using System.Net;
using System.Text.Json;
using JourneyMonitor.Core.UseCases;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace JourneyMonitor.Functions.Functions;

/// <summary>
/// GET /api/settings/llm-client/audit — change history for the Settings tab, newest first. Never
/// includes the token value, past or present (see monitoring.LlmClientSettingAudit).
/// </summary>
public sealed class GetLlmClientSettingsAuditFunction(
    LlmClientSettingsService settingsService,
    ILogger<GetLlmClientSettingsAuditFunction> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Function("GetLlmClientSettingsAudit")]
    public async Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "settings/llm-client/audit")] HttpRequestData req,
        CancellationToken ct)
    {
        try
        {
            var audit = await settingsService.GetAuditAsync(ct);
            return await WriteJsonAsync(req, HttpStatusCode.OK, audit);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GetLlmClientSettingsAudit failed");
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
