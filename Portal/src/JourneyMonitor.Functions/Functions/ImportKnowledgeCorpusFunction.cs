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
/// POST /api/knowledge/import — loads the converted RCA/incident corpus into the knowledge base.
///
/// Deliberately a manual endpoint and NOT a timer: one run makes roughly two thousand embedding
/// calls, which should be something a person decides to do, not something that happens overnight
/// by itself. It is also idempotent - a second run skips everything already stored by content
/// hash, so re-running after a partial failure costs only the missing chunks.
///
/// Gated by <see cref="MonitoringOptions.InternalApiSecret"/> (fails CLOSED, see
/// SettingsWriteSecretGuard): like every Function here it is Anonymous, but this one spends real
/// money on the configured LLM provider, so reaching it directly must not be enough.
/// </summary>
public sealed class ImportKnowledgeCorpusFunction(
    KnowledgeImportService importService,
    IOptions<KnowledgeImportOptions> importOptions,
    IOptions<MonitoringOptions> monitoringOptions,
    ILogger<ImportKnowledgeCorpusFunction> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Function("ImportKnowledgeCorpus")]
    public async Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "knowledge/import")] HttpRequestData req,
        CancellationToken ct)
    {
        if (!SettingsWriteSecretGuard.HasMatchingSecret(req, monitoringOptions.Value.InternalApiSecret, SettingsWriteSecretGuard.InternalApiSecretHeaderName))
        {
            return await WriteJsonAsync(req, HttpStatusCode.Unauthorized, new { error = "Missing or invalid X-Internal-Api-Secret header. Monitoring:InternalApiSecret must be configured for this endpoint to respond." });
        }

        // ?max=N keeps a first run small enough to measure cost before committing to the full corpus.
        var max = importOptions.Value.ResolvedMaxDocumentsPerRun;
        if (int.TryParse(req.Query["max"], out var requested) && requested > 0)
        {
            max = Math.Min(requested, max);
        }

        // ?types=rca_document,incident_ticket - lets the high-signal RCAs be loaded and evaluated
        // before paying for the 185 tickets.
        var types = req.Query["types"];
        IReadOnlySet<string>? onlyTypes = null;
        if (!string.IsNullOrWhiteSpace(types))
        {
            var requestedTypes = types.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(t => t.ToLowerInvariant())
                .ToHashSet(StringComparer.Ordinal);

            var unknown = requestedTypes.Except(KnowledgeSourceType.ImportedTypes, StringComparer.Ordinal).ToList();
            if (unknown.Count > 0)
            {
                return await WriteJsonAsync(req, HttpStatusCode.BadRequest, new
                {
                    error = $"Unknown document type(s): {string.Join(", ", unknown)}. Valid values: {string.Join(", ", KnowledgeSourceType.ImportedTypes)}.",
                });
            }

            onlyTypes = requestedTypes;
        }

        // ?profileKey=acme attributes the corpus to one company. Absent stores it as SHARED, which
        // is right for generic runbooks and wrong for a customer's incident history - so the choice
        // is the operator's, made per import, rather than a default nobody saw.
        var profileKey = req.Query["profileKey"];
        if (string.IsNullOrWhiteSpace(profileKey)) profileKey = null;

        try
        {
            var result = await importService.ImportAsync(max, onlyTypes, profileKey, ct);

            // A corpus that is not mounted is a configuration problem, not an empty import - 409
            // so it cannot be mistaken for "ran fine, found nothing".
            var status = result.SourceAvailable ? HttpStatusCode.OK : HttpStatusCode.Conflict;
            return await WriteJsonAsync(req, status, result);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ImportKnowledgeCorpus failed");
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
