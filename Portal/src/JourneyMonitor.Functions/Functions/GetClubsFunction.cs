using System.Net;
using System.Text.Json;
using JourneyMonitor.Core.UseCases;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace JourneyMonitor.Functions.Functions;

/// <summary>
/// GET /api/clubs - thin HTTP trigger over GetClubsService (Core). Direct port of the routing
/// half of handleClubsApi in server.js.
/// </summary>
public sealed class GetClubsFunction(GetClubsService getClubsService, ILogger<GetClubsFunction> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Function("GetClubs")]
    public async Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "clubs")] HttpRequestData req,
        CancellationToken cancellationToken)
    {
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        var domain = query["domain"] ?? "hotels";

        try
        {
            var result = await getClubsService.GetAsync(
                domain,
                query["win"],
                query["env"],
                query["fresh"] == "1",
                query["mode"],
                cancellationToken);
            return await WriteJsonAsync(req, HttpStatusCode.OK, result);
        }
        catch (Core.Configuration.InsightsConfigurationException ex)
        {
            return await WriteJsonAsync(req, HttpStatusCode.BadRequest, new { source = "error", error = ex.Message });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GetClubs failed unexpectedly for domain {Domain}", domain);
            return await WriteJsonAsync(req, HttpStatusCode.InternalServerError, new { source = "error", error = "Unexpected server error." });
        }
    }

    private static async Task<HttpResponseData> WriteJsonAsync(HttpRequestData req, HttpStatusCode statusCode, object payload)
    {
        var response = req.CreateResponse(statusCode);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        response.Headers.Add("Cache-Control", "no-store");
        await response.WriteStringAsync(JsonSerializer.Serialize(payload, JsonOptions));
        return response;
    }
}
