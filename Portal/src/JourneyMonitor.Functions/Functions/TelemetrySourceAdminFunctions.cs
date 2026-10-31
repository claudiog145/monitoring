using System.Net;
using System.Text.Json;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Configuration;
using JourneyMonitor.Core.Options;
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
/// </summary>
public sealed class TelemetrySourceAdminFunctions(
    ITelemetrySourceStore store,
    IKqlSourceResolver resolver,
    IOptions<MonitoringOptions> monitoringOptions,
    ILogger<TelemetrySourceAdminFunctions> logger)
{
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

            var saved = await store.UpsertAsync(
                new TelemetrySourceEntry(
                    body.Alias.Trim().ToLowerInvariant(),
                    environment,
                    string.IsNullOrWhiteSpace(body.Kind) ? TelemetrySourceKind.AppInsights : body.Kind!.Trim(),
                    body.ResourceId.Trim(),
                    body.AppId, body.TenantId, body.Enabled ?? true, body.Notes),
                string.IsNullOrWhiteSpace(body.ChangedBy) ? "portal" : body.ChangedBy!,
                ct);

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
            await store.DeleteAsync(alias, environment, await ReadChangedByAsync(req, ct), ct);
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

    private static async Task<string> ReadChangedByAsync(HttpRequestData req, CancellationToken ct)
    {
        try
        {
            var body = await JsonSerializer.DeserializeAsync<ChangedByBody>(req.Body, JsonOptions, ct);
            return string.IsNullOrWhiteSpace(body?.ChangedBy) ? "portal" : body!.ChangedBy!;
        }
        catch (JsonException)
        {
            // These endpoints accept an empty body.
            return "portal";
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
        string? AppId, string? TenantId, bool? Enabled, string? Notes, string? ChangedBy);

    private sealed record ChangedByBody(string? ChangedBy);
}
