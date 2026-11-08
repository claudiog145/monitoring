using System.Net;
using System.Text.Json;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Monitoring.Kpi;
using JourneyMonitor.Core.Options;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JourneyMonitor.Functions.Functions;

/// <summary>
/// GET /api/knowledge/documents — what the knowledge base actually holds, one row per document.
///
/// The corpus import reports how many chunks it created and then disappears from view: nothing in
/// the portal could answer "did the document I expected get in?". A row count in the database is
/// not an answer, because the question is about a named document, not a total.
///
/// Reports <c>chunksWithEmbedding</c> alongside <c>chunks</c> so a partially embedded import is
/// visible as such. A document present but unembedded is reachable only by keyword, which reads
/// to a user as the knowledge base having forgotten it.
///
/// Unlike the search endpoint this costs no provider call - it never embeds, and never selects
/// chunk content - but it is gated the same way, because the titles alone name real incidents.
/// </summary>
public sealed class GetKnowledgeDocumentsFunction(
    IKnowledgeRetriever knowledgeRetriever,
    IOptions<MonitoringOptions> monitoringOptions,
    ILogger<GetKnowledgeDocumentsFunction> logger,
    KpiProfileResolver? profileResolver = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Contract v6 §5: the company's documents plus the shared corpus. Missing <c>?profileKey=</c> → the
    /// active company, "*" → the whole corpus.
    /// </summary>
    private Task<(ProfileReadScope? Scope, string? Error)> ReadScopeAsync(HttpRequestData req, CancellationToken ct) =>
        ProfileReadScope.ResolveAsync(profileResolver, req.Query["profileKey"], ct);

    [Function("GetKnowledgeDocuments")]
    public async Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "knowledge/documents")] HttpRequestData req,
        CancellationToken ct)
    {
        if (!SettingsWriteSecretGuard.HasMatchingSecret(req, monitoringOptions.Value.InternalApiSecret, SettingsWriteSecretGuard.InternalApiSecretHeaderName))
        {
            return await WriteJsonAsync(req, HttpStatusCode.Unauthorized, new { error = "Missing or invalid X-Internal-Api-Secret header." });
        }

        try
        {
            var (scope, error) = await ReadScopeAsync(req, ct);
            if (scope is null) return await WriteJsonAsync(req, HttpStatusCode.BadRequest, new { error });

            var documents = await knowledgeRetriever.GetIndexedDocumentsForProfileAsync(scope.ProfileKey, ct);

            return await WriteJsonAsync(req, HttpStatusCode.OK, new
            {
                profileKey = scope.Label,
                documents = documents.Count,
                chunks = documents.Sum(d => d.Chunks),
                chunksWithEmbedding = documents.Sum(d => d.ChunksWithEmbedding),
                byType = documents
                    .GroupBy(d => d.SourceType)
                    .OrderByDescending(g => g.Count())
                    .Select(g => new { sourceType = g.Key, documents = g.Count(), chunks = g.Sum(d => d.Chunks) }),
                items = documents.Select(d => new
                {
                    title = d.DocumentTitle,
                    sourceType = d.SourceType,
                    domain = d.Domain,
                    chunks = d.Chunks,
                    chunksWithEmbedding = d.ChunksWithEmbedding,
                    indexedAt = d.IndexedAt.ToString("O"),
                }),
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GetKnowledgeDocuments failed");
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

    /// <summary>
    /// GET /api/knowledge/document?title=...&amp;type=... — the indexed text of one document.
    ///
    /// Returns the passages as stored rather than a reassembled file. Consecutive passages overlap
    /// on purpose, so concatenating them would silently repeat sentences and misrepresent the
    /// document; showing them separately is what the agent actually reads.
    /// </summary>
    [Function("GetKnowledgeDocument")]
    public async Task<HttpResponseData> RunDocumentAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "knowledge/document")] HttpRequestData req,
        CancellationToken ct)
    {
        if (!SettingsWriteSecretGuard.HasMatchingSecret(req, monitoringOptions.Value.InternalApiSecret, SettingsWriteSecretGuard.InternalApiSecretHeaderName))
        {
            return await WriteJsonAsync(req, HttpStatusCode.Unauthorized, new { error = "Missing or invalid X-Internal-Api-Secret header." });
        }

        var title = req.Query["title"];
        if (string.IsNullOrWhiteSpace(title))
        {
            return await WriteJsonAsync(req, HttpStatusCode.BadRequest, new { error = "title is required." });
        }

        try
        {
            var (scope, error) = await ReadScopeAsync(req, ct);
            if (scope is null) return await WriteJsonAsync(req, HttpStatusCode.BadRequest, new { error });

            // Another company's document does not exist for this one.
            var sections = await knowledgeRetriever.GetDocumentSectionsForProfileAsync(title, req.Query["type"], scope.ProfileKey, ct);
            if (sections.Count == 0)
            {
                return await WriteJsonAsync(req, HttpStatusCode.NotFound, new { error = "No indexed document with that title." });
            }

            return await WriteJsonAsync(req, HttpStatusCode.OK, new
            {
                title,
                sections = sections.Count,
                characters = sections.Sum(s => s.Content.Length),
                reference = sections[0].SourceRef,
                items = sections.Select(s => new { section = s.SectionTitle, content = s.Content }),
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GetKnowledgeDocument failed");
            return await WriteJsonAsync(req, HttpStatusCode.InternalServerError, new { error = "Unexpected server error." });
        }
    }
}
