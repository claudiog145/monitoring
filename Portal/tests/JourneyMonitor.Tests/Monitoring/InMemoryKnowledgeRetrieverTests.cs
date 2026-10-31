using JourneyMonitor.Core.Domain;
using JourneyMonitor.Infrastructure.Monitoring;

namespace JourneyMonitor.Tests.Monitoring;

public class InMemoryKnowledgeRetrieverTests
{
    private static KnowledgeChunk Chunk(float[] embedding, string content = "precedent", string embeddingModel = "fake-embedding-model") => new()
    {
        SourceType = KnowledgeSourceType.FindingHistory,
        Content = content,
        Embedding = embedding,
        EmbeddingModel = embeddingModel,
    };

    [Fact]
    public async Task RetrieveAsyncFiltersOutNonPositiveSimilarityScores()
    {
        // Orthogonal to the query embedding -> cosine similarity 0 - not a real precedent, just noise.
        var retriever = new InMemoryKnowledgeRetriever(new FakeLlmClient("unused", embedding: [1f, 0f]));
        await retriever.IndexAsync(Chunk([0f, 1f]), CancellationToken.None);

        var results = await retriever.RetrieveAsync("query", topK: 5, CancellationToken.None);

        Assert.Empty(results);
    }

    [Fact]
    public async Task RetrieveAsyncReturnsChunksWithPositiveSimilarity()
    {
        var retriever = new InMemoryKnowledgeRetriever(new FakeLlmClient("unused", embedding: [1f, 0f]));
        await retriever.IndexAsync(Chunk([1f, 0f]), CancellationToken.None);

        var results = await retriever.RetrieveAsync("query", topK: 5, CancellationToken.None);

        Assert.Single(results);
    }

    [Fact]
    public async Task RetrieveAsyncFallsBackToKeywordMatchWhenEmbeddingsAreUnavailable()
    {
        var retriever = new InMemoryKnowledgeRetriever(new FakeLlmClient("unused", embedding: null));
        await retriever.IndexAsync(Chunk([1f, 0f], content: "checkout timeout precedent"), CancellationToken.None);

        var results = await retriever.RetrieveAsync("checkout timeout", topK: 5, CancellationToken.None);

        Assert.Single(results);
    }
}
