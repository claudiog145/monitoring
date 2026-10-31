using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Options;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Functions.Functions;

/// <summary>
/// POST /api/inspector/queue/discard — human cleanup of the Inspector's backlog.
///
/// Body: <c>{ "ids": ["guid", ...], "reason": "...", "discardedBy": "..." }</c>. Omitting
/// <c>ids</c> clears everything currently waiting; passing ids discards exactly those, which is how
/// the portal implements both "discard this one" and "discard this whole signature" without a
/// second endpoint.
///
/// Nothing is deleted — the rows become <see cref="AgentWorkItemStatus.Discarded"/>, carrying who
/// asked and why. Gated by X-Internal-Api-Secret for the same reason as the pause switch: dropping
/// work the Scout decided was worth investigating is at least as consequential as stopping an agent.
/// </summary>
public sealed class DiscardInspectorQueueFunction(
    IAgentWorkQueue queue,
    IOptions<MonitoringOptions> monitoringOptions,
    ILogger<DiscardInspectorQueueFunction> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Bounds one request so a malformed or hostile body cannot turn into an unbounded IN list.</summary>
    private const int MaxIdsPerRequest = 500;

    [Function("DiscardInspectorQueue")]
    public async Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "inspector/queue/discard")] HttpRequestData req,
        CancellationToken ct)
    {
        var requiredSecret = monitoringOptions.Value.InternalApiSecret;
        if (!SettingsWriteSecretGuard.HasMatchingSecret(req, requiredSecret, SettingsWriteSecretGuard.InternalApiSecretHeaderName))
        {
            return await WriteJsonAsync(req, HttpStatusCode.Unauthorized, new
            {
                error = "Missing or invalid X-Internal-Api-Secret header. Monitoring:InternalApiSecret must be configured for this endpoint to accept requests.",
            });
        }

        JsonNode? parsed;
        try
        {
            using var reader = new StreamReader(req.Body);
            var raw = await reader.ReadToEndAsync(ct);
            parsed = string.IsNullOrWhiteSpace(raw) ? new JsonObject() : JsonNode.Parse(raw);
        }
        catch (JsonException)
        {
            return await WriteJsonAsync(req, HttpStatusCode.BadRequest, new { error = "Request body must be a JSON object." });
        }

        if (parsed is not JsonObject body)
        {
            return await WriteJsonAsync(req, HttpStatusCode.BadRequest, new { error = "Request body must be a JSON object." });
        }

        List<Guid>? ids = null;
        if (body["ids"] is JsonArray rawIds)
        {
            if (rawIds.Count > MaxIdsPerRequest)
            {
                return await WriteJsonAsync(req, HttpStatusCode.BadRequest, new { error = $"At most {MaxIdsPerRequest} ids can be discarded in one request." });
            }

            ids = [];
            foreach (var node in rawIds)
            {
                // A value that is not a GUID is rejected rather than skipped: silently dropping one
                // id from a batch would report a smaller "discarded" count than the caller selected
                // and leave them guessing which item survived.
                if (node is null || !Guid.TryParse(node.GetValue<string>(), out var id))
                {
                    return await WriteJsonAsync(req, HttpStatusCode.BadRequest, new { error = "Every entry of \"ids\" must be a GUID." });
                }
                ids.Add(id);
            }

            if (ids.Count == 0)
            {
                // An empty array is a selection of nothing. Treating it as "no filter" would clear
                // the whole queue from a request that asked for the opposite.
                return await WriteJsonAsync(req, HttpStatusCode.OK, new { discarded = 0, scope = "none" });
            }
        }

        var reason = Truncate(body["reason"]?.GetValue<string>() is { Length: > 0 } r ? r : "No reason given.", 512);
        var discardedBy = Truncate(body["discardedBy"]?.GetValue<string>() is { Length: > 0 } who ? who : "portal", 128);

        try
        {
            var discarded = await queue.DiscardQueuedAsync(
                AgentWorkPayloadType.InvestigationPackage, ids, reason, discardedBy, ct);

            logger.LogWarning(
                "[InspectorQueue] {Discarded} item(s) discarded by {DiscardedBy} ({Scope}). Reason: {Reason}",
                discarded, discardedBy, ids is null ? "all waiting" : $"{ids.Count} selected", reason);

            return await WriteJsonAsync(req, HttpStatusCode.OK, new
            {
                discarded,
                scope = ids is null ? "all" : "selected",
            });
        }
        catch (Exception ex)
        {
            // A failed write must not answer 200: the portal would remove the rows from its own
            // view while the Inspector keeps investigating every one of them.
            logger.LogError(ex, "DiscardInspectorQueue failed.");
            return await WriteJsonAsync(req, HttpStatusCode.InternalServerError, new { error = "The queue could not be updated." });
        }
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    private static async Task<HttpResponseData> WriteJsonAsync(HttpRequestData req, HttpStatusCode statusCode, object body)
    {
        var response = req.CreateResponse(statusCode);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        response.Headers.Add("Cache-Control", "no-store");
        await response.WriteStringAsync(JsonSerializer.Serialize(body, JsonOptions));
        return response;
    }
}
