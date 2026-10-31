using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using Microsoft.Extensions.Logging;

namespace JourneyMonitor.Core.UseCases;

/// <summary>
/// What an import run actually did. Every number is reported, including the unflattering ones:
/// an import that quietly skipped half the corpus and reported success is worse than one that
/// failed outright, because nobody goes looking for the missing precedents.
/// </summary>
public sealed record KnowledgeImportResult(
    bool SourceAvailable,
    string Location,
    int DocumentsRead,
    int DocumentsSkippedByType,
    int ChunksCreated,
    int ChunksSkippedAsDuplicate,
    int ChunksIndexedWithoutEmbedding,
    int ControlCharactersRemoved,
    IReadOnlyDictionary<string, int> DocumentsByType,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Imports the converted RCA/incident corpus into the knowledge base so the agents have
/// precedents written by people, instead of only their own past output.
///
/// This closes a gap recorded on 2026-09-23: the KB was "self-bootstrapped SOLELY by its own
/// diagnoses", which risks reinforcing its own mistakes - a model citing its own earlier guess as
/// evidence. Forty human-reviewed RCAs and 185 real incident tickets break that loop.
///
/// Deliberately NOT anonymised (decision 2026-10-15): the documents carry real names and reach an
/// external LLM provider verbatim.
/// </summary>
public sealed class KnowledgeImportService(
    IKnowledgeDocumentSource source,
    IKnowledgeRetriever knowledgeRetriever,
    ILlmClient llmClient,
    ILogger<KnowledgeImportService> logger)
{
    /// <summary>Documents whose text is mostly broken control characters are reported; this is the threshold for saying so.</summary>
    private const int DegradedTextThreshold = 40;

    /// <summary>
    /// <paramref name="onlyTypes"/> limits which document types are indexed (see
    /// <see cref="KnowledgeSourceType"/>); null means all. Every document is still parsed - that is
    /// cheap - but only matching ones are embedded, which is the part that costs money and time.
    ///
    /// The profile key is the company these documents belong to. Null stores them as SHARED,
    /// readable by every company - correct for generic runbooks, and a cross-client leak for one
    /// customer's incident history, so the caller decides rather than drifting into the default.
    /// </summary>
    public async Task<KnowledgeImportResult> ImportAsync(int maxDocuments, IReadOnlySet<string>? onlyTypes = null, string? profileKey = null, CancellationToken ct = default)
    {
        var warnings = new List<string>();
        var byType = new Dictionary<string, int>(StringComparer.Ordinal);

        if (!source.IsAvailable)
        {
            // An unmounted corpus and an empty corpus produce the same zero; only this flag tells them apart.
            return new KnowledgeImportResult(false, source.Location, 0, 0, 0, 0, 0, 0, byType,
                [$"The document corpus is not available at '{source.Location}'. Nothing was imported."]);
        }

        var seenHashes = new HashSet<string>(await knowledgeRetriever.GetIndexedHashesAsync(ct), StringComparer.Ordinal);

        var documentsRead = 0;
        var skippedByType = 0;
        var chunksCreated = 0;
        var duplicates = 0;
        var withoutEmbedding = 0;
        var controlCharacters = 0;

        await foreach (var file in source.ReadAllAsync(ct))
        {
            if (documentsRead >= maxDocuments) break;
            ct.ThrowIfCancellationRequested();

            var document = KnowledgeDocumentParser.Parse(file.RelativePath, file.Content);

            if (onlyTypes is not null && !onlyTypes.Contains(document.SourceType))
            {
                skippedByType++;
                continue;
            }

            documentsRead++;
            controlCharacters += document.ControlCharactersRemoved;
            byType[document.SourceType] = byType.GetValueOrDefault(document.SourceType) + 1;

            if (document.ControlCharactersRemoved > DegradedTextThreshold)
            {
                warnings.Add($"'{document.Title}' had {document.ControlCharactersRemoved} control characters stripped - its extracted text is degraded and citations from it may be unreliable.");
            }

            if (document.Sections.Count == 0)
            {
                warnings.Add($"'{document.Title}' produced no usable sections and was skipped.");
                continue;
            }

            foreach (var section in document.Sections)
            {
                ct.ThrowIfCancellationRequested();

                var hash = KnowledgeDocumentParser.ContentHash(section.Body);
                if (!seenHashes.Add(hash))
                {
                    duplicates++;
                    continue;
                }

                var embedding = await llmClient.GetEmbeddingAsync(BuildEmbeddingInput(document, section), ct);
                if (!embedding.IsAvailable) withoutEmbedding++;

                await knowledgeRetriever.IndexAsync(new KnowledgeChunk
                {
                    SourceType = document.SourceType,
                    SourceRef = document.RelativePath,
                    Content = section.Body,
                    // A chunk whose embedding failed is still stored: it stays out of cosine
                    // retrieval (the model filter excludes it) but remains findable by the keyword
                    // fallback, which is better than losing the precedent entirely.
                    Embedding = embedding.Vector ?? [],
                    EmbeddingModel = embedding.IsAvailable ? embedding.Model : KnowledgeChunk.NoEmbeddingModel,
                    Domain = document.Domain,
                    DocumentTitle = document.Title,
                    SectionTitle = section.Title,
                    ContentHash = hash,
                }, ct, string.IsNullOrWhiteSpace(profileKey) ? null : profileKey.Trim());

                chunksCreated++;
            }
        }

        if (withoutEmbedding > 0)
        {
            warnings.Add($"{withoutEmbedding} chunk(s) were stored without an embedding and are only reachable by keyword matching. Re-run the import once the LLM provider is reachable.");
        }

        logger.LogInformation(
            "Knowledge import finished: {Documents} document(s), {Chunks} chunk(s), {Duplicates} duplicate(s) skipped.",
            documentsRead, chunksCreated, duplicates);

        return new KnowledgeImportResult(
            true, source.Location, documentsRead, skippedByType, chunksCreated, duplicates, withoutEmbedding, controlCharacters, byType, warnings);
    }

    /// <summary>
    /// The domain and section title are embedded with the body on purpose: "Root Cause" in an Air
    /// document and the same words in a Telecom one are different precedents, and the query that
    /// should find them carries that context too.
    /// </summary>
    private static string BuildEmbeddingInput(ParsedKnowledgeDocument document, KnowledgeSection section) =>
        $"[{document.Domain}] {document.Title}\n{section.Title}\n{section.Body}";
}
