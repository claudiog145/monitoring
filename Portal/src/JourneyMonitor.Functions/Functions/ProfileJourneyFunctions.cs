using System.Net;
using System.Text.Json;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.UseCases;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Functions.Functions;

/// <summary>
/// Contract v6 §4.1: GET /api/profile-journeys, GET /api/profile-journeys/{journeyId} and the
/// ProfileJourneySync timer. Thin: the query string is validated here, everything else lives in
/// ProfileJourneyReader / ProfileJourneySyncService (Core). Without a configured journey store the
/// services are not registered and the endpoints answer 503.
/// </summary>
public sealed class ProfileJourneyFunctions(
    ILogger<ProfileJourneyFunctions> logger,
    ProfileJourneyReader? reader = null,
    ProfileJourneySyncService? sync = null,
    IOptions<ProfileJourneyOptions>? options = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string NotConfigured = "Per-company journeys are not configured (JourneyStore:ConnectionString is not set).";

    [Function("GetProfileJourneys")]
    public async Task<HttpResponseData> ListAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "profile-journeys")] HttpRequestData req,
        CancellationToken cancellationToken)
    {
        if (reader is null) return await WriteJsonAsync(req, HttpStatusCode.ServiceUnavailable, new { error = NotConfigured });

        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        var errors = new[]
        {
            ProfileJourneyRequestParser.ParseWindow(query["win"], ProfileJourneyRequestParser.DefaultJourneyWindow, out var window),
            ProfileJourneyRequestParser.ParseTop(query["top"], ProfileJourneyRequestParser.DefaultJourneyTop, ProfileJourneyRequestParser.MaxJourneyTop, out var top),
            ProfileJourneyRequestParser.ParseKey(query["service"], "service", out var service),
            ProfileJourneyRequestParser.ParseKey(query["entity"], "entity", out var entity),
            ProfileJourneyRequestParser.ParseKey(query["status"], "status", out var status),
        };
        var error = errors.FirstOrDefault(e => e is not null);
        if (error is not null) return await WriteJsonAsync(req, HttpStatusCode.BadRequest, new { error });

        try
        {
            var scope = await reader.ResolveProfileAsync(query["profileKey"], cancellationToken);
            if (!scope.IsValid) return await WriteJsonAsync(req, HttpStatusCode.NotFound, new { error = scope.Error });

            var result = await reader.ListAsync(
                new ProfileJourneyQuery(scope.ProfileKey!, service, entity, status, window, top), cancellationToken);
            return await WriteJsonAsync(req, HttpStatusCode.OK, result);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "GetProfileJourneys failed.");
            return await WriteJsonAsync(req, HttpStatusCode.InternalServerError, new { error = "Unexpected server error." });
        }
    }

    [Function("GetProfileJourney")]
    public async Task<HttpResponseData> GetAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "profile-journeys/{journeyId}")] HttpRequestData req,
        string journeyId,
        CancellationToken cancellationToken)
    {
        if (reader is null) return await WriteJsonAsync(req, HttpStatusCode.ServiceUnavailable, new { error = NotConfigured });

        var error = ProfileJourneyRequestParser.ParseKey(journeyId, "journeyId", out var id);
        if (error is not null || id is null) return await WriteJsonAsync(req, HttpStatusCode.BadRequest, new { error = error ?? "journeyId is required." });

        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        try
        {
            var scope = await reader.ResolveProfileAsync(query["profileKey"], cancellationToken);
            if (!scope.IsValid) return await WriteJsonAsync(req, HttpStatusCode.NotFound, new { error = scope.Error });

            var detail = await reader.GetAsync(scope.ProfileKey!, id, cancellationToken);
            return detail is null
                ? await WriteJsonAsync(req, HttpStatusCode.NotFound, new { error = $"Journey '{id}' was not found for profile '{scope.ProfileKey}'." })
                : await WriteJsonAsync(req, HttpStatusCode.OK, detail);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "GetProfileJourney failed.");
            return await WriteJsonAsync(req, HttpStatusCode.InternalServerError, new { error = "Unexpected server error." });
        }
    }

    [Function("ProfileJourneySync")]
    public async Task SyncAsync(
        [TimerTrigger("%platform_reliability_cron_setting%")] TimerInfo timer,
        CancellationToken cancellationToken)
    {
        if (sync is null)
        {
            logger.LogInformation("[ProfileJourneySync] skipped: {Reason}", NotConfigured);
            return;
        }
        if (options is not null && !options.Value.SyncEnabled)
        {
            logger.LogInformation("[ProfileJourneySync] skipped: ProfileJourneys:SyncEnabled is false.");
            return;
        }

        var result = await sync.SyncAllAsync(cancellationToken);
        logger.LogInformation("[ProfileJourneySync] {Profiles} profiles, {Journeys} journeys written, {Purged} purged.",
            result.Profiles.Count, result.Profiles.Sum(p => p.Journeys), result.Purged);
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
