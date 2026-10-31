using System.Net;
using System.Text.Json;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Onboarding;
using JourneyMonitor.Core.Options;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Functions.Functions;

/// <summary>
/// The admin API over company profiles (monitoring.CompanyProfile) - what makes onboarding a
/// company a row change instead of a redeploy.
///
/// Writes carry the same fail-closed X-Settings-Write-Secret gate as every other settings
/// endpoint: this document decides how every telemetry row is classified, which entity is blamed
/// and which team is paged, so an unauthenticated write here is worse than an unauthenticated
/// write to a KQL query.
/// </summary>
public sealed class CompanyProfileAdminFunctions(
    ICompanyProfileStore store,
    IOptions<MonitoringOptions> monitoringOptions,
    ILogger<CompanyProfileAdminFunctions> logger,
    IProfileWriteValidator? validator = null,
    ICompanyAdapterProvider? adapters = null,
    IActiveCompanyProfileStore? activeStore = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Optional on the constructor so this endpoint validates today whether or not anything is
    /// registered in DI. Register a richer <see cref="IProfileWriteValidator"/> to replace the
    /// structural one; nothing here needs to change when that happens.
    /// </summary>
    private readonly IProfileWriteValidator _validator = validator ?? StructuralProfileWriteValidator.Instance;

    [Function("GetCompanyProfiles")]
    public async Task<HttpResponseData> ListAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "settings/company-profiles")] HttpRequestData req,
        CancellationToken ct) =>
        await SafeAsync(req, async () =>
            (HttpStatusCode.OK, (object)new { profiles = await store.GetAllAsync(ct) }));

    /// <summary>
    /// A sibling route, NOT a child of company-profiles - the same shape
    /// <c>settings/telemetry-sources-audit</c> already uses, and for a reason that was measured
    /// rather than assumed: the Functions host does NOT give a literal segment precedence over a
    /// parameter one the way ASP.NET Core endpoint routing does. It matched
    /// <c>company-profiles/audit</c> against <c>company-profiles/{key}</c> and answered
    /// "No company profile is stored under 'audit'" with a 404, because "GetCompanyProfile" sorts
    /// before "GetCompanyProfileAudit". Nesting it would also make a company keyed "audit"
    /// unreachable.
    /// </summary>
    [Function("GetCompanyProfileAudit")]
    public async Task<HttpResponseData> AuditAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "settings/company-profiles-audit")] HttpRequestData req,
        CancellationToken ct) =>
        await SafeAsync(req, async () =>
        {
            var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
            var top = int.TryParse(query["top"], out var parsed) ? Math.Min(Math.Max(parsed, 1), 1000) : 200;
            return (HttpStatusCode.OK, (object)new { entries = await store.GetAuditAsync(top, ct) });
        });

    /// <summary>
    /// Which company the platform is monitoring right now. Ungated like every other GET here.
    ///
    /// A sibling route rather than a child, for the reason spelled out on the audit route above:
    /// under the Functions host this only ever worked because "GetActiveCompanyProfile" happens to
    /// sort before "GetCompanyProfile". Renaming either function would have turned the company
    /// switch into a 404 with nothing to connect the two events.
    ///
    /// Answers with a null profileKey - not 404 - when nothing is activated or when
    /// monitoring.ActiveCompanyProfile is not provisioned. "Nobody has chosen, so
    /// Company:DefaultProfileKey is in charge" is a state, not a missing resource, and the UI
    /// needs the same shape either way.
    /// </summary>
    [Function("GetActiveCompanyProfile")]
    public async Task<HttpResponseData> GetActiveAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "settings/company-profiles-active")] HttpRequestData req,
        CancellationToken ct) =>
        await SafeAsync(req, async () => (HttpStatusCode.OK, (object)await ReadActiveAsync(ct)));

    /// <summary>
    /// Switches the monitored company without a redeploy - the reason the pointer is a row.
    ///
    /// Gated like every other write here, and for a stronger reason than most: this decides whose
    /// telemetry is classified, so an unauthenticated flip points the whole platform at another
    /// customer's vocabulary.
    /// </summary>
    [Function("SetActiveCompanyProfile")]
    public async Task<HttpResponseData> SetActiveAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "settings/company-profiles-active")] HttpRequestData req,
        CancellationToken ct)
    {
        if (!HasWriteSecret(req)) return await UnauthorizedAsync(req);

        return await SafeAsync(req, async () =>
        {
            if (activeStore is null)
            {
                return (HttpStatusCode.ServiceUnavailable, (object)new
                {
                    error = "The monitored company cannot be switched here: monitoring.ActiveCompanyProfile is not "
                        + "provisioned on this deployment's JourneyStore database (apply "
                        + "Sql/monitoring/CreateMonitoringCompanyProfile.sql). Company:DefaultProfileKey is in charge until it is.",
                });
            }

            var body = await JsonSerializer.DeserializeAsync<SetActiveBody>(req.Body, JsonOptions, ct);
            var requested = body?.ProfileKey?.Trim() ?? "";
            if (requested.Length == 0)
            {
                return (HttpStatusCode.BadRequest, (object)new { error = "Request body must include profileKey." });
            }

            // Checked here as well as in usp_SetActiveCompanyProfile: the proc is the rule that
            // also covers a write made in SSMS, but only this side still has the operator to tell,
            // and a THROW would surface as an opaque 500.
            var target = (await store.GetAllAsync(ct))
                .FirstOrDefault(p => string.Equals(p.ProfileKey, requested, StringComparison.OrdinalIgnoreCase));
            if (target is null)
            {
                return (HttpStatusCode.NotFound, (object)new { error = $"No company profile is stored under '{requested}'." });
            }
            if (!target.Enabled)
            {
                return (HttpStatusCode.BadRequest, (object)new
                {
                    error = $"Company profile '{target.ProfileKey}' is disabled; enable it before activating it.",
                });
            }

            var changedBy = string.IsNullOrWhiteSpace(body!.UpdatedBy) ? "portal" : body.UpdatedBy!.Trim();

            // The STORED key, not what was typed: the lookup is case-insensitive, and storing the
            // caller's casing would make the pointer disagree with the row it points at.
            await activeStore.SetActiveAsync(target.ProfileKey, changedBy, ct);

            // Without this the switch takes up to the provider's TTL to be seen, and in the
            // meantime cycles keep classifying against the company nobody selected any more.
            adapters?.Invalidate();

            return (HttpStatusCode.OK, (object)await ReadActiveAsync(ct));
        });
    }

    [Function("GetCompanyProfile")]
    public async Task<HttpResponseData> GetAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "settings/company-profiles/{key}")] HttpRequestData req,
        string key,
        CancellationToken ct) =>
        await SafeAsync(req, async () =>
        {
            // Read through GetAllAsync rather than GetAsync: the contract documents GetAsync as
            // returning null for a DISABLED row, which is right for classification and wrong for
            // an editor - a profile that was switched off is exactly the one somebody needs to
            // open and fix.
            var profile = (await store.GetAllAsync(ct))
                .FirstOrDefault(p => string.Equals(p.ProfileKey, key.Trim(), StringComparison.OrdinalIgnoreCase));

            return profile is null
                ? (HttpStatusCode.NotFound, (object)new { error = $"No company profile is stored under '{key}'." })
                : (HttpStatusCode.OK, (object)profile);
        });

    [Function("UpsertCompanyProfile")]
    public async Task<HttpResponseData> UpsertAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "settings/company-profiles/{key}")] HttpRequestData req,
        string key,
        CancellationToken ct)
    {
        if (!HasWriteSecret(req)) return await UnauthorizedAsync(req);

        return await SafeAsync(req, async () =>
        {
            var body = await JsonSerializer.DeserializeAsync<CompanyProfile>(req.Body, JsonOptions, ct);
            if (body is null)
            {
                return (HttpStatusCode.BadRequest, (object)new { error = "Request body must be a company profile document." });
            }

            var routeKey = key.Trim();
            if (routeKey.Length == 0)
            {
                return (HttpStatusCode.BadRequest, (object)new { error = "Route key must not be blank." });
            }

            // The route is authoritative, but a body that names a different company is a mistake
            // worth refusing rather than quietly rewriting - it is how one company's mapping ends
            // up saved over another's.
            if (!string.IsNullOrWhiteSpace(body.ProfileKey)
                && !string.Equals(body.ProfileKey.Trim(), routeKey, StringComparison.OrdinalIgnoreCase))
            {
                return (HttpStatusCode.BadRequest, (object)new { error = $"Body profileKey '{body.ProfileKey}' does not match route key '{routeKey}'." });
            }

            // System.Text.Json fills a missing member of a positional record with default(T),
            // which for these is null - and a profile with a null Services or Glossary would be
            // accepted here and throw somewhere far away, inside a timer run. Rejected at the
            // boundary, with the member named, and never defaulted silently.
            var missing = MissingMembers(body);
            if (missing.Count > 0)
            {
                return (HttpStatusCode.BadRequest, (object)new { error = $"Request body is missing required members: {string.Join(", ", missing)}." });
            }

            // The route key is applied BEFORE validation so the document that gets checked is the
            // document that would be stored, not the one that was posted.
            var candidate = body with { ProfileKey = routeKey };

            // A profile decides how every row is classified, which entity is blamed and which team
            // is paged. A bad one has to be refused here, by name, rather than accepted and left to
            // misbehave inside a timer run where nobody connects the two events - and never
            // silently repaired with a default, which would be a third story about the mapping.
            var validation = _validator.Validate(candidate);
            if (!validation.IsValid)
            {
                return (HttpStatusCode.BadRequest, (object)new
                {
                    error = $"Company profile \"{routeKey}\" was rejected: {validation.Problems[0]}"
                        + (validation.Problems.Count > 1 ? $" (and {validation.Problems.Count - 1} more problem(s) - see problems.)" : ""),
                    problems = validation.Problems,
                });
            }

            var changedBy = string.IsNullOrWhiteSpace(body.UpdatedBy) ? "portal" : body.UpdatedBy!.Trim();
            var saved = await store.UpsertAsync(candidate, changedBy, ct);

            // The provider caches an adapter per profile for 30s, so without this an operator who
            // fixes a mapping watches the broken one keep classifying - the same reason
            // KqlQueryAdminFunctions invalidates after every successful write.
            adapters?.Invalidate();

            return (HttpStatusCode.OK, (object)saved);
        });
    }

    private async Task<ActiveCompanyResponse> ReadActiveAsync(CancellationToken ct)
    {
        var active = activeStore is null ? null : await activeStore.GetActiveDetailAsync(ct);
        return new ActiveCompanyResponse(active?.ProfileKey, active?.UpdatedAt, active?.UpdatedBy);
    }

    /// <summary>Null profileKey means nothing is activated - see the GET's remark.</summary>
    private sealed record ActiveCompanyResponse(string? ProfileKey, DateTimeOffset? UpdatedAt, string? UpdatedBy);

    private sealed record SetActiveBody(string? ProfileKey, string? UpdatedBy);

    private static List<string> MissingMembers(CompanyProfile profile)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(profile.DisplayName)) missing.Add("displayName");
        if (profile.Labels is null) missing.Add("labels");
        if (profile.Services is null) missing.Add("services");
        if (profile.EntityIdentitySources is null) missing.Add("entityIdentitySources");
        if (profile.Glossary is null) missing.Add("glossary");
        return missing;
    }

    private bool HasWriteSecret(HttpRequestData req) =>
        SettingsWriteSecretGuard.HasMatchingWriteSecret(req, monitoringOptions.Value.SettingsWriteSecret);

    private static async Task<HttpResponseData> UnauthorizedAsync(HttpRequestData req) =>
        await WriteJsonAsync(req, HttpStatusCode.Unauthorized, new { error = "Missing or invalid X-Settings-Write-Secret header. Monitoring:SettingsWriteSecret must be configured for this endpoint to accept writes." });

    private async Task<HttpResponseData> SafeAsync(HttpRequestData req, Func<Task<(HttpStatusCode, object)>> handler)
    {
        try
        {
            var (status, payload) = await handler();
            return await WriteJsonAsync(req, status, payload);
        }
        catch (JsonException)
        {
            return await WriteJsonAsync(req, HttpStatusCode.BadRequest, new { error = "Request body must be valid JSON." });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Company profile admin call failed");
            return await WriteJsonAsync(req, HttpStatusCode.InternalServerError, new { error = "Unexpected server error." });
        }
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
