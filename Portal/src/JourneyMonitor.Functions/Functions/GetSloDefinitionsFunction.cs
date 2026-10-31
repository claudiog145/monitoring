using System.Net;
using System.Text.Json;
using JourneyMonitor.Core.UseCases;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace JourneyMonitor.Functions.Functions;

/// <summary>GET /api/settings/slo — every configured SLO definition (Settings tab "SLO Definitions" section), see docs/product/wi-238036-threshold-research-and-wi-package-2026-09-29.md.</summary>
public sealed class GetSloDefinitionsFunction(SloDefinitionsService settingsService, ILogger<GetSloDefinitionsFunction> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Function("GetSloDefinitions")]
    public async Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "settings/slo")] HttpRequestData req,
        CancellationToken ct)
    {
        try
        {
            var definitions = await settingsService.GetAllAsync(ct);
            return await WriteJsonAsync(req, HttpStatusCode.OK, definitions);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GetSloDefinitions failed");
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
