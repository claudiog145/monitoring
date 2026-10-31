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
/// GET /api/knowledge/search?q=...&amp;top=N — what the knowledge base returns for a query.
///
/// Exists so retrieval quality can be judged by reading real results instead of assumed from the
/// row count: a knowledge base that returns the wrong precedent confidently is worse than an empty
/// one, and nothing else in the system makes that visible.
///
/// Each hit reports its <c>sourceType</c>, because a reviewed RCA and an exported ticket carry
/// very different authority - one states a conclusion, the other a reported symptom.
///
/// Gated by the internal secret: a query embeds, which costs a provider call.
/// </summary>
public sealed class SearchKnowledgeFunction(
    IKnowledgeRetriever knowledgeRetriever,
    IOptions<MonitoringOptions> monitoringOptions,
    ILogger<SearchKnowledgeFunction> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private const int DefaultTop = 5;
    private const int MaxTop = 25;

    /// <summary>Enough to judge relevance without dumping whole documents into a diagnostic response.</summary>
    private const int SnippetChars = 400;

    [Function("SearchKnowledge")]
    public async Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "knowledge/search")] HttpRequestData req,
        CancellationToken ct)
    {
        if (!SettingsWriteSecretGuard.HasMatchingSecret(req, monitoringOptions.Value.InternalApiSecret, SettingsWriteSecretGuard.InternalApiSecretHeaderName))
        {
            return await WriteJsonAsync(req, HttpStatusCode.Unauthorized, new { error = "Missing or invalid X-Internal-Api-Secret header." });
        }

        var query = req.Query["q"];
        if (string.IsNullOrWhiteSpace(query))
        {
            return await WriteJsonAsync(req, HttpStatusCode.BadRequest, new { error = "q is required." });
        }

        var top = DefaultTop;
        if (int.TryParse(req.Query["top"], out var requested) && requested > 0) top = Math.Min(requested, MaxTop);

        // Absent means the whole corpus, which is what an operator debugging retrieval wants.
        // Pass the company to see exactly what that company's agents would get back.
        var profileKey = req.Query["profileKey"];
        if (string.IsNullOrWhiteSpace(profileKey)) profileKey = null;

        try
        {
            var hits = await knowledgeRetriever.RetrieveAsync(query, top, ct, profileKey);

            return await WriteJsonAsync(req, HttpStatusCode.OK, new
            {
                query,
                count = hits.Count,
                results = hits.Select(h => new
                {
                    sourceType = h.SourceType,
                    domain = h.Domain,
                    document = h.DocumentTitle,
                    section = h.SectionTitle,
                    reference = h.SourceRef,
                    embeddingModel = h.EmbeddingModel,
                    snippet = h.Content.Length > SnippetChars ? h.Content[..SnippetChars] : h.Content,
                }),
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "SearchKnowledge failed");
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
