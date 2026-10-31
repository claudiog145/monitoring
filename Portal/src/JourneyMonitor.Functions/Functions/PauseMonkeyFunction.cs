using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring;
using JourneyMonitor.Core.Options;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Functions.Functions;

/// <summary>
/// POST /api/monkeys/{id}/pause — the real, durable pause switch behind the Simian Army card's
/// "Pause Agent" button. That button used to flip a field in the browser's own state object: the
/// toast said "Agent paused", the timer trigger kept running and /api/monkeys kept reporting
/// "active". This writes <c>monitoring.AgentPauseSetting</c>, which every runner checks before it
/// does any work, so a pause survives the tab, the restart and the next tick.
///
/// Gated by X-Internal-Api-Secret, same fail-closed guard as RunMonkeyNowFunction - stopping an
/// agent is at least as consequential as starting one.
/// </summary>
public sealed class PauseMonkeyFunction(
    IAgentPauseStore pauseStore,
    IAgentPauseResolver pauseResolver,
    IOptions<MonitoringOptions> monitoringOptions,
    ILogger<PauseMonkeyFunction> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>The roster this endpoint accepts. An id outside it is a 404, never a row nobody reads.</summary>
    private static readonly string[] KnownMonkeyKeys =
    [
        MonkeyKey.LatencyScoutMonkey,
        MonkeyKey.ExceptionScoutMonkey,
        MonkeyKey.InspectorMonkey,
        MonkeyKey.RcaMonkey,
        MonkeyKey.LatencySpikeMonkey,
        MonkeyKey.SloBurnRateMonkey,
        MonkeyKey.DoctorMonkey,
    ];

    [Function("PauseMonkey")]
    public async Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "monkeys/{id}/pause")] HttpRequestData req,
        string id,
        CancellationToken ct)
    {
        var requiredSecret = monitoringOptions.Value.InternalApiSecret;
        if (!SettingsWriteSecretGuard.HasMatchingSecret(req, requiredSecret, SettingsWriteSecretGuard.InternalApiSecretHeaderName))
        {
            return await WriteJsonAsync(req, HttpStatusCode.Unauthorized, new { error = "Missing or invalid X-Internal-Api-Secret header. Monitoring:InternalApiSecret must be configured for this endpoint to accept requests." });
        }

        var monkeyKey = KnownMonkeyKeys.FirstOrDefault(k => string.Equals(k, id, StringComparison.OrdinalIgnoreCase));
        if (monkeyKey is null)
        {
            return await WriteJsonAsync(req, HttpStatusCode.NotFound, new { error = $"Unknown monkey id '{id}'." });
        }

        JsonNode? parsed;
        try
        {
            using var reader = new StreamReader(req.Body);
            parsed = JsonNode.Parse(await reader.ReadToEndAsync(ct));
        }
        catch (JsonException)
        {
            return await WriteJsonAsync(req, HttpStatusCode.BadRequest, new { error = "Request body must be a JSON object with a boolean \"paused\" property." });
        }

        if (parsed is not JsonObject body || body["paused"] is not JsonValue pausedValue || !pausedValue.TryGetValue<bool>(out var paused))
        {
            // No default: "pause" and "resume" are opposite actions and guessing one of them from
            // a malformed body is exactly the kind of silent wrong answer this feature removes.
            return await WriteJsonAsync(req, HttpStatusCode.BadRequest, new { error = "Request body must be a JSON object with a boolean \"paused\" property." });
        }

        var changedBy = body["changedBy"]?.GetValue<string>() is { Length: > 0 } who ? who : "portal";

        try
        {
            var setting = await pauseStore.UpsertAsync(monkeyKey, paused, Truncate(changedBy, 128), ct);
            // Without this the runner in this very process could keep using a snapshot that is up
            // to the cache TTL old - long enough for a "Run Now" right after a pause to still run.
            pauseResolver.Invalidate();

            logger.LogInformation("[PauseMonkey] {MonkeyKey} paused={Paused} by {ChangedBy}", monkeyKey, paused, changedBy);

            return await WriteJsonAsync(req, HttpStatusCode.OK, new
            {
                id = setting.MonkeyKey,
                paused = setting.IsPaused,
                updatedAt = setting.UpdatedAt.ToString("O"),
            });
        }
        catch (Exception ex)
        {
            // A write that fails must not answer 200: the caller would believe the agent is
            // stopped while it keeps running, which is the original bug in a new costume.
            logger.LogError(ex, "PauseMonkey failed for monkey {Id}", monkeyKey);
            return await WriteJsonAsync(req, HttpStatusCode.InternalServerError, new { error = "The pause state could not be saved." });
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
