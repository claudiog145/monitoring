using System.Net;
using System.Text.Json;
using JourneyMonitor.Core.UseCases;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace JourneyMonitor.Functions.Functions;

/// <summary>GET /api/settings/slo/audit — change history for monitoring.SloDefinition.</summary>
public sealed class GetSloDefinitionsAuditFunction(SloDefinitionsService settingsService, ILogger<GetSloDefinitionsAuditFunction> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Function("GetSloDefinitionsAudit")]
    public async Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "settings/slo/audit")] HttpRequestData req,
        CancellationToken ct)
    {
        try
        {
            var audit = await settingsService.GetAuditAsync(ct);
            return await WriteJsonAsync(req, HttpStatusCode.OK, audit);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GetSloDefinitionsAudit failed");
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
