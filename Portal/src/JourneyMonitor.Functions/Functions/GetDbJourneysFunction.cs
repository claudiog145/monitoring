using System.Net;
using System.Text.Json;
using JourneyMonitor.Core.UseCases;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace JourneyMonitor.Functions.Functions;

/// <summary>GET /api/journeys/db - reads filtered rows from the latest SQL snapshot.</summary>
public sealed class GetDbJourneysFunction(
    GetDbJourneysService getDbJourneysService,
    ILogger<GetDbJourneysFunction> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Function("GetDbJourneys")]
    public async Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "journeys/db")] HttpRequestData req,
        CancellationToken cancellationToken)
    {
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        var request = new DbJourneysRequest(
            query["domain"] ?? "hotels",
            query["club"],
            query["status"],
            query["id"] ?? query["corr"],
            query["top"] ?? query["limit"],
            query["win"]);

        try
        {
            var outcome = await getDbJourneysService.GetAsync(request, cancellationToken);
            return await WriteJsonAsync(req, outcome.StatusCode, outcome.Result);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GetDbJourneys failed unexpectedly for domain {Domain}", request.Domain);
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
