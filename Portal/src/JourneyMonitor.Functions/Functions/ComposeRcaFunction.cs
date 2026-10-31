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
/// POST /api/inspector/cases/{id}/rca — composes the publishable RCA for a finished investigation.
///
/// On demand rather than automatic: the Inspector closes cases that are nowhere near worth an RCA
/// (eight of the fifteen recorded cases escalated, none resolved), and writing one for each would
/// spend tokens producing documents nobody asked for. A person decides which incident deserves a
/// write-up.
///
/// Gated by the internal secret: the response carries the raw synthesis of the investigation and
/// the call costs a provider request.
/// </summary>
public sealed class ComposeRcaFunction(
    IRcaAgent rcaAgent,
    IForensicCaseRepository forensicCases,
    IRcaReportRepository rcaReports,
    IOptions<RcaOptions> rcaOptions,
    IOptions<MonitoringOptions> monitoringOptions,
    ILogger<ComposeRcaFunction> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Function("ComposeRca")]
    public async Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "inspector/cases/{id}/rca")] HttpRequestData req,
        string id,
        CancellationToken ct)
    {
        if (!SettingsWriteSecretGuard.HasMatchingSecret(req, monitoringOptions.Value.InternalApiSecret, SettingsWriteSecretGuard.InternalApiSecretHeaderName))
        {
            return await WriteJsonAsync(req, HttpStatusCode.Unauthorized, new { error = "Missing or invalid X-Internal-Api-Secret header." });
        }

        if (!rcaOptions.Value.IsEnabled)
        {
            return await WriteJsonAsync(req, HttpStatusCode.Conflict, new { error = "The RCA Agent is disabled (Rca:Enabled = false)." });
        }

        if (!Guid.TryParse(id, out var caseId))
        {
            return await WriteJsonAsync(req, HttpStatusCode.BadRequest, new { error = "id must be a GUID." });
        }

        try
        {
            var forensicCase = await forensicCases.GetByIdAsync(caseId, ct);
            if (forensicCase is null)
            {
                return await WriteJsonAsync(req, HttpStatusCode.NotFound, new { error = $"No investigation found with id {caseId}." });
            }

            // Composing from a half-finished investigation would describe an incident whose
            // analysis is still changing underneath it.
            if (forensicCase.CompletedAt is null)
            {
                return await WriteJsonAsync(req, HttpStatusCode.Conflict, new
                {
                    error = "This investigation has not finished yet. An RCA can only be written once the analysis is final.",
                });
            }

            var report = await rcaAgent.ComposeAsync(forensicCase, ct);

            if (!report.IsAvailable)
            {
                // A failed "Rewrite the RCA" must not destroy a previously published, successful
                // document - a transient provider blip is not consent to delete the old one. A
                // first attempt has nothing to protect, so its failure is still recorded: "we
                // tried and the provider returned nothing" is a fact about this incident.
                var existing = await rcaReports.GetByForensicCaseIdAsync(caseId, ct);
                if (existing is null)
                {
                    try
                    {
                        await rcaReports.SaveAsync(report, ct);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogWarning(ex, "Composed the RCA for case {CaseId} but could not persist the failed attempt.", caseId);
                    }
                }
                else
                {
                    logger.LogWarning("Rewrite of the RCA for case {CaseId} failed; keeping the previously published document instead of replacing it.", caseId);
                }

                // A composition that failed returns 502 with its reason rather than an empty-looking 200.
                return await WriteJsonAsync(req, HttpStatusCode.BadGateway, report);
            }

            try
            {
                await rcaReports.SaveAsync(report, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Composed the RCA for case {CaseId} but could not persist it.", caseId);
                // Composition succeeded but nothing durable happened - a 200 here would show the
                // browser a document the next GET cannot retrieve.
                return await WriteJsonAsync(req, HttpStatusCode.InternalServerError, new
                {
                    error = "The RCA was composed but could not be saved, so a later read will not return it.",
                    report,
                });
            }

            return await WriteJsonAsync(req, HttpStatusCode.OK, report);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ComposeRca failed for case {CaseId}", caseId);
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
