using System.Net;
using System.Text.Json;
using JourneyMonitor.Core.UseCases;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace JourneyMonitor.Functions.Functions;

/// <summary>
/// GET /api/club-inventory — thin HTTP trigger over <see cref="GetClubInventoryService"/>.
/// </summary>
public sealed class GetClubInventoryFunction(
    GetClubInventoryService getClubInventoryService,
    ILogger<GetClubInventoryFunction> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Function("GetClubInventory")]
    public async Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "club-inventory")] HttpRequestData req,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await getClubInventoryService.GetAsync(cancellationToken);
            return await WriteJsonAsync(req, HttpStatusCode.OK, result);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GetClubInventory failed unexpectedly.");
            return await WriteJsonAsync(req, HttpStatusCode.InternalServerError, new
            {
                source = "error",
                warning = "Unexpected server error.",
                clubs = Array.Empty<object>(),
            });
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
