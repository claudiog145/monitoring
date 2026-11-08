using System.Net;
using System.Text.Json;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.UseCases;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace JourneyMonitor.Functions.Functions;

/// <summary>
/// Contract v6 §4.2: GET /api/entities?profileKey=&amp;service=&amp;win=&amp;top= - top entities of one
/// company from its own telemetry. Thin: validates the query string and dispatches to EntityCatalogService.
/// </summary>
public sealed class GetEntitiesFunction(
    ILogger<GetEntitiesFunction> logger,
    EntityCatalogService? catalog = null,
    ProfileJourneyReader? reader = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Function("GetEntities")]
    public async Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "entities")] HttpRequestData req,
        CancellationToken cancellationToken)
    {
        if (catalog is null || reader is null)
        {
            return await WriteJsonAsync(req, HttpStatusCode.ServiceUnavailable,
                new { error = "The entity catalog is not configured (JourneyStore:ConnectionString is not set)." });
        }

        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        var errors = new[]
        {
            ProfileJourneyRequestParser.ParseWindow(query["win"], ProfileJourneyRequestParser.DefaultEntityWindow, out var window),
            ProfileJourneyRequestParser.ParseTop(query["top"], ProfileJourneyRequestParser.DefaultEntityTop, ProfileJourneyRequestParser.MaxEntityTop, out var top),
            ProfileJourneyRequestParser.ParseKey(query["service"], "service", out var service),
        };
        var error = errors.FirstOrDefault(e => e is not null);
        if (error is not null) return await WriteJsonAsync(req, HttpStatusCode.BadRequest, new { error });

        try
        {
            var scope = await reader.ResolveProfileAsync(query["profileKey"], cancellationToken);
            if (!scope.IsValid) return await WriteJsonAsync(req, HttpStatusCode.NotFound, new { error = scope.Error });

            var result = await catalog.GetTopEntitiesAsync(new EntityQuery(scope.ProfileKey!, service, window, top), cancellationToken);
            return await WriteJsonAsync(req, HttpStatusCode.OK, result);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "GetEntities failed.");
            return await WriteJsonAsync(req, HttpStatusCode.InternalServerError, new { error = "Unexpected server error." });
        }
    }

    private static async Task<HttpResponseData> WriteJsonAsync(HttpRequestData req, HttpStatusCode statusCode, object payload)
    {
        var response = req.CreateResponse(statusCode);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        response.Headers.Add("Cache-Control", "no-store");
        await response.WriteStringAsync(JsonSerializer.Serialize(payload, payload.GetType(), JsonOptions));
        return response;
    }
}
