using System.Net;
using System.Text.Json;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Core.UseCases;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Functions.Functions;

/// <summary>
/// GET /api/journeys/stored - serves the last snapshot the ingestion timer wrote, in the same
/// envelope and raw row shape as GET /api/journeys, so the screen and the Refresh button both
/// call this single endpoint. There is no cache on this path, so a refresh is the same request.
/// </summary>
public sealed class GetStoredJourneysFunction(
    GetStoredJourneysService getStoredJourneysService,
    IOptions<JourneyStoreOptions> storeOptions,
    ILogger<GetStoredJourneysFunction> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Function("GetStoredJourneys")]
    public async Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "journeys/stored")] HttpRequestData req,
        CancellationToken cancellationToken)
    {
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        var domain = query["domain"] ?? "hotels";

        if (!storeOptions.Value.IsConfigured)
        {
            return await WriteJsonAsync(req, (int)HttpStatusCode.ServiceUnavailable, new
            {
                source = "error",
                domain,
                error = $"{JourneyStoreOptions.SectionName}:ConnectionString is not configured.",
            });
        }

        try
        {
            var outcome = await getStoredJourneysService.GetAsync(domain, cancellationToken);
            return await WriteJsonAsync(req, outcome.StatusCode, outcome.Result);
        }
        catch (ArgumentOutOfRangeException)
        {
            return await WriteJsonAsync(req, (int)HttpStatusCode.BadRequest, new
            {
                source = "error",
                domain,
                error = "domain must be hotels, cars, cruises or air",
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GetStoredJourneys failed unexpectedly for domain {Domain}", domain);
            return await WriteJsonAsync(req, (int)HttpStatusCode.InternalServerError, new { source = "error", error = "Unexpected server error." });
        }
    }

    private static async Task<HttpResponseData> WriteJsonAsync(HttpRequestData req, int statusCode, object payload)
    {
        var response = req.CreateResponse((HttpStatusCode)statusCode);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        response.Headers.Add("Cache-Control", "no-store");
        await response.WriteStringAsync(JsonSerializer.Serialize(payload, JsonOptions));
        return response;
    }
}
