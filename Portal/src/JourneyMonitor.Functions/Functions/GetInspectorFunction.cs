using System.Globalization;
using System.Net;
using System.Text.Json;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Options;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Functions.Functions;

/// <summary>
/// Read side of the Inspector Monkey's L3 analysis phase.
///
/// GET /api/inspector/cases       - case summaries, newest first.
/// GET /api/inspector/cases/{id}  - the full case, including the verbatim agent transcript.
/// GET /api/inspector/queue       - the hand-off backlog plus the cost limits it runs under.
/// GET /api/inspector/toasts      - in-portal notifications the polling client has not seen yet.
///
/// Only the detail is gated by X-Internal-Api-Secret: the transcript carries raw prompts (which
/// embed the package's evidence, so member and correlation ids with it), exactly the reason
/// GET /api/scout/packages/{id} is gated and its list is not. The summaries, the queue and the
/// toasts stay anonymous like /api/findings.
/// </summary>
public sealed class GetInspectorFunction(
    IForensicCaseRepository cases,
    IAgentTranscriptRepository transcripts,
    IAgentWorkQueue queue,
    INotificationLogRepository notificationLog,
    IOptions<InspectorOptions> inspectorOptions,
    IOptions<MonitoringOptions> monitoringOptions,
    ILogger<GetInspectorFunction> logger)
{
    private const int DefaultTop = 25;
    private const int MaxTop = 200;

    /// <summary>
    /// Replay window for a poll that passes no <c>since</c>. Small on purpose: zero would drop a
    /// toast raised while the page was loading, and "everything" would replay the whole history on
    /// every cold start of the UI.
    /// </summary>
    private static readonly TimeSpan DefaultToastLookback = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Function("GetInspectorCases")]
    public async Task<HttpResponseData> GetCasesAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "inspector/cases")] HttpRequestData req,
        CancellationToken ct)
    {
        try
        {
            var items = await cases.GetRecentAsync(ReadTop(req), ReadQuery(req, "domain"), ReadQuery(req, "club"), ReadQuery(req, "profileKey"), ct);
            return await WriteJsonAsync(req, HttpStatusCode.OK, new
            {
                cases = items.Select(ToSummary).ToList(),
                // Echoed so a short list reads as "scoped" rather than as "the agent did nothing".
                appliedFilter = new { domain = ReadQuery(req, "domain"), club = ReadQuery(req, "club"), profileKey = ReadQuery(req, "profileKey") },
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GetInspectorCases failed.");
            return await WriteJsonAsync(req, HttpStatusCode.InternalServerError, new { error = "Unexpected server error." });
        }
    }

    [Function("GetInspectorCaseById")]
    public async Task<HttpResponseData> GetCaseByIdAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "inspector/cases/{id}")] HttpRequestData req,
        string id,
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

        if (!Guid.TryParse(id, out var caseId))
        {
            return await WriteJsonAsync(req, HttpStatusCode.BadRequest, new { error = "id must be a GUID." });
        }

        try
        {
            var forensicCase = await cases.GetByIdAsync(caseId, ct);
            if (forensicCase is null)
            {
                return await WriteJsonAsync(req, HttpStatusCode.NotFound, new { error = $"No forensic case with id '{id}'." });
            }

            var summary = ToSummary(forensicCase);
            var turns = await transcripts.GetTurnsAsync(forensicCase.Id, ct);
            var toolCalls = await transcripts.GetToolCallsAsync(forensicCase.Id, ct);

            return await WriteJsonAsync(req, HttpStatusCode.OK, new
            {
                summary.id,
                summary.caseId,
                summary.entityKey,
                summary.brandName,
                summary.clubName,
                summary.vertical,
                summary.stage,
                summary.status,
                summary.decision,
                summary.verdict,
                summary.probableRootCause,
                summary.confidence,
                summary.blockedBy,
                summary.alertLevel,
                summary.alertReason,
                summary.ownerTeam,
                summary.ownerPrimary,
                summary.ownerBackup,
                summary.ownerResolvedBy,
                summary.clubId,
                summary.icePartnerId,
                summary.clubResolvedBy,
                summary.citationsTotal,
                summary.citationsVerified,
                summary.llmCalls,
                summary.tokensIn,
                summary.tokensOut,
                summary.durationMs,
                summary.startedAt,
                summary.completedAt,
                summary.phase,
                summary.errorMessage,
                // The four stored-as-text payloads, parsed so the client gets structured JSON
                // instead of JSON-in-a-string. Null when unparseable - a corrupt payload must not
                // cost the reader the rest of the case.
                blackboard = GetMonkeysFunction.ParseJsonOrNull(forensicCase.BlackboardJson),
                grounding = GetMonkeysFunction.ParseJsonOrNull(forensicCase.GroundingJson),
                synthesis = GetMonkeysFunction.ParseJsonOrNull(forensicCase.SynthesisJson),
                runbook = GetMonkeysFunction.ParseJsonOrNull(forensicCase.RunbookJson),
                // The audit trail: who said what, with the exact prompt and the raw response. This
                // is the half that makes the endpoint secret-gated.
                turns = turns.Select(t => new
                {
                    id = t.Id,
                    agentRole = t.AgentRole,
                    kind = t.Kind,
                    round = t.Round,
                    hop = t.Hop,
                    modelUsed = t.ModelUsed,
                    inputContext = t.InputContext,
                    promptText = t.PromptText,
                    rawResponseText = t.RawResponseText,
                    outcomeStatus = t.OutcomeStatus,
                    outcome = GetMonkeysFunction.ParseJsonOrNull(t.OutcomeJson),
                    tokensIn = t.TokensIn,
                    tokensOut = t.TokensOut,
                    durationMs = t.DurationMs,
                    errorMessage = t.ErrorMessage,
                    createdAt = t.CreatedAt.ToString("O"),
                }).ToList(),
                toolCalls = toolCalls.Select(c => new
                {
                    id = c.Id,
                    agentTurnId = c.AgentTurnId,
                    toolName = c.ToolName,
                    arguments = GetMonkeysFunction.ParseJsonOrNull(c.ArgumentsJson),
                    result = GetMonkeysFunction.ParseJsonOrNull(c.ResultJson),
                    // A truncated result must never be readable as a complete one.
                    truncated = c.Truncated,
                    success = c.Success,
                    errorMessage = c.ErrorMessage,
                    resultRowCount = c.ResultRowCount,
                    durationMs = c.DurationMs,
                }).ToList(),
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GetInspectorCaseById failed for {Id}", id);
            return await WriteJsonAsync(req, HttpStatusCode.InternalServerError, new { error = "Unexpected server error." });
        }
    }

    /// <summary>
    /// The backlog the card cannot infer from past cases: a heartbeat shows what was investigated,
    /// this shows what is waiting and under which cost ceiling it is being drained.
    /// </summary>
    [Function("GetInspectorQueue")]
    public async Task<HttpResponseData> GetQueueAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "inspector/queue")] HttpRequestData req,
        CancellationToken ct)
    {
        try
        {
            var limits = inspectorOptions.Value;
            var queued = await queue.GetQueuedAsync(AgentWorkPayloadType.InvestigationPackage, ReadTop(req), ct);
            var queueStats = await queue.GetStatsAsync(AgentWorkPayloadType.InvestigationPackage, ct);
            // Deliberately unscoped: these counters are read against the daily investigation cap,
            // which is a limit on the deployment and not on one company.
            var caseStats = await cases.GetStatsAsync(ct: ct);

            return await WriteJsonAsync(req, HttpStatusCode.OK, new
            {
                queued = queued.Select(i => new
                {
                    id = i.Id,
                    sourceMonkey = i.SourceMonkey,
                    payloadId = i.PayloadId,
                    dedupKey = i.DedupKey,
                    priority = i.Priority,
                    status = i.Status,
                    attempts = i.Attempts,
                    maxAttempts = i.MaxAttempts,
                    leasedUntil = i.LeasedUntil?.ToString("O"),
                    leasedBy = i.LeasedBy,
                    lastError = i.LastError,
                    enqueuedAt = i.EnqueuedAt.ToString("O"),
                }).ToList(),
                stats = new
                {
                    queued = queueStats.Queued,
                    leased = queueStats.Leased,
                    doneToday = queueStats.DoneToday,
                    failedToday = queueStats.FailedToday,
                    abandonedTotal = queueStats.AbandonedTotal,
                    discardedTotal = queueStats.DiscardedTotal,
                    oldestQueuedAt = queueStats.OldestQueuedAt?.ToString("O"),
                },
                // The resolved values, not the raw settings: what actually bounds spend is the
                // default when a setting is absent, and a null would read as "no limit".
                limits = new
                {
                    maxInvestigationsPerDay = limits.ResolvedMaxInvestigationsPerDay,
                    concurrency = limits.ResolvedConcurrency,
                    investigationsToday = caseStats.InvestigationsToday,
                },
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GetInspectorQueue failed.");
            return await WriteJsonAsync(req, HttpStatusCode.InternalServerError, new { error = "Unexpected server error." });
        }
    }

    /// <summary>
    /// Toast delivery is a poll, not a push: "undelivered" is relative to the timestamp the client
    /// sends back, which is why there is no acknowledgement write here.
    /// </summary>
    [Function("GetInspectorToasts")]
    public async Task<HttpResponseData> GetToastsAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "inspector/toasts")] HttpRequestData req,
        CancellationToken ct)
    {
        try
        {
            // Scoped to this agent's own notifications: the toast channel is shared with every other
            // monkey, so returning the whole channel from an /inspector/ route would credit the
            // Inspector for work the SLO Burn-Rate Monkey did.
            var toasts = (await notificationLog.GetUndeliveredToastsAsync(ReadSince(req), ct))
                .Where(t => t.SourceType == NotificationSourceType.ForensicCase)
                .ToList();

            return await WriteJsonAsync(req, HttpStatusCode.OK, new
            {
                // Subject only - the body can quote the case verbatim, and this endpoint is anonymous.
                toasts = toasts.Select(t => new
                {
                    id = t.Id,
                    sourceType = t.SourceType,
                    sourceId = t.SourceId,
                    subject = t.Subject,
                    sentAt = t.SentAt.ToString("O"),
                }).ToList(),
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GetInspectorToasts failed.");
            return await WriteJsonAsync(req, HttpStatusCode.InternalServerError, new { error = "Unexpected server error." });
        }
    }

    /// <summary>
    /// The anonymous projection: verdict text and counters, never the transcript and never a member
    /// id. Everything a summary needs is already denormalised onto the case, so there is no join.
    /// </summary>
    private static CaseSummary ToSummary(ForensicCase c) => new(
        c.Id,
        c.CaseId,
        c.EntityKey,
        // Null rather than a placeholder - the club catalogue does not cover every entity and an
        // invented brand would be worse than an honest gap.
        c.BrandName,
        c.ClubName,
        c.Vertical,
        c.Stage,
        c.Status,
        c.Decision,
        c.Verdict,
        // Carried on escalations too: naming the likely cause and being able to close the case are
        // different claims, and the first is useful to a human even when the second is impossible.
        c.ProbableRootCause,
        c.Confidence,
        c.BlockedBy,
        c.AlertLevel,
        // Populated for the downgrades as well - "why is this NOT a Red Alert" is the question an
        // operator actually asks, and it is how the candidate rate gets measured.
        c.AlertReason,
        // Who fixes it, which is a different question from the brand above (who is affected).
        c.OwnerTeam,
        c.OwnerPrimary,
        c.OwnerBackup,
        c.OwnerResolvedBy,
        c.ClubId,
        c.IcePartnerId,
        c.ClubResolvedBy,
        c.CitationsTotal,
        // Carried next to the total on purpose: the ratio of the two is the one quality figure a
        // confident-sounding model cannot fake.
        c.CitationsVerified,
        c.LlmCalls,
        c.TokensIn,
        c.TokensOut,
        c.DurationMs,
        c.StartedAt.ToString("O"),
        c.CompletedAt?.ToString("O"),
        c.Phase,
        c.ErrorMessage);

    /// <summary>Blank is treated as absent: an empty filter value must mean "no filter", not "match nothing".</summary>
    private static string? ReadQuery(HttpRequestData req, string key)
    {
        var raw = System.Web.HttpUtility.ParseQueryString(req.Url.Query)[key];
        return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
    }

    private static int ReadTop(HttpRequestData req)
    {
        var raw = System.Web.HttpUtility.ParseQueryString(req.Url.Query)["top"];
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var top) || top <= 0)
        {
            return DefaultTop;
        }
        return Math.Min(top, MaxTop);
    }

    private static DateTimeOffset ReadSince(HttpRequestData req)
    {
        var raw = System.Web.HttpUtility.ParseQueryString(req.Url.Query)["since"];
        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var since)
            ? since
            : DateTimeOffset.UtcNow - DefaultToastLookback;
    }

    private static async Task<HttpResponseData> WriteJsonAsync(HttpRequestData req, HttpStatusCode statusCode, object payload)
    {
        var response = req.CreateResponse(statusCode);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        response.Headers.Add("Cache-Control", "no-store");
        await response.WriteStringAsync(JsonSerializer.Serialize(payload, JsonOptions));
        return response;
    }

    /// <summary>
    /// Lower-cased member names on purpose: this record is serialised straight to the client and the
    /// UI reads camelCase, so the shape is pinned here rather than left to a serializer policy.
    /// </summary>
    private sealed record CaseSummary(
        Guid id,
        string caseId,
        string entityKey,
        string? brandName,
        string? clubName,
        string vertical,
        string stage,
        string status,
        string? decision,
        string? verdict,
        string? probableRootCause,
        double confidence,
        string? blockedBy,
        string alertLevel,
        string? alertReason,
        string? ownerTeam,
        string? ownerPrimary,
        string? ownerBackup,
        string? ownerResolvedBy,
        string? clubId,
        string? icePartnerId,
        string? clubResolvedBy,
        int citationsTotal,
        int citationsVerified,
        int llmCalls,
        int tokensIn,
        int tokensOut,
        long durationMs,
        string startedAt,
        string? completedAt,
        string? phase,
        string? errorMessage);
}
