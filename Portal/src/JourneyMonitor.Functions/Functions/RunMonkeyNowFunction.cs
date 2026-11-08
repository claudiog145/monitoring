using System.Net;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring.Kpi;
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
    IKpiIngestionRunner ingestionRunner,
    IPillarHealthMonitor pillarMonitor,
    IOptions<MonitoringOptions> monitoringOptions,
    ILogger<RunMonkeyNowFunction> logger,
    IEntityDetectionMonitor? entityMonitor = null)
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
            var spec1000Body = await RunSpec1000Async(id, ct);
            if (spec1000Body is not null)
            {
                return await WriteJsonAsync(req, HttpStatusCode.OK, spec1000Body);
            }

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

    /// <summary>
    /// Spec 1000 runners. They honour HealthMonitoring:Enabled themselves, so a manual run cannot
    /// bypass the flag. newFindings/diagnosed keep the Run Now toast working; result is the raw runner output.
    /// </summary>
    private async Task<object?> RunSpec1000Async(string id, CancellationToken ct)
    {
        switch (id)
        {
            case MonkeyKey.KpiIngestion:
            {
                var result = await ingestionRunner.RunAsync(ct);
                return new { monkeyId = id, newFindings = result.BucketsWritten, diagnosed = result.KpisProcessed, notified = 0, ranAt = DateTimeOffset.UtcNow.ToString("O"), result };
            }
            case MonkeyKey.ApplicationHealthMonitor:
                return await RunApplicationAsync(id, ct);
            case MonkeyKey.BusinessHealthMonitor:
                return await RunPillarAsync(id, KpiPillar.Business, ct);
            case MonkeyKey.InfrastructureHealthMonitor:
                return await RunPillarAsync(id, KpiPillar.Infrastructure, ct);
            default:
                return null;
        }
    }

    /// <summary>
    /// Same as the Application Health timer: the pillar monitor, then <see cref="IEntityDetectionMonitor"/>
    /// (contract v4 §3). A failure of either part does not prevent the other from running; a pillar failure
    /// is still rethrown (500) after the entity pass, an entity failure is logged and reported in the body.
    /// </summary>
    private async Task<object> RunApplicationAsync(string id, CancellationToken ct)
    {
        ExceptionDispatchInfo? pillarFailure = null;
        PillarMonitorRunResult? pillar = null;
        try
        {
            pillar = await pillarMonitor.RunAsync(KpiPillar.Application, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            pillarFailure = ExceptionDispatchInfo.Capture(ex);
        }

        EntityDetectionRunResult? entity = null;
        string? entityError = null;
        if (entityMonitor is not null)
        {
            try
            {
                entity = await entityMonitor.RunAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "RunMonkeyNow {Id}: entity detection failed; the pillar result is kept.", id);
                entityError = "Entity detection failed.";
            }
        }

        pillarFailure?.Throw();
        return new
        {
            monkeyId = id,
            newFindings = pillar!.Triggered,
            diagnosed = pillar.KpisEvaluated,
            notified = 0,
            ranAt = DateTimeOffset.UtcNow.ToString("O"),
            result = pillar,
            entityDetection = entity,
            entityDetectionError = entityError,
        };
    }

    private async Task<object> RunPillarAsync(string id, KpiPillar pillar, CancellationToken ct)
    {
        var result = await pillarMonitor.RunAsync(pillar, ct);
        return new { monkeyId = id, newFindings = result.Triggered, diagnosed = result.KpisEvaluated, notified = 0, ranAt = DateTimeOffset.UtcNow.ToString("O"), result };
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
