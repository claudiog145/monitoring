using System.Collections.Concurrent;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;

namespace JourneyMonitor.Infrastructure.Monitoring;

/// <summary>
/// Lightweight knowledge base retriever for the MVP: no vector database, just an in-process list
/// of <see cref="KnowledgeChunk"/> compared by cosine similarity. This is a deliberate scope
/// decision (see the implementation plan, "MVP liviano") — acceptable while the corpus is small
/// and self-bootstrapped from this app's own past Doctor reports. If the embedding service is
/// unavailable, falls back to a simple case-insensitive keyword match rather than failing outright
/// (still honest: returns fewer/no results instead of fabricating relevance).
/// </summary>
public sealed class InMemoryKnowledgeRetriever(ILlmClient llmClient) : IKnowledgeRetriever
{
    private readonly ConcurrentBag<StoredChunk> _chunks = new();

    /// <summary>The scope rides alongside the chunk because <see cref="KnowledgeChunk"/> itself carries no company - see the note in the report accompanying this change.</summary>
    private sealed record StoredChunk(KnowledgeChunk Chunk, string ProfileKey);

    private IEnumerable<KnowledgeChunk> AllChunks => _chunks.Select(s => s.Chunk);

    public Task IndexAsync(KnowledgeChunk chunk, CancellationToken ct = default, string? profileKey = null)
    {
        // Blank is the shared corpus, same meaning as N'' in monitoring.KnowledgeChunk.
        _chunks.Add(new StoredChunk(chunk, string.IsNullOrWhiteSpace(profileKey) ? "" : profileKey.Trim()));
        return Task.CompletedTask;
    }

    public Task<IReadOnlySet<string>> GetIndexedHashesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlySet<string>>(
            AllChunks.Select(c => c.ContentHash).Where(h => !string.IsNullOrEmpty(h)).Select(h => h!).ToHashSet(StringComparer.Ordinal));

    public Task<IReadOnlyList<KnowledgeDocumentSummary>> GetIndexedDocumentsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<KnowledgeDocumentSummary>>(
            AllChunks
                .GroupBy(c => (Title: string.IsNullOrWhiteSpace(c.DocumentTitle) ? "(untitled)" : c.DocumentTitle!, c.SourceType))
                .Select(g => new KnowledgeDocumentSummary(
                    g.Key.Title,
                    g.Key.SourceType,
                    g.Select(c => c.Domain).FirstOrDefault(d => !string.IsNullOrWhiteSpace(d)) ?? "",
                    g.Count(),
                    g.Count(c => KnowledgeChunk.HasComparableEmbedding(c.EmbeddingModel)),
                    g.Max(c => c.CreatedAt)))
                .OrderByDescending(d => d.IndexedAt)
                .ToList());

    public Task<IReadOnlyList<KnowledgeDocumentSection>> GetDocumentSectionsAsync(string documentTitle, string? sourceType, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<KnowledgeDocumentSection>>(
            AllChunks
                .Where(c => (string.IsNullOrWhiteSpace(c.DocumentTitle) ? "(untitled)" : c.DocumentTitle) == documentTitle)
                .Where(c => string.IsNullOrWhiteSpace(sourceType) || c.SourceType == sourceType)
                .OrderBy(c => c.CreatedAt)
                .Select(c => new KnowledgeDocumentSection(c.SectionTitle ?? "", c.Content, c.SourceRef ?? ""))
                .ToList());

    public async Task<IReadOnlyList<KnowledgeChunk>> RetrieveAsync(string query, int topK, CancellationToken ct = default, string? profileKey = null)
    {
        // Same semantics as the SQL retriever, enforced before anything is scored: the company's
        // own chunks plus the shared corpus (""), never another company's. A fake that skipped this
        // would make an isolation test pass over a real leak.
        var scope = string.IsNullOrWhiteSpace(profileKey) ? null : profileKey.Trim();
        var all = _chunks
            .Where(s => scope is null || s.ProfileKey == scope || s.ProfileKey.Length == 0)
            .Select(s => s.Chunk)
            .ToList();
        if (all.Count == 0) return [];

        var queryEmbedding = await llmClient.GetEmbeddingAsync(query, ct);

        // Only chunks embedded by the SAME model are comparable - see KnowledgeChunk.EmbeddingModel.
        // When a provider/model switch leaves nothing comparable, fall through to the keyword path
        // rather than return an empty result that reads as "the KB knows nothing about this".
        var comparable = queryEmbedding.IsAvailable
            ? all.Where(c => string.Equals(c.EmbeddingModel, queryEmbedding.Model, StringComparison.OrdinalIgnoreCase)).ToList()
            : [];

        if (comparable.Count > 0)
        {
            return comparable
                .Select(c => (Chunk: c, Score: CosineSimilarity(queryEmbedding.Vector!, c.Embedding)))
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .Take(topK)
                .Select(x => x.Chunk)
                .ToList();
        }

        // Fallback: embeddings unavailable or no chunk shares this model — cheap keyword overlap
        // instead of failing the whole retrieval.
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return all
            .Select(c => (Chunk: c, Score: terms.Count(t => c.Content.Contains(t, StringComparison.OrdinalIgnoreCase))))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Take(topK)
            .Select(x => x.Chunk)
            .ToList();
    }

    private static double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0) return 0;
        double dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        if (normA == 0 || normB == 0) return 0;
        return dot / (Math.Sqrt(normA) * Math.Sqrt(normB));
    }
}
