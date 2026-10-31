using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.UseCases;
using Microsoft.Extensions.Logging.Abstractions;

namespace JourneyMonitor.Tests.Monitoring;

public class KnowledgeDocumentParserTests
{
    // Shortened from docs/RCAS-md/Air/NG Air latency.md - markitdown renders Word headings as
    // bold-only lines, which is why the parser cannot rely on '#'.
    private const string FormalRca = """
        **Root Cause Analysis**

        NG Flight Issue — Booking Latency / Intermittent Timeouts

        **Bottom Line Up Front**

        **What Happened** On June 16, 2026, beginning at 7:32 AM, agents and users reported severe latency on the NG Flight page.

        **Root Cause — Plain Language**

        The NG Air API server entered a degraded state, causing severe latency on flight page requests across all clubs.

        **5 Whys Analysis**

        Why 1: Why were NG flights slow to load? The NG Air API server was in a degraded, high-latency state.
        """;

    private const string Ticket = """
        Arrivia

        15147 N Scottsdale Rd ste 210

        User: Jorge Gonzalez
        Opened

        Unable to search Flights, getting an error message.

        Incident Details Report

        ID: 0119086
        Priority: Red
        Status: Closed
        """;

    [Fact]
    public void AFormalRcaIsLabelledAsAnRcaDocument()
    {
        var parsed = KnowledgeDocumentParser.Parse("Air/NG Air latency.md", FormalRca);

        Assert.Equal(KnowledgeSourceType.RcaDocument, parsed.SourceType);
    }

    [Fact]
    public void AnExportedTicketIsLabelledAsATicketNotAnRca()
    {
        // A ticket records a reported symptom; an RCA records a reviewed conclusion. Citing the
        // first as the second is exactly what the grounding exists to prevent.
        var parsed = KnowledgeDocumentParser.Parse("Air/1.15.24 Ng Flights unable to search.md", Ticket);

        Assert.Equal(KnowledgeSourceType.IncidentTicket, parsed.SourceType);
    }

    [Fact]
    public void ADocumentMatchingNeitherTemplateIsNotForcedIntoTheNearestBucket()
    {
        var parsed = KnowledgeDocumentParser.Parse("Network/notes.md", "Some loose notes about a switch replacement that follow no template at all, written by hand.");

        Assert.Equal(KnowledgeSourceType.IncidentDocument, parsed.SourceType);
    }

    [Fact]
    public void TheDomainComesFromTheFolderBecauseItIsTheOnlyLabelTheCorpusCarries()
    {
        var parsed = KnowledgeDocumentParser.Parse("NG-Hotel/some outage.md", FormalRca);

        Assert.Equal("NG-Hotel", parsed.Domain);
        Assert.Equal("some outage", parsed.Title);
    }

    [Fact]
    public void ADocumentAtTheRootHasNoDomainRatherThanAGuessedOne()
    {
        var parsed = KnowledgeDocumentParser.Parse("loose.md", FormalRca);

        Assert.Equal("unclassified", parsed.Domain);
    }

    [Fact]
    public void BoldHeadingsBecomeSeparateSections()
    {
        var parsed = KnowledgeDocumentParser.Parse("Air/NG Air latency.md", FormalRca);

        // "Root Cause" and "5 Whys" answer different questions - one vector over the whole
        // document would average them into something that answers neither.
        Assert.Contains(parsed.Sections, s => s.Title.Contains("Root Cause", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(parsed.Sections, s => s.Title.Contains("5 Whys", StringComparison.OrdinalIgnoreCase));
        Assert.True(parsed.Sections.Count > 1);
    }

    [Fact]
    public void MarkdownHashHeadingsAreAlsoRecognised()
    {
        var text = "## Incident Summary\nThe gateway returned 504 for every request during the window, affecting all clubs.\n\n## Resolution\nThe pool was restarted and traffic recovered within four minutes of the change.";

        var parsed = KnowledgeDocumentParser.Parse("Cruise/x.md", text);

        Assert.Contains(parsed.Sections, s => s.Title == "Incident Summary");
        Assert.Contains(parsed.Sections, s => s.Title == "Resolution");
    }

    [Fact]
    public void AFullyBoldParagraphIsContentNotAHeading()
    {
        var longBold = "**" + new string('x', 200) + "**";
        var text = $"## Real Heading\n{longBold}\nMore body text that belongs under the real heading rather than its own.";

        var parsed = KnowledgeDocumentParser.Parse("Car/x.md", text);

        Assert.Single(parsed.Sections);
        Assert.Equal("Real Heading", parsed.Sections[0].Title);
    }

    [Fact]
    public void ControlCharactersAreStrippedAndCounted()
    {
        // Real corpus damage: PDF extraction loses ligatures and leaves control bytes mid-word,
        // so "Scottsdale" arrives as "Sco\0sdale" in 210 of the 257 documents.
        var text = "## Heading\nThe incident happened in Sco\u0000sdale during an Automa\u0000on failure that affected every club on the platform.";

        var parsed = KnowledgeDocumentParser.Parse("Travel/x.md", text);

        Assert.Equal(2, parsed.ControlCharactersRemoved);
        // char overload on purpose: the string overload compares by culture, which gives control
        // characters no weight, so every string "contains" "\0" at position 0.
        Assert.DoesNotContain('\u0000', parsed.Sections[0].Body);
        // The missing letters are NOT guessed back - removing what is broken is cleaning,
        // inventing what is missing is fabrication.
        Assert.Contains("Scosdale", parsed.Sections[0].Body);
    }

    [Fact]
    public void TheContentHashIgnoresWhitespaceAndCaseSoTwoExportsOfOneDocumentCollapse()
    {
        var a = KnowledgeDocumentParser.ContentHash("The API server entered a degraded state.");
        var b = KnowledgeDocumentParser.ContentHash("  the   API server\nentered a degraded   state.  ");

        Assert.Equal(a, b);
    }

    [Fact]
    public void DifferentContentProducesDifferentHashes()
    {
        var a = KnowledgeDocumentParser.ContentHash("The API server entered a degraded state.");
        var b = KnowledgeDocumentParser.ContentHash("The database ran out of connections.");

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void AnOversizedSectionIsSplitIntoNumberedParts()
    {
        var body = string.Join("\n\n", Enumerable.Range(0, 60).Select(i => $"Paragraph {i} describing one more step of a very long incident timeline in detail."));
        var parsed = KnowledgeDocumentParser.Parse("Telecom/x.md", $"## Timeline\n{body}");

        Assert.True(parsed.Sections.Count > 1);
        Assert.All(parsed.Sections, s => Assert.True(s.Body.Length <= KnowledgeDocumentParser.MaxChunkChars));
        Assert.Contains(parsed.Sections, s => s.Title.Contains("part 1", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SplittingProducesAChunkCountProportionalToTheTextNotToItsTail()
    {
        // Regression: the step collapsed to one character once the remaining text was shorter
        // than the overlap, so the loop walked the tail one character at a time. Three real
        // documents produced 197 chunks, almost all near-copies of each other.
        var body = new string('x', 5000);
        var parsed = KnowledgeDocumentParser.Parse("Travel/x.md", $"## Long\n{body}");

        var upperBound = (body.Length / (KnowledgeDocumentParser.MaxChunkChars / 2)) + 2;
        Assert.InRange(parsed.Sections.Count, 1, upperBound);
    }

    [Fact]
    public void ADocumentWithNoHeadingsStillProducesChunks()
    {
        var parsed = KnowledgeDocumentParser.Parse("Network/plain.md", "A flat document with no headings at all, just a paragraph long enough to be worth indexing on its own.");

        Assert.Single(parsed.Sections);
        Assert.Equal("Document", parsed.Sections[0].Title);
    }
}

internal sealed class FakeKnowledgeDocumentSource(bool available, params KnowledgeDocumentFile[] files) : IKnowledgeDocumentSource
{
    public bool IsAvailable => available;

    public string Location => "/fake-corpus";

    public async IAsyncEnumerable<KnowledgeDocumentFile> ReadAllAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var file in files)
        {
            await Task.Yield();
            yield return file;
        }
    }
}

internal sealed class RecordingKnowledgeRetriever : IKnowledgeRetriever
{
    public List<KnowledgeChunk> Indexed { get; } = [];

    /// <summary>The company each chunk was filed under - null means the shared corpus.</summary>
    public List<string?> IndexedProfileKeys { get; } = [];

    public Task<IReadOnlyList<KnowledgeChunk>> RetrieveAsync(string query, int topK, CancellationToken ct = default, string? profileKey = null) =>
        Task.FromResult<IReadOnlyList<KnowledgeChunk>>(Indexed);

    public Task IndexAsync(KnowledgeChunk chunk, CancellationToken ct = default, string? profileKey = null)
    {
        Indexed.Add(chunk);
        IndexedProfileKeys.Add(profileKey);
        return Task.CompletedTask;
    }

    public Task<IReadOnlySet<string>> GetIndexedHashesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlySet<string>>(
            Indexed.Select(c => c.ContentHash).Where(h => h is not null).Select(h => h!).ToHashSet(StringComparer.Ordinal));

    public Task<IReadOnlyList<KnowledgeDocumentSummary>> GetIndexedDocumentsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<KnowledgeDocumentSummary>>(
            Indexed
                .GroupBy(c => (c.DocumentTitle ?? "(untitled)", c.SourceType))
                .Select(g => new KnowledgeDocumentSummary(g.Key.Item1, g.Key.SourceType, g.First().Domain ?? "", g.Count(), g.Count(), g.Max(c => c.CreatedAt)))
                .ToList());

    public Task<IReadOnlyList<KnowledgeDocumentSection>> GetDocumentSectionsAsync(string documentTitle, string? sourceType, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<KnowledgeDocumentSection>>(
            Indexed
                .Where(c => (c.DocumentTitle ?? "(untitled)") == documentTitle)
                .Select(c => new KnowledgeDocumentSection(c.SectionTitle ?? "", c.Content, c.SourceRef ?? ""))
                .ToList());
}

public class KnowledgeImportServiceTests
{
    private const string Doc = "## Root Cause\nThe API server entered a degraded state and every booking request timed out for ninety minutes.";

    private static KnowledgeImportService Build(IKnowledgeDocumentSource source, IKnowledgeRetriever retriever, ILlmClient llm) =>
        new(source, retriever, llm, NullLogger<KnowledgeImportService>.Instance);

    [Fact]
    public async Task AnUnavailableCorpusIsReportedRatherThanCountedAsAnEmptyImport()
    {
        var retriever = new RecordingKnowledgeRetriever();
        var service = Build(new FakeKnowledgeDocumentSource(available: false), retriever, new FakeLlmClient("", [0.1f, 0.2f]));

        var result = await service.ImportAsync(100, null, ct: CancellationToken.None);

        // An unmounted corpus and an empty one both produce zero; only this flag tells them apart.
        Assert.False(result.SourceAvailable);
        Assert.Equal(0, result.DocumentsRead);
        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public async Task ChunksCarryTheDomainDocumentAndSectionSoACitationCanNameThem()
    {
        var retriever = new RecordingKnowledgeRetriever();
        var service = Build(
            new FakeKnowledgeDocumentSource(true, new KnowledgeDocumentFile("Air/NG Air latency.md", Doc)),
            retriever,
            new FakeLlmClient("", [0.1f, 0.2f]));

        await service.ImportAsync(100, null, ct: CancellationToken.None);

        var chunk = Assert.Single(retriever.Indexed);
        Assert.Equal("Air", chunk.Domain);
        Assert.Equal("NG Air latency", chunk.DocumentTitle);
        Assert.Equal("Root Cause", chunk.SectionTitle);
        Assert.Equal("Air/NG Air latency.md", chunk.SourceRef);
        Assert.NotNull(chunk.ContentHash);
    }

    [Fact]
    public async Task TheSameDocumentImportedTwiceIsSkippedTheSecondTime()
    {
        // Eleven documents in the corpus are literal copies ("- Copy", "(1)"). Indexed twice,
        // that precedent silently counts double in cosine retrieval.
        var retriever = new RecordingKnowledgeRetriever();
        var service = Build(
            new FakeKnowledgeDocumentSource(true,
                new KnowledgeDocumentFile("Air/original.md", Doc),
                new KnowledgeDocumentFile("Air/original - Copy.md", Doc)),
            retriever,
            new FakeLlmClient("", [0.1f, 0.2f]));

        var result = await service.ImportAsync(100, null, ct: CancellationToken.None);

        Assert.Equal(2, result.DocumentsRead);
        Assert.Equal(1, result.ChunksCreated);
        Assert.Equal(1, result.ChunksSkippedAsDuplicate);
        Assert.Single(retriever.Indexed);
    }

    [Fact]
    public async Task ARerunAgainstAnAlreadyPopulatedStoreAddsNothing()
    {
        var retriever = new RecordingKnowledgeRetriever();
        var source = new FakeKnowledgeDocumentSource(true, new KnowledgeDocumentFile("Air/x.md", Doc));
        var service = Build(source, retriever, new FakeLlmClient("", [0.1f, 0.2f]));

        await service.ImportAsync(100, null, ct: CancellationToken.None);
        var second = await service.ImportAsync(100, null, ct: CancellationToken.None);

        Assert.Equal(0, second.ChunksCreated);
        Assert.Single(retriever.Indexed);
    }

    [Fact]
    public async Task AChunkWhoseEmbeddingFailedIsStillStoredButMarkedAsHavingNone()
    {
        // Keeps the precedent reachable by the keyword fallback instead of losing it, while the
        // model filter keeps it out of cosine retrieval where it would be meaningless.
        var retriever = new RecordingKnowledgeRetriever();
        var service = Build(
            new FakeKnowledgeDocumentSource(true, new KnowledgeDocumentFile("Air/x.md", Doc)),
            retriever,
            new FakeLlmClient("", embedding: null));

        var result = await service.ImportAsync(100, null, ct: CancellationToken.None);

        Assert.Equal(1, result.ChunksIndexedWithoutEmbedding);
        Assert.Equal("none", Assert.Single(retriever.Indexed).EmbeddingModel);
        Assert.Contains(result.Warnings, w => w.Contains("keyword", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DocumentsAreCountedByTypeSoTheMixIsVisible()
    {
        var retriever = new RecordingKnowledgeRetriever();
        var service = Build(
            new FakeKnowledgeDocumentSource(true,
                new KnowledgeDocumentFile("Air/rca.md", "**Root Cause Analysis**\n\n**What Happened** The API server entered a degraded state for ninety minutes."),
                new KnowledgeDocumentFile("Air/ticket.md", "Incident Details Report\n\nID: 1\nPriority: Red\nUnable to search flights, an error message was shown to every user.")),
            retriever,
            new FakeLlmClient("", [0.1f, 0.2f]));

        var result = await service.ImportAsync(100, null, ct: CancellationToken.None);

        Assert.Equal(1, result.DocumentsByType[KnowledgeSourceType.RcaDocument]);
        Assert.Equal(1, result.DocumentsByType[KnowledgeSourceType.IncidentTicket]);
    }

    [Fact]
    public async Task TheDocumentCapIsHonoured()
    {
        var retriever = new RecordingKnowledgeRetriever();
        var service = Build(
            new FakeKnowledgeDocumentSource(true,
                new KnowledgeDocumentFile("Air/a.md", Doc),
                new KnowledgeDocumentFile("Air/b.md", Doc.Replace("degraded", "overloaded")),
                new KnowledgeDocumentFile("Air/c.md", Doc.Replace("degraded", "unreachable"))),
            retriever,
            new FakeLlmClient("", [0.1f, 0.2f]));

        var result = await service.ImportAsync(2, null, ct: CancellationToken.None);

        Assert.Equal(2, result.DocumentsRead);
    }

    [Fact]
    public async Task OnlyTheRequestedDocumentTypesAreEmbedded()
    {
        // Loading the 40 reviewed RCAs first costs a tenth of the full corpus and answers whether
        // retrieval is any good before paying for the 185 tickets.
        var retriever = new RecordingKnowledgeRetriever();
        var service = Build(
            new FakeKnowledgeDocumentSource(true,
                new KnowledgeDocumentFile("Air/rca.md", "**Root Cause Analysis**\n\n**What Happened** The API server entered a degraded state for ninety minutes straight."),
                new KnowledgeDocumentFile("Air/ticket.md", "Incident Details Report\n\nID: 1\nPriority: Red\nUnable to search flights, an error message was shown to every user.")),
            retriever,
            new FakeLlmClient("", [0.1f, 0.2f]));

        var result = await service.ImportAsync(100, new HashSet<string> { KnowledgeSourceType.RcaDocument }, ct: CancellationToken.None);

        Assert.Equal(1, result.DocumentsRead);
        Assert.Equal(1, result.DocumentsSkippedByType);
        Assert.All(retriever.Indexed, c => Assert.Equal(KnowledgeSourceType.RcaDocument, c.SourceType));
    }
}
