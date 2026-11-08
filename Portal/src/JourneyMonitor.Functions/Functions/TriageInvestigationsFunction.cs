using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring.Inspector;
using JourneyMonitor.Core.Monitoring.Kpi;
using JourneyMonitor.Core.Options;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Functions.Functions;

/// <summary>
/// Spec 1000 Triage API. POST /api/triage/investigations is the manual entry point (the daemons
/// call <see cref="ITriageSignalSubmitter"/> directly) and is gated by X-Internal-Api-Secret with
/// the same fail-closed guard as RunMonkeyNow, since every accepted signal can cost an LLM run.
/// The two GETs are anonymous reads like every other read in this app.
/// </summary>
public sealed class TriageInvestigationsFunction(
    ITriageSignalSubmitter submitter,
    ITriageInvestigationStore store,
    IOptions<MonitoringOptions> monitoringOptions,
    ILogger<TriageInvestigationsFunction> logger,
    KpiProfileResolver? profileResolver = null)
{
    private readonly KpiProfileResolver _profiles = profileResolver ?? new KpiProfileResolver();

    public const int DefaultTop = 50;
    public const int MaxKpiLength = 64;
    public const int MaxDetectionLength = 400;
    public const int MaxStageLength = 64;

    [Function("SubmitTriageInvestigation")]
    public async Task<HttpResponseData> SubmitAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "triage/investigations")] HttpRequestData req,
        CancellationToken ct)
    {
        if (!SettingsWriteSecretGuard.HasMatchingSecret(req, monitoringOptions.Value.InternalApiSecret, SettingsWriteSecretGuard.InternalApiSecretHeaderName))
        {
            return await Spec1000Http.WriteJsonAsync(req, HttpStatusCode.Unauthorized, new { error = "Missing or invalid X-Internal-Api-Secret header. Monitoring:InternalApiSecret must be configured for this endpoint to accept requests." });
        }

        try
        {
            var (signal, errors, requestedProfile) = await ParseSignalAsync(req, ct);
            if (signal is null)
            {
                return await Spec1000Http.BadRequestAsync(req, errors);
            }

            var scope = await _profiles.ResolveWriteAsync(requestedProfile, ct);
            if (!scope.IsValid) return await Spec1000Http.BadRequestAsync(req, [scope.Error!]);
            signal = signal with { ProfileKey = scope.ProfileKey };

            var submission = await submitter.SubmitAsync(signal, ct);
            return await Spec1000Http.WriteJsonAsync(req, HttpStatusCode.Accepted, new { investigationId = submission.InvestigationId, duplicate = submission.Duplicate });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "SubmitTriageInvestigation failed unexpectedly");
            return await Spec1000Http.WriteJsonAsync(req, HttpStatusCode.InternalServerError, new { error = "Unexpected server error." });
        }
    }

    [Function("GetTriageInvestigations")]
    public async Task<HttpResponseData> GetRecentAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "triage/investigations")] HttpRequestData req,
        CancellationToken ct)
    {
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        if (!Spec1000Http.TryReadIntInRange(query["top"], DefaultTop, 1, Spec1000Http.MaxTop, out var top))
        {
            return await Spec1000Http.BadRequestAsync(req, [$"top must be an integer between 1 and {Spec1000Http.MaxTop}."]);
        }

        try
        {
            var scope = await _profiles.ResolveReadAsync(query["profileKey"], ct);
            if (!scope.IsValid) return await Spec1000Http.BadRequestAsync(req, [scope.Error!]);

            var investigations = await store.GetRecentAsync(top, scope.ProfileKey, ct);
            return await Spec1000Http.WriteJsonAsync(req, HttpStatusCode.OK, investigations.Select(WithProfileKey).ToList());
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GetTriageInvestigations failed unexpectedly");
            return await Spec1000Http.WriteJsonAsync(req, HttpStatusCode.InternalServerError, new { error = "Unexpected server error." });
        }
    }

    [Function("GetTriageInvestigation")]
    public async Task<HttpResponseData> GetByIdAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "triage/investigations/{id:guid}")] HttpRequestData req,
        Guid id,
        CancellationToken ct)
    {
        try
        {
            var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
            var scope = await _profiles.ResolveReadAsync(query["profileKey"], ct);
            if (!scope.IsValid) return await Spec1000Http.BadRequestAsync(req, [scope.Error!]);

            var investigation = await store.GetAsync(id, ct);
            // Contract v6 §5: another company's investigation does not exist for this one.
            if (investigation is null || !new ProfileReadScope(scope.ProfileKey).Includes(TriageMonkeyService.ProfileKeyOf(investigation.Signal)))
            {
                return await Spec1000Http.WriteJsonAsync(req, HttpStatusCode.NotFound, new { error = $"Investigation '{id}' not found." });
            }

            return await Spec1000Http.WriteJsonAsync(req, HttpStatusCode.OK, WithProfileKey(investigation));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GetTriageInvestigation failed unexpectedly for {Id}", id);
            return await Spec1000Http.WriteJsonAsync(req, HttpStatusCode.InternalServerError, new { error = "Unexpected server error." });
        }
    }

    /// <summary>Contract v3 §1.5: every item carries a top-level profileKey (the investigation's Signal.ProfileKey).</summary>
    private static JsonObject WithProfileKey(TriageInvestigation investigation)
    {
        var item = JsonSerializer.SerializeToNode(investigation, TriageJson.Options)!.AsObject();
        item["profileKey"] = TriageMonkeyService.ProfileKeyOf(investigation.Signal);
        return item;
    }

    private static async Task<(TriageSignal? Signal, List<string> Errors, string? ProfileKey)> ParseSignalAsync(HttpRequestData req, CancellationToken ct)
    {
        var errors = new List<string>();
        JsonDocument doc;
        try
        {
            doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: ct);
        }
        catch (JsonException)
        {
            errors.Add("Request body must be valid JSON.");
            return (null, errors, null);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                errors.Add("Request body must be a JSON object.");
                return (null, errors, null);
            }

            var detectedAtText = ReadString(root, "detectedAt");
            DateTimeOffset detectedAt = default;
            if (string.IsNullOrWhiteSpace(detectedAtText))
                errors.Add("detectedAt is required (ISO 8601).");
            else if (!TryParseIso8601(detectedAtText.Trim(), out detectedAt))
                errors.Add("detectedAt must be an ISO 8601 date/time with a time zone (e.g. 2026-11-05T11:40:00Z).");

            var pillarText = ReadString(root, "pillar");
            KpiPillar pillar = default;
            if (string.IsNullOrWhiteSpace(pillarText))
                errors.Add("pillar is required.");
            else if (!KpiPillarNames.TryParseDisplay(pillarText.Trim(), out pillar))
                errors.Add("pillar must be Business Health, Application Health or Infrastructure Health.");

            var kpi = ReadString(root, "kpi")?.Trim();
            if (string.IsNullOrEmpty(kpi) || kpi.Length > MaxKpiLength)
                errors.Add($"kpi is required (1..{MaxKpiLength} chars).");

            var detection = ReadString(root, "detection")?.Trim();
            if (string.IsNullOrEmpty(detection) || detection.Length > MaxDetectionLength)
                errors.Add($"detection is required (1..{MaxDetectionLength} chars).");

            var domain = NullIfBlank(ReadString(root, "domain"));
            if (domain is not null && !KpiDomains.All.Contains(domain))
                errors.Add("domain must be hotels, cars, cruises or air.");

            var stage = NullIfBlank(ReadString(root, "stage"));
            if (stage is not null && stage.Length > MaxStageLength)
                errors.Add($"stage must be at most {MaxStageLength} chars.");

            if (errors.Count > 0) return (null, errors, null);

            var signal = new TriageSignal(
                detectedAt.ToUniversalTime(),
                KpiPillarNames.ToDisplay(pillar),
                kpi!,
                detection!,
                domain,
                stage,
                AnomalyId: null,
                TriageSignalSources.Manual);
            return (signal, errors, NullIfBlank(ReadString(root, "profileKey")));
        }
    }

    private static readonly string[] Iso8601Formats =
    [
        "yyyy-MM-dd'T'HH:mmzzz", "yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz",
        "yyyy-MM-dd'T'HH:mm'Z'", "yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
    ];

    /// <summary>Only ISO 8601 with an explicit zone ("Z" or ±hh:mm): an unzoned time would be guessed, not known.</summary>
    private static bool TryParseIso8601(string text, out DateTimeOffset value) =>
        DateTimeOffset.TryParseExact(text, Iso8601Formats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out value);

    private static string? ReadString(JsonElement root, string name)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            return property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                _ => property.Value.GetRawText(),
            };
        }
        return null;
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>Response/query helpers shared by the Spec 1000 HTTP functions. Every body uses <see cref="TriageJson.Options"/>.</summary>
internal static class Spec1000Http
{
    internal const int MaxTop = 500;

    internal static async Task<HttpResponseData> WriteJsonAsync(HttpRequestData req, HttpStatusCode statusCode, object body)
    {
        var response = req.CreateResponse(statusCode);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        response.Headers.Add("Cache-Control", "no-store");
        await response.WriteStringAsync(JsonSerializer.Serialize(body, body.GetType(), TriageJson.Options));
        return response;
    }

    internal static Task<HttpResponseData> BadRequestAsync(HttpRequestData req, IReadOnlyList<string> errors) =>
        WriteJsonAsync(req, HttpStatusCode.BadRequest, new { errors });

    /// <summary>Missing/blank uses the default; anything present must be an integer inside [min, max].</summary>
    internal static bool TryReadIntInRange(string? raw, int defaultValue, int min, int max, out int value)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            value = defaultValue;
            return true;
        }

        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value >= min && value <= max)
        {
            return true;
        }

        value = defaultValue;
        return false;
    }
}
