using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;

namespace JourneyMonitor.Core.Monitoring.Evidence;

/// <summary>Up to five knowledge-base chunks retrieved for the signal's text - precedents the triage can cite, scoped to the signal's profile plus the shared corpus.</summary>
public sealed class KnowledgeEvidenceProvider(IKnowledgeRetriever retriever) : IEvidenceProvider
{
    public const int MaxChunks = 5;
    private const int MaxContentLength = 1000;

    public string Dimension => TriageDimensions.KnowledgeBase;

    public async Task<EvidenceSection> CollectAsync(TriageSignal signal, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken ct = default)
    {
        var query = string.Join(' ', new[] { signal.Pillar, signal.Kpi, signal.Domain, signal.Stage, signal.Detection }
            .Where(part => !string.IsNullOrWhiteSpace(part)));

        var chunks = await retriever.RetrieveAsync(query, MaxChunks, ct, EvidenceProfile.KeyOf(signal));
        var data = EvidenceJson.ToElement(new
        {
            Query = query,
            Chunks = chunks.Take(MaxChunks).Select(c => new
            {
                c.DocumentTitle,
                c.SectionTitle,
                c.SourceType,
                c.SourceRef,
                c.Domain,
                Content = c.Content.Length <= MaxContentLength ? c.Content : c.Content[..MaxContentLength],
            }).ToList(),
        });
        return new EvidenceSection(Dimension, true, data, null);
    }
}
