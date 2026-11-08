using System.Net;
using System.Text.Json;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Configuration;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Core.UseCases;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Functions.Functions;

/// <summary>
/// The admin API over the telemetry source catalog (monitoring.TelemetrySource) - what makes
/// pointing the platform at a different subscription a row change instead of a redeploy.
///
/// Writes carry the same fail-closed X-Settings-Write-Secret gate as every other settings
/// endpoint, and invalidate the resolver snapshot so an edited resource id takes effect on the
/// next query rather than up to a TTL later.
///
/// Every alias has an owner profile (AG8, see <see cref="TelemetrySourceOwnerPolicy"/>), across all
/// of its environments: a brand-new alias takes the profile the request names (body or
/// ?profileKey=) or else the active profile; an edit, or a row for another environment of the
/// alias, keeps the alias's owner; naming a different profile is a 409; an alias whose rows were
/// deleted keeps the owner its audit records; only an explicit transferOwnerTo moves an alias, in
/// every environment. Company profile saves refuse aliases owned by a different profile, or not in
/// the catalog.
///
/// Onboarding a NEW company is therefore three steps, because the source must be owned by a
/// profile that exists and the profile may only name sources it owns:
/// <list type="number">
/// <item>Save the draft profile (disabled) with <c>telemetrySourceAliases: []</c>.</item>
/// <item>Register its source: <c>PUT settings/telemetry-sources</c> with <c>profileKey</c> = the new company
/// (or <c>transferOwnerTo</c> for a source that already exists and is being handed over).</item>
/// <item>Save the profile again with the alias in its services.</item>
/// </list>
/// </summary>
public sealed class TelemetrySourceAdminFunctions(
    ITelemetrySourceStore store,
    IKqlSourceResolver resolver,
    IOptions<MonitoringOptions> monitoringOptions,
    ILogger<TelemetrySourceAdminFunctions> logger,
    TelemetrySourceOwnerPolicy? ownerPolicy = null)
{
    private readonly TelemetrySourceOwnerPolicy _ownerPolicy = ownerPolicy ?? new TelemetrySourceOwnerPolicy();

    private static readonly System.Text.RegularExpressions.Regex ProfileKeyPattern = new(@"^[A-Za-z0-9._-]{1,64}$");

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Function("GetTelemetrySources")]
    public async Task<HttpResponseData> ListAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "settings/telemetry-sources")] HttpRequestData req,
        CancellationToken ct) =>
        await SafeAsync(req, async () =>
            (HttpStatusCode.OK, (object)new { sources = await store.GetAllAsync(ct) }));

    [Function("UpsertTelemetrySource")]
    public async Task<HttpResponseData> UpsertAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "settings/telemetry-sources")] HttpRequestData req,
        CancellationToken ct)
    {
        if (!HasWriteSecret(req)) return await UnauthorizedAsync(req);

        return await SafeAsync(req, async () =>
        {
            var body = await JsonSerializer.DeserializeAsync<SourceBody>(req.Body, JsonOptions, ct);
            if (body is null || string.IsNullOrWhiteSpace(body.Alias) || string.IsNullOrWhiteSpace(body.ResourceId))
            {
                return (HttpStatusCode.BadRequest, (object)new { error = "Request body must include alias and resourceId." });
            }

            string environment;
            try
            {
                environment = InsightsEnvironment.Normalize(body.Environment);
            }
            catch (InsightsConfigurationException ex)
            {
                return (HttpStatusCode.BadRequest, (object)new { error = ex.Message });
            }

            // Validated here and not only at query time: a malformed id stored now would surface
            // as a failed timer run later, with nobody connecting the two events.
            if (!InsightsSourceResolver.IsInsightsResourceId(body.ResourceId))
            {
                return (HttpStatusCode.BadRequest, (object)new { error = "resourceId must be an Application Insights ARM resource id (/subscriptions/{guid}/resourceGroups/{rg}/providers/microsoft.insights/components/{name})." });
            }

            var requestedOwner = !string.IsNullOrWhiteSpace(body.ProfileKey)
                ? body.ProfileKey.Trim()
                : System.Web.HttpUtility.ParseQueryString(req.Url.Query)["profileKey"]?.Trim();
            if (!string.IsNullOrEmpty(requestedOwner) && !ProfileKeyPattern.IsMatch(requestedOwner))
            {
                return (HttpStatusCode.BadRequest, (object)new { error = $"profileKey '{requestedOwner}' must be 1-64 characters of letters, digits, dot, dash or underscore." });
            }

            var alias = body.Alias.Trim().ToLowerInvariant();
            var owner = await _ownerPolicy.DecideUpsertAsync(store, alias, environment, requestedOwner, body.TransferOwnerTo, ct);
            if (!owner.IsAllowed)
            {
                return ((HttpStatusCode)owner.StatusCode, (object)new { error = owner.Error });
            }

            var saved = await store.UpsertAsync(
                new TelemetrySourceEntry(
                    alias,
                    environment,
                    string.IsNullOrWhiteSpace(body.Kind) ? TelemetrySourceKind.AppInsights : body.Kind!.Trim(),
                    body.ResourceId.Trim(),
                    body.AppId, body.TenantId, body.Enabled ?? true, body.Notes,
                    OwnerProfileKey: owner.OwnerProfileKey),
                string.IsNullOrWhiteSpace(body.ChangedBy) ? "portal" : body.ChangedBy!,
                ct);

            // An explicit transfer moves the alias in every environment, each change audited.
            foreach (var other in owner.AlsoMove ?? [])
            {
                await store.UpsertAsync(other with { OwnerProfileKey = owner.OwnerProfileKey },
                    string.IsNullOrWhiteSpace(body.ChangedBy) ? "portal" : body.ChangedBy!, ct);
            }

            resolver.Invalidate();
            return (HttpStatusCode.OK, (object)saved);
        });
    }

    [Function("DeleteTelemetrySource")]
    public async Task<HttpResponseData> DeleteAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "settings/telemetry-sources/{alias}/{environment}/delete")] HttpRequestData req,
        string alias,
        string environment,
        CancellationToken ct)
    {
        if (!HasWriteSecret(req)) return await UnauthorizedAsync(req);

        return await SafeAsync(req, async () =>
        {
            var (changedBy, requestedOwner) = await ReadDeleteBodyAsync(req, ct);
            var existing = (await store.GetAllAsync(ct)).FirstOrDefault(s =>
                string.Equals(s.Alias, alias.Trim(), StringComparison.OrdinalIgnoreCase)
                && string.Equals(s.Environment, environment.Trim(), StringComparison.OrdinalIgnoreCase));
            var decision = TelemetrySourceOwnerPolicy.DecideDelete(alias.Trim(), existing,
                requestedOwner ?? System.Web.HttpUtility.ParseQueryString(req.Url.Query)["profileKey"]);
            if (!decision.IsAllowed)
            {
                return ((HttpStatusCode)decision.StatusCode, (object)new { error = decision.Error });
            }

            await store.DeleteAsync(alias, environment, changedBy, ct);
            resolver.Invalidate();
            return (HttpStatusCode.OK, (object)new { alias, environment, removed = true });
        });
    }

    [Function("GetTelemetrySourceAudit")]
    public async Task<HttpResponseData> AuditAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "settings/telemetry-sources-audit")] HttpRequestData req,
        CancellationToken ct) =>
        await SafeAsync(req, async () =>
        {
            var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
            var top = int.TryParse(query["top"], out var parsed) ? Math.Min(parsed, 1000) : 200;
            return (HttpStatusCode.OK, (object)new { entries = await store.GetAuditAsync(top, ct) });
        });

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
            logger.LogError(ex, "Telemetry source admin call failed");
            return await WriteJsonAsync(req, HttpStatusCode.InternalServerError, new { error = "Unexpected server error." });
        }
    }

    private static async Task<(string ChangedBy, string? ProfileKey)> ReadDeleteBodyAsync(HttpRequestData req, CancellationToken ct)
    {
        try
        {
            var body = await JsonSerializer.DeserializeAsync<DeleteBody>(req.Body, JsonOptions, ct);
            return (string.IsNullOrWhiteSpace(body?.ChangedBy) ? "portal" : body!.ChangedBy!,
                string.IsNullOrWhiteSpace(body?.ProfileKey) ? null : body!.ProfileKey!.Trim());
        }
        catch (JsonException)
        {
            // These endpoints accept an empty body.
            return ("portal", null);
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

    private sealed record SourceBody(
        string? Alias, string? Environment, string? Kind, string? ResourceId,
        string? AppId, string? TenantId, bool? Enabled, string? Notes, string? ChangedBy, string? ProfileKey = null,
        string? TransferOwnerTo = null);

    private sealed record DeleteBody(string? ChangedBy, string? ProfileKey = null);
}
