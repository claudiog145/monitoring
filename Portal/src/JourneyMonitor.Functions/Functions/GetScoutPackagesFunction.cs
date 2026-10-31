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
/// Read side of the Scout Monkey's detection phase.
///
/// GET /api/scout/packages      - summaries of the handoff packages, newest first.
/// GET /api/scout/packages/{id} - the full package, including the board's verbatim evidence.
/// GET /api/scout/runs          - recent cycles, including the quiet and the failed ones.
/// GET /api/scout/stats         - cumulative counters across the recorded cycles.
///
/// The list endpoints take <c>?board=</c> and <c>?profileKey=</c>; both absent means everything,
/// so a caller written before multi-company existed keeps working unchanged.
///
/// The list deliberately omits the evidence payloads: those carry member ids and correlation
/// ids, so the full package is gated by X-Internal-Api-Secret, the same fail-closed guard
/// GetFindingDoctorReport uses. The list and the run log stay anonymous like /api/findings.
/// </summary>
public sealed class GetScoutPackagesFunction(
    IInvestigationPackageRepository packages,
    IScoutRunRepository runs,
    IScoutLlmCallLogRepository llmCallLogs,
    IOptions<MonitoringOptions> monitoringOptions,
    ILogger<GetScoutPackagesFunction> logger)
{
    private const int DefaultTop = 50;
    private const int MaxTop = 200;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Optional <c>?board=latency|exception</c> filter - one endpoint serving both scouts rather
    /// than two parallel route trees. An unrecognised value is ignored rather than rejected: the
    /// honest fallback is "everything", not an error the caller cannot act on.
    /// </summary>
    private static string? ReadBoard(HttpRequestData req)
    {
        var raw = System.Web.HttpUtility.ParseQueryString(req.Url.Query)["board"];
        return ScoutBoard.All.FirstOrDefault(b => string.Equals(b, raw, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Optional <c>?profileKey=</c> filter - which company's work to show. Absent or blank means
    /// every company, which is what the portal asked for before it knew about companies at all.
    /// Blank is collapsed to null rather than passed through: the empty string is a real stored
    /// ProfileKey (rows written before the column existed), so "?profileKey=" must not be read as
    /// a request for those. Unknown keys are not rejected - they simply match nothing, which is a
    /// truthful empty list rather than an error the caller cannot act on.
    /// </summary>
    private static string? ReadProfileKey(HttpRequestData req)
    {
        var raw = System.Web.HttpUtility.ParseQueryString(req.Url.Query)["profileKey"];
        return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
    }

    [Function("GetScoutPackages")]
    public async Task<HttpResponseData> GetPackagesAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "scout/packages")] HttpRequestData req,
        CancellationToken ct)
    {
        try
        {
            var items = await packages.GetRecentAsync(ReadTop(req), ReadBoard(req), ReadProfileKey(req), ct);
            return await WriteJsonAsync(req, HttpStatusCode.OK, items.Select(ToSummary).ToList());
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GetScoutPackages failed.");
            return await WriteJsonAsync(req, HttpStatusCode.InternalServerError, new { error = "Unexpected server error." });
        }
    }

    [Function("GetScoutPackageById")]
    public async Task<HttpResponseData> GetPackageByIdAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "scout/packages/{id}")] HttpRequestData req,
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

        if (!Guid.TryParse(id, out var packageId))
        {
            return await WriteJsonAsync(req, HttpStatusCode.BadRequest, new { error = "id must be a GUID." });
        }

        try
        {
            var package = await packages.GetByIdAsync(packageId, ct);
            if (package is null)
            {
                return await WriteJsonAsync(req, HttpStatusCode.NotFound, new { error = $"No investigation package with id '{id}'." });
            }

            var summary = ToSummary(package);
            var call = await llmCallLogs.GetByPackageIdAsync(package.Id, ct);
            // The board query lives on the run, not on every package - the text is identical for
            // all packages of a cycle - so it is stitched back in here to keep the response
            // self-contained for whoever reads one package. Scoped to the package's own company so
            // a busy second customer cannot push the owning cycle out of the window.
            var run = (await runs.GetRecentAsync(200, package.Board, package.ProfileKey, ct)).FirstOrDefault(r => r.Id == package.ScoutRunId);
            var recurrence = await GetRecurrenceOrNoneAsync(package, ct);

            return await WriteJsonAsync(req, HttpStatusCode.OK, new
            {
                summary.id,
                summary.caseId,
                summary.board,
                summary.vertical,
                summary.stage,
                summary.source,
                summary.club,
                summary.priority,
                summary.priorityRank,
                summary.reason,
                summary.window,
                summary.impact,
                summary.status,
                summary.scoutRunId,
                summary.createdAt,
                // A package is one 5-minute bucket; without this the reader cannot tell a one-off
                // from something that has been firing every cycle.
                recurrence = new
                {
                    occurrences = recurrence.Occurrences,
                    firstSeen = recurrence.FirstSeen?.ToString("O"),
                    lastSeen = recurrence.LastSeen?.ToString("O"),
                },
                // The two halves stay separate here too - a reader must be able to tell what the
                // board measured from what the Scout inferred.
                evidence = ParseJsonOrNull(package.DeterministicJson),
                assessment = ParseJsonOrNull(package.AssessmentJson),
                // Everything needed to reproduce the detection without rebuilding it by hand.
                queries = new
                {
                    board = ReadBoardQuery(run?.BoardQueriesJson, package.Board),
                    drillDown = package.DrillDownKql,
                },
                rawEvidence = ParseJsonOrNull(package.RawEvidenceJson),
                llmCall = call is null ? null : new
                {
                    promptText = call.PromptText,
                    rawResponseText = call.RawResponseText,
                    modelUsed = call.ModelUsed,
                    tokensIn = call.TokensIn,
                    tokensOut = call.TokensOut,
                    durationMs = call.DurationMs,
                    errorMessage = call.ErrorMessage,
                    calledAt = call.CalledAt.ToString("O"),
                },
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GetScoutPackageById failed for {Id}", id);
            return await WriteJsonAsync(req, HttpStatusCode.InternalServerError, new { error = "Unexpected server error." });
        }
    }

    /// <summary>Recurrence is context, not the package - losing it must not cost the whole response.</summary>
    private async Task<PackageRecurrence> GetRecurrenceOrNoneAsync(InvestigationPackage package, CancellationToken ct)
    {
        var lastSeparator = package.CaseId.LastIndexOf('|');
        if (lastSeparator < 0) return PackageRecurrence.None;

        try
        {
            return await packages.GetRecurrenceAsync(package.CaseId[..lastSeparator].TrimEnd(), ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read recurrence for package {Id}.", package.Id);
            return PackageRecurrence.None;
        }
    }

    [Function("GetScoutRuns")]
    public async Task<HttpResponseData> GetRunsAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "scout/runs")] HttpRequestData req,
        CancellationToken ct)
    {
        try
        {
            var items = await runs.GetRecentAsync(ReadTop(req), ReadBoard(req), ReadProfileKey(req), ct);
            return await WriteJsonAsync(req, HttpStatusCode.OK, items.Select(r => new
            {
                id = r.Id,
                board = r.Board,
                startedAt = r.StartedAt.ToString("O"),
                completedAt = r.CompletedAt?.ToString("O"),
                status = r.CompletedAt is null ? ScoutRunStatus.Running : r.Status,
                phase = r.Phase,
                window = new { from = r.WindowFrom.ToString("O"), to = r.WindowTo.ToString("O") },
                boardRowsEvaluated = r.BoardRowsEvaluated,
                clubsWatched = r.ClubsWatched,
                candidatesFound = r.CandidatesFound,
                packagesCreated = r.PackagesCreated,
                packagesSkippedDuplicate = r.PackagesSkippedDuplicate,
                packagesSkippedCap = r.PackagesSkippedCap,
                packagesVetoedByScout = r.PackagesVetoedByScout,
                durationMs = r.DurationMs,
                boardQueries = ParseJsonOrNull(r.BoardQueriesJson),
                errorMessage = r.ErrorMessage,
            }).ToList());
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GetScoutRuns failed.");
            return await WriteJsonAsync(req, HttpStatusCode.InternalServerError, new { error = "Unexpected server error." });
        }
    }

    /// <summary>Cumulative proof of work - what the Simian Army card shows under the heartbeat.</summary>
    [Function("GetScoutStats")]
    public async Task<HttpResponseData> GetStatsAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "scout/stats")] HttpRequestData req,
        CancellationToken ct)
    {
        try
        {
            var s = await runs.GetStatsAsync(ReadBoard(req), ReadProfileKey(req), ct);
            return await WriteJsonAsync(req, HttpStatusCode.OK, new
            {
                cycles = s.Cycles,
                clearCycles = s.ClearCycles,
                detectedCycles = s.DetectedCycles,
                failedCycles = s.FailedCycles,
                boardRowsEvaluated = s.BoardRowsEvaluated,
                candidatesFound = s.CandidatesFound,
                packagesCreated = s.PackagesCreated,
                duplicatesAvoided = s.DuplicatesAvoided,
                maxClubsWatched = s.MaxClubsWatched,
                llmCalls = s.LlmCalls,
                tokensIn = s.TokensIn,
                tokensOut = s.TokensOut,
                firstCycleAt = s.FirstCycleAt?.ToString("O"),
                lastCycleAt = s.LastCycleAt?.ToString("O"),
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GetScoutStats failed.");
            return await WriteJsonAsync(req, HttpStatusCode.InternalServerError, new { error = "Unexpected server error." });
        }
    }

    private static PackageSummary ToSummary(InvestigationPackage p)
    {
        var assessment = ParseJsonOrNull(p.AssessmentJson);
        return new PackageSummary(
            p.Id,
            p.CaseId,
            p.Board,
            p.Vertical,
            p.Stage,
            p.Source,
            new ClubSummary(
                p.EntityKey,
                p.EntityKind,
                p.ClubId,
                p.IcePartnerId,
                // Null rather than a placeholder: the catalogue covers only a fraction of the
                // clubs on the board, and an invented brand would be worse than an honest gap.
                p.BrandName,
                p.ClubName,
                p.ClubResolvedBy),
            p.Priority,
            p.PriorityRank,
            p.Reason,
            new WindowSummary(p.WindowFrom.ToString("O"), p.WindowTo.ToString("O")),
            new ImpactSummary(p.RequestCount, p.FailedRequestCount, p.ExceptionCount, p.AffectedUserCount),
            assessment?["symptom"]?.GetValue<string>(),
            assessment?["available"]?.GetValue<bool>() ?? false,
            p.Status,
            p.ScoutRunId,
            p.CreatedAt.ToString("O"));
    }

    private static int ReadTop(HttpRequestData req)
    {
        var raw = System.Web.HttpUtility.ParseQueryString(req.Url.Query)["top"];
        if (!int.TryParse(raw, out var top) || top <= 0) return DefaultTop;
        return Math.Min(top, MaxTop);
    }

    /// <summary>Degrades to null instead of breaking the whole response if a stored payload is somehow not valid JSON.</summary>
    private static JsonNode? ParseJsonOrNull(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadBoardQuery(string? boardQueriesJson, string board) =>
        ParseJsonOrNull(boardQueriesJson)?[board]?.GetValue<string>();

    private static async Task<HttpResponseData> WriteJsonAsync(HttpRequestData req, HttpStatusCode statusCode, object body)
    {
        var response = req.CreateResponse(statusCode);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        response.Headers.Add("Cache-Control", "no-store");
        await response.WriteStringAsync(JsonSerializer.Serialize(body, JsonOptions));
        return response;
    }

    private sealed record ClubSummary(string entityKey, string entityKind, string? clubId, string? icePartnerId, string? brandName, string? clubName, string resolvedBy);

    private sealed record WindowSummary(string from, string to);

    private sealed record ImpactSummary(int requests, int failedRequests, int exceptions, int affectedUsers);

    private sealed record PackageSummary(
        Guid id, string caseId, string board, string vertical, string stage, string source,
        ClubSummary club, string priority, int priorityRank, string reason, WindowSummary window,
        ImpactSummary impact, string? symptom, bool assessmentAvailable, string status,
        Guid scoutRunId, string createdAt);
}
