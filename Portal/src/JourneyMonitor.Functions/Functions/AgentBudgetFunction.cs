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
/// The per-agent spend limits behind Settings → Agent Budgets.
///
/// GET  /api/agent-budgets                  stored overrides plus the values actually in effect
/// PUT  /api/agent-budgets/{id}             replaces this agent's limits (whole row)
/// GET  /api/agent-budgets/{id}/history     who changed what, newest first
///
/// The GET returns BOTH the stored row and the effective value on purpose. A form that shows only
/// the override cannot tell "inherits 2" from "someone set it to 2", and those differ the moment
/// the configured default changes.
///
/// Writes are gated by X-Settings-Write-Secret: raising a debate roster costs money on every
/// investigation afterwards, which is at least as consequential as pausing an agent.
/// </summary>
public sealed class AgentBudgetFunction(
    IAgentBudgetStore budgetStore,
    IAgentBudgetResolver budgetResolver,
    IOptions<MonitoringOptions> monitoringOptions,
    ILogger<AgentBudgetFunction> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>The agents that call a model. An id outside this list is a 404, never a row nobody reads.</summary>
    private static readonly string[] KnownMonkeyKeys =
    [
        MonkeyKey.LatencyScoutMonkey,
        MonkeyKey.ExceptionScoutMonkey,
        MonkeyKey.InspectorMonkey,
        MonkeyKey.DoctorMonkey,
        MonkeyKey.RcaMonkey,
    ];

    [Function("GetAgentBudgets")]
    public async Task<HttpResponseData> GetAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "agent-budgets")] HttpRequestData req,
        CancellationToken ct)
    {
        try
        {
            var stored = await budgetResolver.GetAllAsync(ct);
            var inspector = await budgetResolver.ResolveInspectorAsync(ct);

            var agents = KnownMonkeyKeys.Select(key =>
            {
                stored.TryGetValue(key, out var row);
                return new
                {
                    id = key,
                    // Only the Inspector has dials wired up today. Saying so is better than
                    // rendering five identical forms where four of them do nothing.
                    tunable = string.Equals(key, MonkeyKey.InspectorMonkey, StringComparison.Ordinal),
                    overridden = row is not null && !row.IsEmpty,
                    updatedAt = row?.UpdatedAt.ToString("O"),
                    updatedBy = row?.UpdatedBy,
                    stored = row is null ? null : new
                    {
                        maxRunsPerDay = row.MaxRunsPerDay,
                        debateModels = row.DebateModels,
                        debateRounds = row.DebateRounds,
                        maxHelpHops = row.MaxHelpHops,
                        skipDebateWhenContributorsBelow = row.SkipDebateWhenContributorsBelow,
                    },
                };
            }).ToList();

            return await WriteJsonAsync(req, HttpStatusCode.OK, new
            {
                agents,
                effective = new
                {
                    id = MonkeyKey.InspectorMonkey,
                    maxRunsPerDay = inspector.MaxInvestigationsPerDay,
                    debateModels = string.Join(",", inspector.DebateModels),
                    debateRounds = inspector.DebateRounds,
                    maxHelpHops = inspector.MaxHelpHops,
                    skipDebateWhenContributorsBelow = inspector.SkipDebateWhenContributorsBelow,
                    // What one investigation costs at these settings, so the number moves while
                    // the form is edited instead of only after the next real run.
                    debateCallsPerInvestigation = inspector.DebateModels.Count * (1 + inspector.DebateRounds),
                },
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not read the agent budgets.");
            return await WriteJsonAsync(req, HttpStatusCode.InternalServerError, new { error = "Could not read the agent budgets." });
        }
    }

    [Function("UpsertAgentBudget")]
    public async Task<HttpResponseData> UpsertAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "agent-budgets/{id}")] HttpRequestData req,
        string id,
        CancellationToken ct)
    {
        if (!SettingsWriteSecretGuard.HasMatchingWriteSecret(req, monitoringOptions.Value.SettingsWriteSecret))
        {
            return await WriteJsonAsync(req, HttpStatusCode.Unauthorized, new { error = "Missing or invalid X-Settings-Write-Secret header. Monitoring:SettingsWriteSecret must be configured for this endpoint to accept writes." });
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
            return await WriteJsonAsync(req, HttpStatusCode.BadRequest, new { error = "Request body must be a JSON object." });
        }

        if (parsed is not JsonObject body)
        {
            return await WriteJsonAsync(req, HttpStatusCode.BadRequest, new { error = "Request body must be a JSON object." });
        }

        var debateModels = CleanRoster(body["debateModels"]?.GetValue<string>());
        var setting = new AgentBudgetSetting(
            monkeyKey,
            MaxRunsPerDay: ReadInt(body, "maxRunsPerDay"),
            DebateModels: debateModels,
            DebateRounds: ReadInt(body, "debateRounds"),
            MaxHelpHops: ReadInt(body, "maxHelpHops"),
            SkipDebateWhenContributorsBelow: ReadInt(body, "skipDebateWhenContributorsBelow"),
            UpdatedAt: DateTimeOffset.UtcNow,
            UpdatedBy: "");

        if (Validate(setting) is { } problem)
        {
            return await WriteJsonAsync(req, HttpStatusCode.BadRequest, new { error = problem });
        }

        var changedBy = body["changedBy"]?.GetValue<string>() is { Length: > 0 } who ? who : "portal";

        try
        {
            var saved = await budgetStore.UpsertAsync(setting, Truncate(changedBy, 128), ct);
            // Without this the runner in this very process keeps the old snapshot for up to the
            // cache TTL - long enough for the next tick to spend at the limit just lowered.
            budgetResolver.Invalidate();

            logger.LogInformation(
                "[AgentBudget] {MonkeyKey} updated by {ChangedBy}: rounds={Rounds} models={Models} cap={Cap} skipBelow={SkipBelow}",
                monkeyKey, changedBy, saved.DebateRounds, saved.DebateModels, saved.MaxRunsPerDay, saved.SkipDebateWhenContributorsBelow);

            return await WriteJsonAsync(req, HttpStatusCode.OK, new
            {
                id = saved.MonkeyKey,
                overridden = !saved.IsEmpty,
                updatedAt = saved.UpdatedAt.ToString("O"),
                updatedBy = saved.UpdatedBy,
            });
        }
        catch (Exception ex)
        {
            // A write that fails must not answer 200: the caller would believe the ceiling moved.
            logger.LogError(ex, "Could not save the budget for {MonkeyKey}.", monkeyKey);
            return await WriteJsonAsync(req, HttpStatusCode.InternalServerError, new { error = "Could not save the agent budget." });
        }
    }

    [Function("GetAgentBudgetHistory")]
    public async Task<HttpResponseData> GetHistoryAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "agent-budgets/{id}/history")] HttpRequestData req,
        string id,
        CancellationToken ct)
    {
        var monkeyKey = KnownMonkeyKeys.FirstOrDefault(k => string.Equals(k, id, StringComparison.OrdinalIgnoreCase));
        if (monkeyKey is null)
        {
            return await WriteJsonAsync(req, HttpStatusCode.NotFound, new { error = $"Unknown monkey id '{id}'." });
        }

        try
        {
            var history = await budgetStore.GetAuditAsync(monkeyKey, 50, ct);
            return await WriteJsonAsync(req, HttpStatusCode.OK, new
            {
                id = monkeyKey,
                history = history.Select(h => new
                {
                    changedAt = h.ChangedAt.ToString("O"),
                    changedBy = h.ChangedBy,
                    maxRunsPerDay = h.MaxRunsPerDay,
                    debateModels = h.DebateModels,
                    debateRounds = h.DebateRounds,
                    maxHelpHops = h.MaxHelpHops,
                    skipDebateWhenContributorsBelow = h.SkipDebateWhenContributorsBelow,
                }),
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not read the budget history for {MonkeyKey}.", monkeyKey);
            return await WriteJsonAsync(req, HttpStatusCode.InternalServerError, new { error = "Could not read the budget history." });
        }
    }

    /// <summary>
    /// Bounds every dial in code. These are spend limits reachable from a browser, and the only
    /// thing between a typo and a very expensive afternoon.
    /// </summary>
    private static string? Validate(AgentBudgetSetting setting)
    {
        if (setting.MaxRunsPerDay is < 1 or > 500) return "maxRunsPerDay must be between 1 and 500, or null to inherit.";
        if (setting.DebateRounds is < 0 or > 5) return "debateRounds must be between 0 and 5, or null to inherit.";
        if (setting.MaxHelpHops is < 0 or > 5) return "maxHelpHops must be between 0 and 5, or null to inherit.";
        if (setting.SkipDebateWhenContributorsBelow is < 0 or > 10) return "skipDebateWhenContributorsBelow must be between 0 and 10, or null to inherit.";

        if (setting.DebateModels is { Length: > 0 })
        {
            var count = setting.DebateModels.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
            if (count > 5) return "debateModels must list at most 5 models - every one of them is a completion per round.";
        }

        return null;
    }

    /// <summary>Null when nothing usable was sent, so the field reverts to the configured roster rather than silencing the debate.</summary>
    private static string? CleanRoster(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var models = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return models.Count == 0 ? null : string.Join(",", models);
    }

    /// <summary>Absent and explicit null both mean "inherit"; a non-numeric value is ignored rather than guessed at.</summary>
    private static int? ReadInt(JsonObject body, string name) =>
        body[name] is JsonValue value && value.TryGetValue<int>(out var parsed) ? parsed : null;

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    private static async Task<HttpResponseData> WriteJsonAsync(HttpRequestData req, HttpStatusCode status, object body)
    {
        var response = req.CreateResponse(status);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        await response.WriteStringAsync(JsonSerializer.Serialize(body, JsonOptions));
        return response;
    }
}
