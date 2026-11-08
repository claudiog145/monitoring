using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring.Evidence;
using JourneyMonitor.Infrastructure.Fabric;
using Microsoft.Extensions.Logging.Abstractions;

namespace JourneyMonitor.Tests.Spec1000.A5;

public class A5EvidenceProviderTests
{
    private static readonly DateTimeOffset From = A5TriageFixtures.DetectedAt.AddHours(-3);
    private static readonly DateTimeOffset To = A5TriageFixtures.DetectedAt.AddMinutes(15);

    [Fact]
    public async Task PhaseThreeProviders_AreUnavailableWithReason()
    {
        IEvidenceProvider[] providers = [new InfrastructureEvidenceProvider(), new CodeReliabilityEvidenceProvider(), new RecentChangesEvidenceProvider()];

        foreach (var provider in providers)
        {
            var section = await provider.CollectAsync(A5TriageFixtures.Signal(), From, To);
            Assert.Equal(provider.Dimension, section.Dimension);
            Assert.False(section.Available);
            Assert.Null(section.Data);
            Assert.Equal("not implemented (Fase 3)", section.UnavailableReason);
        }
        Assert.Equal(
            [TriageDimensions.InfrastructureHealth, TriageDimensions.CodeReliability, TriageDimensions.RecentChanges],
            providers.Select(p => p.Dimension).ToArray());
    }

    [Fact]
    public async Task KnowledgeProvider_ReturnsAtMostFiveChunks()
    {
        var retriever = new A5KnowledgeRetriever(8);
        var section = await new KnowledgeEvidenceProvider(retriever).CollectAsync(A5TriageFixtures.Signal(), From, To);

        Assert.True(section.Available);
        Assert.Equal(TriageDimensions.KnowledgeBase, section.Dimension);
        Assert.Equal(5, retriever.LastTopK);
        Assert.Contains("Latency", retriever.LastQuery);
        Assert.Equal(5, section.Data!.Value.GetProperty("chunks").GetArrayLength());
    }

    [Fact]
    public async Task ApplicationProvider_ReadsDomainKpiBucketsAndIncidentsInWindow()
    {
        var definitions = new A5KpiDefinitionStore(
            Definition(1, "hotels"),
            Definition(2, "air"));
        var buckets = new A5KpiBucketStore();
        var incidents = new A5IncidentReader();

        var section = await new ApplicationEvidenceProvider(definitions, buckets, incidents, NullLogger<ApplicationEvidenceProvider>.Instance)
            .CollectAsync(A5TriageFixtures.Signal("hotels"), From, To);

        Assert.True(section.Available);
        var data = section.Data!.Value;
        var kpi = Assert.Single(data.GetProperty("kpis").EnumerateArray());
        Assert.Equal(1, kpi.GetProperty("kpiDefinitionId").GetInt32());
        Assert.Equal([(1, From, To)], buckets.Requests);
        Assert.Equal("hotels", incidents.LastQuery!.Domain);
        Assert.Equal(From, incidents.LastQuery.Since);
        Assert.Equal(1, data.GetProperty("incidents").GetArrayLength());
    }

    [Fact]
    public async Task FabricBusinessProvider_NonHotelsDomain_IsUnavailable()
    {
        var source = new A5HotelKpiSource();
        var section = await new FabricBusinessEvidenceProvider(source, NullLogger<FabricBusinessEvidenceProvider>.Instance)
            .CollectAsync(A5TriageFixtures.Signal("air"), From, To);

        Assert.False(section.Available);
        Assert.Equal(TriageDimensions.BusinessHealth, section.Dimension);
        Assert.Equal("no business source for air", section.UnavailableReason);
        Assert.Equal(0, source.Calls);
    }

    [Fact]
    public async Task FabricBusinessProvider_HotelsWithoutRows_IsUnavailable()
    {
        var section = await new FabricBusinessEvidenceProvider(new A5HotelKpiSource(), NullLogger<FabricBusinessEvidenceProvider>.Instance)
            .CollectAsync(A5TriageFixtures.Signal("hotels"), From, To);

        Assert.False(section.Available);
        Assert.NotNull(section.UnavailableReason);
    }

    private static KpiDefinition Definition(int id, string domain) => new(
        id, KpiPillar.Application, "Latency", domain, "", "appinsights.latency", KpiDirection.Increase, KpiAggregation.P95,
        TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5), TimeSpan.FromHours(1), 100, 10,
        DetectionMethodKind.StaticThreshold, """{"threshold":3000}""", 2, 60, true, A5TriageFixtures.Now, "test");
}

internal sealed class A5KnowledgeRetriever(int available) : IKnowledgeRetriever
{
    public string? LastQuery { get; private set; }

    public int LastTopK { get; private set; }

    public string? LastProfileKey { get; private set; }

    public Task<IReadOnlyList<KnowledgeChunk>> RetrieveAsync(string query, int topK, CancellationToken ct = default, string? profileKey = null)
    {
        LastQuery = query;
        LastTopK = topK;
        LastProfileKey = profileKey;
        IReadOnlyList<KnowledgeChunk> chunks = Enumerable.Range(0, available).Select(i => new KnowledgeChunk
        {
            SourceType = "rca",
            Content = $"chunk {i}",
            Embedding = [],
            EmbeddingModel = KnowledgeChunk.NoEmbeddingModel,
            DocumentTitle = $"doc {i}",
        }).ToList();
        return Task.FromResult(chunks);
    }

    public Task IndexAsync(KnowledgeChunk chunk, CancellationToken ct = default, string? profileKey = null) => Task.CompletedTask;

    public Task<IReadOnlySet<string>> GetIndexedHashesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());

    public Task<IReadOnlyList<KnowledgeDocumentSummary>> GetIndexedDocumentsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<KnowledgeDocumentSummary>>([]);

    public Task<IReadOnlyList<KnowledgeDocumentSection>> GetDocumentSectionsAsync(string documentTitle, string? sourceType, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<KnowledgeDocumentSection>>([]);
}

internal sealed class A5KpiDefinitionStore(params KpiDefinition[] definitions) : IKpiDefinitionStore
{
    public Task<IReadOnlyList<KpiDefinition>> GetAllAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<KpiDefinition>>(definitions);

    public Task<IReadOnlyList<KpiDefinition>> GetEnabledAsync(KpiPillar? pillar, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<KpiDefinition>>(definitions.Where(d => d.Enabled && (pillar is null || d.Pillar == pillar)).ToList());

    public Task<KpiDefinition?> GetAsync(int kpiDefinitionId, CancellationToken ct = default) =>
        Task.FromResult(definitions.FirstOrDefault(d => d.KpiDefinitionId == kpiDefinitionId));

    public Task<KpiDefinition> UpsertAsync(KpiDefinition definition, string changedBy, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<IReadOnlyList<KpiDefinitionAuditEntry>> GetAuditAsync(int kpiDefinitionId, int top, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<KpiDefinitionAuditEntry>>([]);
}

internal sealed class A5KpiBucketStore : IKpiBucketStore
{
    public List<(int Id, DateTimeOffset From, DateTimeOffset To)> Requests { get; } = [];

    public Task UpsertAsync(IReadOnlyList<KpiBucket> buckets, CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<KpiBucket>> GetRangeAsync(int kpiDefinitionId, DateTimeOffset fromInclusive, DateTimeOffset toExclusive, CancellationToken ct = default)
    {
        Requests.Add((kpiDefinitionId, fromInclusive, toExclusive));
        IReadOnlyList<KpiBucket> buckets = [new KpiBucket(kpiDefinitionId, fromInclusive, 120, 100, 900, 4200, 1100, 300, null)];
        return Task.FromResult(buckets);
    }
}

internal sealed class A5IncidentReader : IIncidentReader
{
    public IncidentReadQuery? LastQuery { get; private set; }

    public Task<IReadOnlyList<IncidentRow>> ListAsync(IncidentReadQuery query, CancellationToken cancellationToken)
    {
        LastQuery = query;
        IReadOnlyList<IncidentRow> rows =
        [
            new IncidentRow(
                "ext-1", "hotels", "club-1", "production", "System.TimeoutException", "booking", "POST /confirm",
                "Timed out", null, "high", "open", null, A5TriageFixtures.DetectedAt.AddMinutes(-30), A5TriageFixtures.DetectedAt,
                40, 12, null, null, [], []),
        ];
        return Task.FromResult(rows);
    }

    public Task<IReadOnlyDictionary<string, int>> CountByDomainAsync(IncidentCountQuery query, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<string, int>>(new Dictionary<string, int>());
}

internal sealed class A5HotelKpiSource : IHotelBookingKpiSource
{
    public int Calls { get; private set; }

    public Task<IReadOnlyList<HotelBookingKpiResponse>> ReadAlertStatusAsync(CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult<IReadOnlyList<HotelBookingKpiResponse>>([]);
    }
}
