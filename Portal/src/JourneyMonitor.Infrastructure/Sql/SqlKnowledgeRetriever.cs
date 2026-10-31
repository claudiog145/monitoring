using System.Data;
using System.Text.Json;
using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Options;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Dapper;

namespace JourneyMonitor.Infrastructure.Sql;

/// <summary>
/// SQL-backed IKnowledgeRetriever (monitoring.KnowledgeChunk) - survives process/container
/// restarts, unlike InMemoryKnowledgeRetriever. Still no vector DB engine: every chunk is loaded
/// and compared with in-process cosine similarity, same MVP-scope decision as the in-memory
/// version, just durable now.
/// </summary>
public sealed class SqlKnowledgeRetriever(ILlmClient llmClient, IOptions<JourneyStoreOptions> storeOptions) : IKnowledgeRetriever
{
    public async Task IndexAsync(KnowledgeChunk chunk, CancellationToken ct = default, string? profileKey = null)
    {
        var parameters = new DynamicParameters();
        parameters.Add("Id", chunk.Id);
        parameters.Add("SourceType", chunk.SourceType, DbType.String, size: 64);
        parameters.Add("SourceRef", chunk.SourceRef, DbType.String, size: 400);
        parameters.Add("Content", chunk.Content, DbType.String, size: -1);
        parameters.Add("EmbeddingJson", JsonSerializer.Serialize(chunk.Embedding), DbType.String, size: -1);
        parameters.Add("EmbeddingModel", chunk.EmbeddingModel, DbType.String, size: 128);
        parameters.Add("Domain", chunk.Domain, DbType.String, size: 64);
        parameters.Add("DocumentTitle", chunk.DocumentTitle, DbType.String, size: 400);
        parameters.Add("SectionTitle", chunk.SectionTitle, DbType.String, size: 200);
        parameters.Add("ContentHash", chunk.ContentHash, DbType.String, size: 64);
        // Blank is the shared corpus, which is what an unscoped caller means: a chunk nobody
        // scoped is readable by everyone by definition.
        parameters.Add("ProfileKey", string.IsNullOrWhiteSpace(profileKey) ? "" : profileKey.Trim(), DbType.String, size: 128);
        parameters.Add("CreatedAt", chunk.CreatedAt);

        await using var connection = OpenConnection();
        await connection.ExecuteAsync(new CommandDefinition(
            "monitoring.usp_AddKnowledgeChunk", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));
    }

    public async Task<IReadOnlySet<string>> GetIndexedHashesAsync(CancellationToken ct = default)
    {
        await using var connection = OpenConnection();
        var hashes = await connection.QueryAsync<string>(new CommandDefinition(
            "monitoring.usp_GetKnowledgeChunkHashes", commandType: CommandType.StoredProcedure, cancellationToken: ct));

        return hashes.Where(h => !string.IsNullOrEmpty(h)).ToHashSet(StringComparer.Ordinal);
    }

    public async Task<IReadOnlyList<KnowledgeDocumentSummary>> GetIndexedDocumentsAsync(CancellationToken ct = default)
    {
        await using var connection = OpenConnection();
        var rows = await connection.QueryAsync<KnowledgeDocumentSummary>(new CommandDefinition(
            "monitoring.usp_GetKnowledgeDocuments",
            new
            {
                UnknownEmbeddingModel = KnowledgeChunk.UnknownEmbeddingModel,
                NoEmbeddingModel = KnowledgeChunk.NoEmbeddingModel,
            },
            commandType: CommandType.StoredProcedure,
            cancellationToken: ct));

        return rows.ToList();
    }

    public async Task<IReadOnlyList<KnowledgeDocumentSection>> GetDocumentSectionsAsync(string documentTitle, string? sourceType, CancellationToken ct = default)
    {
        var parameters = new DynamicParameters();
        parameters.Add("DocumentTitle", documentTitle, DbType.String, size: 400);
        parameters.Add("SourceType", string.IsNullOrWhiteSpace(sourceType) ? null : sourceType, DbType.String, size: 64);

        await using var connection = OpenConnection();
        var rows = await connection.QueryAsync<KnowledgeDocumentSection>(new CommandDefinition(
            "monitoring.usp_GetKnowledgeChunksByDocument", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));

        return rows.ToList();
    }

    public async Task<IReadOnlyList<KnowledgeChunk>> RetrieveAsync(string query, int topK, CancellationToken ct = default, string? profileKey = null)
    {
        var all = await GetAllChunksAsync(profileKey, ct);
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

    /// <summary>
    /// The company scope is applied in the query, never after it: filtering the shortlist in memory
    /// would mean another customer's documents had already been read out of the database.
    /// </summary>
    private async Task<List<KnowledgeChunk>> GetAllChunksAsync(string? profileKey, CancellationToken ct)
    {
        var parameters = new DynamicParameters();
        parameters.Add("ProfileKey", string.IsNullOrWhiteSpace(profileKey) ? null : profileKey.Trim(), DbType.String, size: 128);

        await using var connection = OpenConnection();
        var rows = await connection.QueryAsync<KnowledgeChunkRow>(new CommandDefinition(
            "monitoring.usp_GetAllKnowledgeChunks", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));
        return rows.Select(r => r.ToDomain()).ToList();
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

    private SqlConnection OpenConnection()
    {
        var options = storeOptions.Value;
        if (!options.IsConfigured)
        {
            throw new InvalidOperationException($"{JourneyStoreOptions.SectionName}:ConnectionString is not configured.");
        }
        return new SqlConnection(options.ConnectionString);
    }

    private sealed record KnowledgeChunkRow(
        Guid Id,
        string SourceType,
        string? SourceRef,
        string Content,
        string EmbeddingJson,
        string? EmbeddingModel,
        string? Domain,
        string? DocumentTitle,
        string? SectionTitle,
        string? ContentHash,
        DateTimeOffset CreatedAt)
    {
        public KnowledgeChunk ToDomain() => new()
        {
            Id = Id,
            SourceType = SourceType,
            SourceRef = SourceRef,
            Content = Content,
            Embedding = JsonSerializer.Deserialize<float[]>(EmbeddingJson) ?? [],
            // Rows written before the column existed are NULL: mark them unknown so they never
            // match a live model, instead of defaulting them to whatever model is current now.
            EmbeddingModel = string.IsNullOrWhiteSpace(EmbeddingModel) ? KnowledgeChunk.UnknownEmbeddingModel : EmbeddingModel,
            Domain = Domain,
            DocumentTitle = DocumentTitle,
            SectionTitle = SectionTitle,
            ContentHash = ContentHash,
            CreatedAt = CreatedAt,
        };
    }
}
