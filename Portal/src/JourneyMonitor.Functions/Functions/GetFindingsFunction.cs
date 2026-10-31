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
/// GET /api/findings and GET /api/findings/{id}/doctor-report — real backend for the "Simian
/// Army" tab, replacing the static wwwroot/data/monkeys.json mock (see the implementation plan,
/// Phase 3). Kept as two thin HTTP triggers over the Core repositories, same shape as
/// GetClubsFunction/GetJourneysFunction.
///
/// GetFindings stays <see cref="AuthorizationLevel.Anonymous"/> like every endpoint in this
/// Functions app (see Program.cs's Managed Identity extension-point comment). GetDoctorReport
/// additionally exposes the full LLM prompt/raw response (<see cref="ILlmCallLogRepository"/>) -
/// the Web proxy already requires the authenticated session when Entra ID is configured, but that
/// alone does not stop a caller from reaching this Function directly (e.g. a published compose
/// port), so it also requires <see cref="MonitoringOptions.InternalApiSecret"/> via the
/// X-Internal-Api-Secret header (fails CLOSED - see SettingsWriteSecretGuard).
/// </summary>
public sealed class GetFindingsFunction(
    IFindingsRepository findingsRepository,
    IDoctorReportsRepository doctorReportsRepository,
    ILlmCallLogRepository llmCallLogRepository,
    IOptions<MonitoringOptions> monitoringOptions,
    ILogger<GetFindingsFunction> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Function("GetFindings")]
    public async Task<HttpResponseData> GetFindingsAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "findings")] HttpRequestData req,
        CancellationToken ct)
    {
        try
        {
            var findings = await findingsRepository.GetAllAsync(ct);
            return await WriteJsonAsync(req, HttpStatusCode.OK, new { source = "live", count = findings.Count, findings });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GetFindings failed unexpectedly");
            return await WriteJsonAsync(req, HttpStatusCode.InternalServerError, new { source = "error", error = "Unexpected server error." });
        }
    }

    [Function("GetFindingDoctorReport")]
    public async Task<HttpResponseData> GetDoctorReportAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "findings/{id:guid}/doctor-report")] HttpRequestData req,
        Guid id,
        CancellationToken ct)
    {
        if (!SettingsWriteSecretGuard.HasMatchingSecret(req, monitoringOptions.Value.InternalApiSecret, SettingsWriteSecretGuard.InternalApiSecretHeaderName))
        {
            return await WriteJsonAsync(req, HttpStatusCode.Unauthorized, new { source = "error", error = "Missing or invalid X-Internal-Api-Secret header. Monitoring:InternalApiSecret must be configured for this endpoint to respond." });
        }

        try
        {
            var finding = await findingsRepository.GetByIdAsync(id, ct);
            if (finding is null)
            {
                return await WriteJsonAsync(req, HttpStatusCode.NotFound, new { source = "error", error = $"Finding '{id}' not found." });
            }

            var report = await doctorReportsRepository.GetByFindingIdAsync(id, ct);
            if (report is null)
            {
                return await WriteJsonAsync(req, HttpStatusCode.OK, new { source = "live", hasReport = false, finding });
            }

            // The prompt sent + raw LLM response text - shown to any Portal user, in full, under
            // this on-demand detail endpoint only (never in the GET /api/monkeys list payload).
            var llmCallLog = await llmCallLogRepository.GetByDoctorReportIdAsync(report.Id, ct);

            return await WriteJsonAsync(req, HttpStatusCode.OK, new { source = "live", hasReport = true, finding, report, llmCallLog });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GetFindingDoctorReport failed unexpectedly for finding {Id}", id);
            return await WriteJsonAsync(req, HttpStatusCode.InternalServerError, new { source = "error", error = "Unexpected server error." });
        }
    }

    private static async Task<HttpResponseData> WriteJsonAsync(HttpRequestData req, HttpStatusCode statusCode, object payload)
    {
        var response = req.CreateResponse(statusCode);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        response.Headers.Add("Cache-Control", "no-store");
        await response.WriteStringAsync(JsonSerializer.Serialize(payload, JsonOptions));
        return response;
    }
}
