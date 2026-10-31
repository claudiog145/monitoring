using System.Net;
using System.Text.Json;
using JourneyMonitor.Core.UseCases;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace JourneyMonitor.Functions.Functions;

/// <summary>GET /api/settings/latency/audit?domain=... — change history for the Settings tab, newest first.</summary>
public sealed class GetLatencySpikeSettingsAuditFunction(
    LatencySpikeSettingsService settingsService,
    ILogger<GetLatencySpikeSettingsAuditFunction> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Function("GetLatencySpikeSettingsAudit")]
    public async Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "settings/latency/audit")] HttpRequestData req,
        CancellationToken ct)
    {
        try
        {
            var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
            var audit = await settingsService.GetAuditAsync(query["domain"], ct);
            var response = req.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "application/json; charset=utf-8");
            response.Headers.Add("Cache-Control", "no-store");
            await response.WriteStringAsync(JsonSerializer.Serialize(audit, JsonOptions));
            return response;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GetLatencySpikeSettingsAudit failed");
            var response = req.CreateResponse(HttpStatusCode.InternalServerError);
            response.Headers.Add("Content-Type", "application/json; charset=utf-8");
            await response.WriteStringAsync(JsonSerializer.Serialize(new { error = "Unexpected server error." }, JsonOptions));
            return response;
        }
    }
}
