using System.Net;
using System.Text.Json;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Options;
using JourneyMonitor.Core.UseCases;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Functions.Functions;

/// <summary>
/// POST /api/monkeys/{id}/run — a genuine on-demand trigger for a monkey's real cycle (the
/// Simian Army tab's "Run Now" button used to be 100% simulated, see docs/sessions 2026-09-30).
/// Calls the exact same Core runner the matching Timer trigger uses
/// (<see cref="LatencySpikeMonkeyRunner"/>/<see cref="SloBurnRateMonkeyRunner"/>/
/// <see cref="DoctorMonkeyRunner"/>), so a manual run has identical behavior to a scheduled one -
/// real LLM diagnoses and, for the SLO Burn-Rate Monkey, real Teams/Email notifications. Gated by
/// X-Internal-Api-Secret (same fail-closed pattern as GetFindingDoctorReport/ListLlmClientModels -
/// this is, if anything, more sensitive: it triggers real outbound calls with a real cost, not
/// just a read).
/// </summary>
public sealed class RunMonkeyNowFunction(
    LatencySpikeMonkeyRunner latencyRunner,
    SloBurnRateMonkeyRunner sloRunner,
    DoctorMonkeyRunner doctorRunner,
    ScoutMonkeyRunner scoutRunner,
    InspectorMonkeyRunner inspectorRunner,
    IOptions<MonitoringOptions> monitoringOptions,
    ILogger<RunMonkeyNowFunction> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Function("RunMonkeyNow")]
    public async Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "monkeys/{id}/run")] HttpRequestData req,
        string id,
        CancellationToken ct)
    {
        var requiredSecret = monitoringOptions.Value.InternalApiSecret;
        if (!SettingsWriteSecretGuard.HasMatchingSecret(req, requiredSecret, SettingsWriteSecretGuard.InternalApiSecretHeaderName))
        {
            return await WriteJsonAsync(req, HttpStatusCode.Unauthorized, new { error = "Missing or invalid X-Internal-Api-Secret header. Monitoring:InternalApiSecret must be configured for this endpoint to accept requests." });
        }

        try
        {
            MonkeyRunSummary? summary = id switch
            {
                MonkeyKey.LatencySpikeMonkey => await latencyRunner.RunAsync(ct),
                MonkeyKey.SloBurnRateMonkey => await sloRunner.RunAsync(ct),
                MonkeyKey.DoctorMonkey => await doctorRunner.RunAsync(ct),
                MonkeyKey.LatencyScoutMonkey => await scoutRunner.RunAsync(ScoutBoard.Latency, ct),
                MonkeyKey.ExceptionScoutMonkey => await scoutRunner.RunAsync(ScoutBoard.Exception, ct),
                // Honours Inspector:Enabled and the daily cap exactly like the timer does, so a
                // manual click cannot be used to spend past the configured ceiling.
                MonkeyKey.InspectorMonkey => await inspectorRunner.RunAsync(ct),
                _ => null,
            };

            if (summary is null)
            {
                return await WriteJsonAsync(req, HttpStatusCode.NotFound, new { error = $"Unknown or non-runnable monkey id '{id}'." });
            }

            return await WriteJsonAsync(req, HttpStatusCode.OK, new
            {
                monkeyId = summary.MonkeyKey,
                newFindings = summary.NewFindings,
                diagnosed = summary.Diagnosed,
                notified = summary.Notified,
                ranAt = summary.RanAt.ToString("O"),
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "RunMonkeyNow failed for monkey {Id}", id);
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
