using System.Net;
using System.Text.Json;
using System.Web;
using JourneyMonitor.Core.Onboarding;
using JourneyMonitor.Core.Options;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Functions.Functions;

/// <summary>
/// GET /api/onboarding/discover — read-only profile of one registered Application Insights
/// resource, so an engineer onboarding a new company learns on day one whether that resource can
/// attribute an incident to a customer, instead of finding out months later.
///
/// Gated by <see cref="MonitoringOptions.InternalApiSecret"/> (fails CLOSED, see
/// <see cref="SettingsWriteSecretGuard"/>) - the strictest pattern in this app, and the right one:
/// like <see cref="ListLlmClientModelsFunction"/> this endpoint makes a real outbound query on the
/// caller's behalf against a caller-named resource, so reachable directly (bypassing the Web
/// proxy's Entra check) it would be an unauthenticated probe of every telemetry resource the
/// platform identity can read, with the response returning that customer's dimension names.
///
/// Measurement only. It proposes no CompanyProfile and calls no model.
///
/// POST /api/onboarding/draft sits alongside it and DOES propose - a draft profile for a human to
/// edit - but still without a model, and still without persisting anything.
/// </summary>
public sealed class TelemetryDiscoveryFunctions(
    ITelemetryResourceProfiler profiler,
    IOptions<MonitoringOptions> monitoringOptions,
    ILogger<TelemetryDiscoveryFunctions> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    [Function("DiscoverTelemetryResource")]
    public async Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "onboarding/discover")] HttpRequestData req,
        CancellationToken ct)
    {
        if (!SettingsWriteSecretGuard.HasMatchingSecret(req, monitoringOptions.Value.InternalApiSecret, SettingsWriteSecretGuard.InternalApiSecretHeaderName))
        {
            return await WriteJsonAsync(req, HttpStatusCode.Unauthorized, new { error = "Missing or invalid X-Internal-Api-Secret header. Monitoring:InternalApiSecret must be configured for this endpoint to respond." });
        }

        var query = HttpUtility.ParseQueryString(req.Url.Query);
        var alias = query["alias"]?.Trim() ?? "";
        if (alias.Length == 0)
        {
            return await WriteJsonAsync(req, HttpStatusCode.BadRequest, new { error = "alias is required." });
        }

        var request = new TelemetryProfileRequest(
            Alias: alias,
            Environment: string.IsNullOrWhiteSpace(query["environment"]) ? InsightsEnvironmentDefault : query["environment"]!.Trim(),
            WindowHours: ParseInt(query["hours"], TelemetryProfileDefaults.WindowHours),
            Tables: ParseTables(query["tables"]));

        try
        {
            var profile = await profiler.ProfileAsync(request, ct);

            // An alias/environment the catalog cannot resolve is a caller mistake, not a server
            // fault - and it has to stay distinguishable from a resource that answered with
            // nothing, which is a 200 carrying an uncomfortable finding.
            var status = profile.Status == TelemetryProfileStatus.SourceUnresolved
                ? HttpStatusCode.BadRequest
                : HttpStatusCode.OK;

            return await WriteJsonAsync(req, status, profile);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return await WriteJsonAsync(req, HttpStatusCode.RequestTimeout, new { error = "Discovery was cancelled before it completed." });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "DiscoverTelemetryResource failed for alias {Alias}", alias);
            return await WriteJsonAsync(req, HttpStatusCode.InternalServerError, new { error = "Unexpected server error." });
        }
    }

    private const string InsightsEnvironmentDefault = "production";

    /// <summary>
    /// POST /api/onboarding/draft — runs the same discovery and turns it into a DRAFT
    /// <c>CompanyProfile</c> plus the warnings a reviewer needs. Persists nothing: the draft is
    /// returned, edited by a human, and saved through PUT /api/settings/company-profiles/{key}.
    ///
    /// Still no model call. The draft proposes identity sources and services from measurements
    /// and refuses to invent a single flow, step or business name - see <see cref="ProfileDraftBuilder"/>.
    ///
    /// POST rather than GET because it carries a body (the key and display name the operator
    /// wants), not because it writes anything. Same fail-closed internal-secret gate as discover,
    /// for the same reason: it makes a real outbound query against a caller-named resource.
    /// </summary>
    [Function("BuildCompanyProfileDraft")]
    public async Task<HttpResponseData> DraftAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "onboarding/draft")] HttpRequestData req,
        CancellationToken ct)
    {
        if (!SettingsWriteSecretGuard.HasMatchingSecret(req, monitoringOptions.Value.InternalApiSecret, SettingsWriteSecretGuard.InternalApiSecretHeaderName))
        {
            return await WriteJsonAsync(req, HttpStatusCode.Unauthorized, new { error = "Missing or invalid X-Internal-Api-Secret header. Monitoring:InternalApiSecret must be configured for this endpoint to respond." });
        }

        DraftBody? body;
        try
        {
            body = await JsonSerializer.DeserializeAsync<DraftBody>(req.Body, JsonOptions, ct);
        }
        catch (JsonException ex)
        {
            // The parser's own message carries the line and path, which is the difference between
            // "fix your JSON" and a caller re-sending the same broken body.
            return await WriteJsonAsync(req, HttpStatusCode.BadRequest, new { error = $"Request body must be valid JSON: {ex.Message}" });
        }

        var alias = body?.Alias?.Trim() ?? "";
        if (alias.Length == 0)
        {
            return await WriteJsonAsync(req, HttpStatusCode.BadRequest, new { error = "Request body must include alias - the telemetry source alias registered in Settings > Telemetry sources." });
        }

        var request = new TelemetryProfileRequest(
            Alias: alias,
            Environment: string.IsNullOrWhiteSpace(body!.Environment) ? InsightsEnvironmentDefault : body.Environment!.Trim(),
            WindowHours: body.Hours ?? TelemetryProfileDefaults.WindowHours,
            Tables: body.Tables is { Count: > 0 } ? body.Tables : null);

        try
        {
            var report = await profiler.ProfileAsync(request, ct);
            if (report.Status == TelemetryProfileStatus.SourceUnresolved)
            {
                return await WriteJsonAsync(req, HttpStatusCode.BadRequest, new { error = report.Error });
            }

            var draft = ProfileDraftBuilder.Build(report, new ProfileDraftOptions(
                ProfileKey: body.ProfileKey,
                DisplayName: body.DisplayName,
                MinUsableCoveragePercent: body.MinUsableCoveragePercent ?? ProfileDraftDefaults.UsableCoveragePercent));

            // The measurement travels with the proposal. A reviewer who has to fetch the discovery
            // report separately to check a number is a reviewer who will not check it.
            return await WriteJsonAsync(req, HttpStatusCode.OK, new { draft, discovery = report });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return await WriteJsonAsync(req, HttpStatusCode.RequestTimeout, new { error = "Draft generation was cancelled before it completed." });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "BuildCompanyProfileDraft failed for alias {Alias}", alias);
            return await WriteJsonAsync(req, HttpStatusCode.InternalServerError, new { error = "Unexpected server error." });
        }
    }

    private sealed record DraftBody(
        string? Alias,
        string? Environment,
        int? Hours,
        IReadOnlyList<string>? Tables,
        string? ProfileKey,
        string? DisplayName,
        double? MinUsableCoveragePercent);

    private static int ParseInt(string? raw, int fallback) =>
        int.TryParse(raw, out var value) ? value : fallback;

    private static IReadOnlyList<string>? ParseTables(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 0 ? null : parts;
    }

    private static async Task<HttpResponseData> WriteJsonAsync(HttpRequestData req, HttpStatusCode statusCode, object body)
    {
        var response = req.CreateResponse(statusCode);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        response.Headers.Add("Cache-Control", "no-store");
        await response.WriteStringAsync(JsonSerializer.Serialize(body, JsonOptions));
        return response;
    }
}
