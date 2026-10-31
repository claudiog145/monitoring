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
/// POST /api/journeys/refresh - re-queries Application Insights and writes a new query run, then
/// the screen re-reads it from GET /api/journeys/stored. Behind the portal's Refresh button.
///
/// Environment and the time range come from the request rather than configuration because the
/// screen has selectors for both; the timer passes its configured defaults to the same use case.
/// Named "refresh" rather than "update" on purpose: there is no payload to write, it re-reads the
/// source of truth and appends a snapshot.
/// </summary>
public sealed class RefreshJourneysFunction(
    SyncJourneysService syncJourneysService,
    IOptions<JourneyStoreOptions> storeOptions,
    ILogger<RefreshJourneysFunction> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Function("RefreshJourneys")]
    public async Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "journeys/refresh")] HttpRequestData req,
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

        var defaults = syncJourneysService.ScopeFor(domain);
        var scope = defaults with
        {
            Window = query["win"] ?? defaults.Window,
            Environment = query["env"] ?? defaults.Environment,
            ClubId = query["club"] ?? defaults.ClubId,
            RowCap = query["top"] ?? query["limit"] ?? defaults.RowCap,
            Host = query["host"],
            PartnerId = query["partner"],
        };

        try
        {
            var pass = await syncJourneysService.SyncAsync(scope, cancellationToken);

            if (!pass.Stored)
            {
                // A rejected domain/window/club is the caller's fault; anything else upstream is not.
                var statusCode = pass.SourceStatusCode == 400
                    ? (int)HttpStatusCode.BadRequest
                    : (int)HttpStatusCode.BadGateway;

                logger.LogWarning("Refresh stored nothing for {Domain}: {SkipReason}", domain, pass.SkipReason);
                return await WriteJsonAsync(req, statusCode, new
                {
                    source = "error",
                    domain,
                    win = scope.Window,
                    environment = scope.Environment,
                    clubId = scope.ClubId,
                    error = pass.SkipReason,
                });
            }

            logger.LogInformation(
                "Refreshed {RecordCount} {Domain} journeys as query run {QueryRunId}.",
                pass.RecordCount, domain, pass.QueryRunId);

            return await WriteJsonAsync(req, (int)HttpStatusCode.OK, new
            {
                source = "stored",
                domain,
                win = scope.Window,
                environment = scope.Environment,
                clubId = scope.ClubId,
                queryRunId = pass.QueryRunId,
                rowCount = pass.RecordCount,
                inserted = pass.Inserted,
                updated = pass.Updated,
                unchanged = pass.Unchanged,
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "RefreshJourneys failed unexpectedly for domain {Domain}", domain);
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
