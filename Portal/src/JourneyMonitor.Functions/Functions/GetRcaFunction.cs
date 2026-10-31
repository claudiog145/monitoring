using System.Net;
using System.Text.Json;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Options;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Functions.Functions;

/// <summary>
/// GET /api/inspector/cases/{id}/rca — the RCA already written for an investigation, if any.
///
/// Separate from the POST that composes one so opening a case file never spends a provider call:
/// reading a document costs nothing, writing one costs money, and a UI that blurs the two will
/// eventually compose by accident.
/// </summary>
public sealed class GetRcaFunction(
    IRcaReportRepository rcaReports,
    IOptions<MonitoringOptions> monitoringOptions,
    ILogger<GetRcaFunction> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Function("GetRca")]
    public async Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "inspector/cases/{id}/rca")] HttpRequestData req,
        string id,
        CancellationToken ct)
    {
        // Same gate as the case detail it belongs to: the document quotes the raw synthesis.
        if (!SettingsWriteSecretGuard.HasMatchingSecret(req, monitoringOptions.Value.InternalApiSecret, SettingsWriteSecretGuard.InternalApiSecretHeaderName))
        {
            return await WriteJsonAsync(req, HttpStatusCode.Unauthorized, new { error = "Missing or invalid X-Internal-Api-Secret header." });
        }

        if (!Guid.TryParse(id, out var caseId))
        {
            return await WriteJsonAsync(req, HttpStatusCode.BadRequest, new { error = "id must be a GUID." });
        }

        try
        {
            var report = await rcaReports.GetByForensicCaseIdAsync(caseId, ct);

            // 404 means "no RCA has been written yet", which the UI shows as an offer to write one
            // - distinct from an error.
            return report is null
                ? await WriteJsonAsync(req, HttpStatusCode.NotFound, new { error = "No RCA has been composed for this investigation yet." })
                : await WriteJsonAsync(req, HttpStatusCode.OK, report);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GetRca failed for case {CaseId}", caseId);
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
