using System.Net;
using System.Text.Json;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Core.UseCases;
using JourneyMonitor.Infrastructure.Kql;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Functions.Functions;

/// <summary>
/// The admin API over editable KQL (monitoring.KqlQuery).
///
/// Writes carry the same fail-closed X-Settings-Write-Secret gate as every other settings
/// endpoint - see <see cref="UpsertLatencySpikeSettingFunction"/>. That matters more here than
/// anywhere else in the schema: these rows are the queries the timers run against production.
///
/// Every write invalidates the provider snapshot, otherwise a saved query would keep running the
/// previous text for up to the cache TTL and nobody could tell which one produced a given alert.
/// </summary>
public sealed class KqlQueryAdminFunctions(
    KqlQueryAdminService service,
    DbKqlQueryTextProvider provider,
    IOptions<MonitoringOptions> monitoringOptions,
    ILogger<KqlQueryAdminFunctions> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Function("GetKqlQueries")]
    public async Task<HttpResponseData> ListAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "settings/kql")] HttpRequestData req,
        CancellationToken ct) =>
        await SafeAsync(req, async () =>
            (HttpStatusCode.OK, (object)new { queries = await service.ListAsync(ct) }));

    [Function("GetKqlQuery")]
    public async Task<HttpResponseData> GetAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "settings/kql/{key}")] HttpRequestData req,
        string key,
        CancellationToken ct) =>
        await SafeAsync(req, async () =>
        {
            var detail = await service.GetAsync(key, ct);
            return detail is null
                ? (HttpStatusCode.NotFound, (object)new { error = $"Unknown query key \"{key}\"." })
                : (HttpStatusCode.OK, detail);
        });

    [Function("UpsertKqlQuery")]
    public async Task<HttpResponseData> UpsertAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "settings/kql/{key}")] HttpRequestData req,
        string key,
        CancellationToken ct)
    {
        if (!HasWriteSecret(req)) return await UnauthorizedAsync(req);

        return await SafeAsync(req, async () =>
        {
            var body = await JsonSerializer.DeserializeAsync<SaveBody>(req.Body, JsonOptions, ct);
            if (body is null || string.IsNullOrWhiteSpace(body.KqlText))
            {
                return (HttpStatusCode.BadRequest, (object)new { error = "Request body must include kqlText." });
            }

            var (status, payload) = await service.SaveAsync(
                key, body.KqlText, body.Description, body.SourceRef, body.Enabled ?? true, body.ChangedBy ?? "", ct);
            if (status == 200) provider.Invalidate();
            return ((HttpStatusCode)status, payload);
        });
    }

    /// <summary>POST rather than DELETE: this removes an override, it does not delete the query.</summary>
    [Function("RevertKqlQuery")]
    public async Task<HttpResponseData> RevertAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "settings/kql/{key}/revert")] HttpRequestData req,
        string key,
        CancellationToken ct)
    {
        if (!HasWriteSecret(req)) return await UnauthorizedAsync(req);

        return await SafeAsync(req, async () =>
        {
            var changedBy = await ReadChangedByAsync(req, ct);
            var (status, payload) = await service.RevertAsync(key, changedBy, ct);
            if (status == 200) provider.Invalidate();
            return ((HttpStatusCode)status, payload);
        });
    }

    [Function("GetKqlQueryAudit")]
    public async Task<HttpResponseData> AuditAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "settings/kql-audit")] HttpRequestData req,
        CancellationToken ct) =>
        await SafeAsync(req, async () =>
        {
            var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
            var key = query["key"];
            var top = int.TryParse(query["top"], out var parsed) ? parsed : 200;
            return (HttpStatusCode.OK, (object)new { entries = await service.GetAuditAsync(key, top, ct) });
        });

    /// <summary>One historical version with its full text - what the diff and the restore read.</summary>
    [Function("GetKqlQueryAuditEntry")]
    public async Task<HttpResponseData> AuditEntryAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "settings/kql-audit/{auditId:long}")] HttpRequestData req,
        long auditId,
        CancellationToken ct) =>
        await SafeAsync(req, async () =>
        {
            var entry = await service.GetAuditEntryAsync(auditId, ct);
            return entry is null
                ? (HttpStatusCode.NotFound, (object)new { error = $"No history entry {auditId}." })
                : (HttpStatusCode.OK, entry);
        });

    [Function("RestoreKqlQueryVersion")]
    public async Task<HttpResponseData> RestoreAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "settings/kql-audit/{auditId:long}/restore")] HttpRequestData req,
        long auditId,
        CancellationToken ct)
    {
        if (!HasWriteSecret(req)) return await UnauthorizedAsync(req);

        return await SafeAsync(req, async () =>
        {
            var changedBy = await ReadChangedByAsync(req, ct);
            var (status, payload) = await service.RestoreAsync(auditId, changedBy, ct);
            if (status == 200) provider.Invalidate();
            return ((HttpStatusCode)status, payload);
        });
    }

    [Function("ImportBuiltInKqlQueries")]
    public async Task<HttpResponseData> ImportAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "settings/kql/import-builtins")] HttpRequestData req,
        CancellationToken ct)
    {
        if (!HasWriteSecret(req)) return await UnauthorizedAsync(req);

        return await SafeAsync(req, async () =>
        {
            var body = await TryReadBodyAsync<ImportBody>(req, ct);
            var (status, payload) = await service.ImportBuiltInsAsync(
                body?.Overwrite ?? false, body?.ChangedBy ?? "", ct);
            if (status == 200) provider.Invalidate();
            return ((HttpStatusCode)status, payload);
        });
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
            logger.LogError(ex, "KQL query admin call failed");
            return await WriteJsonAsync(req, HttpStatusCode.InternalServerError, new { error = "Unexpected server error." });
        }
    }

    private static async Task<string> ReadChangedByAsync(HttpRequestData req, CancellationToken ct) =>
        (await TryReadBodyAsync<ChangedByBody>(req, ct))?.ChangedBy ?? "";

    /// <summary>These endpoints accept an empty body, so a missing or unparsable one is not an error.</summary>
    private static async Task<T?> TryReadBodyAsync<T>(HttpRequestData req, CancellationToken ct) where T : class
    {
        try
        {
            return await JsonSerializer.DeserializeAsync<T>(req.Body, JsonOptions, ct);
        }
        catch (JsonException)
        {
            return null;
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

    private sealed record SaveBody(string? KqlText, string? Description, string? SourceRef, bool? Enabled, string? ChangedBy);

    private sealed record ImportBody(bool? Overwrite, string? ChangedBy);

    private sealed record ChangedByBody(string? ChangedBy);
}
