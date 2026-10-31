using System.Net;
using System.Text.Json;
using JourneyMonitor.Core.UseCases;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace JourneyMonitor.Functions.Functions;

/// <summary>GET /api/exceptions — incidents stored by exception ingestion.</summary>
public sealed class GetIncidentsFunction(
    GetIncidentsService getIncidentsService,
    ILogger<GetIncidentsFunction> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Function("GetIncidents")]
    public async Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "exceptions")] HttpRequestData req,
        CancellationToken cancellationToken)
    {
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        var request = new IncidentListRequest(
            query["domain"],
            query["club"],
            query["env"] ?? query["environment"],
            query["win"],
            query["id"]);

        try
        {
            var outcome = await getIncidentsService.GetAsync(request, cancellationToken);
            return await WriteJsonAsync(req, outcome.StatusCode, outcome.Result);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GetIncidents failed unexpectedly");
            return await WriteJsonAsync(
                req,
                (int)HttpStatusCode.InternalServerError,
                new { source = "error", error = "Unexpected server error." });
        }
    }

    private static async Task<HttpResponseData> WriteJsonAsync(
        HttpRequestData req,
        int statusCode,
        object payload)
    {
        var response = req.CreateResponse((HttpStatusCode)statusCode);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        response.Headers.Add("Cache-Control", "no-store");
        await response.WriteStringAsync(JsonSerializer.Serialize(payload, JsonOptions));
        return response;
    }
}
