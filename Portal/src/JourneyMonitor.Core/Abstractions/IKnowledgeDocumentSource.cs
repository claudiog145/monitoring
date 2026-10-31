namespace JourneyMonitor.Core.Abstractions;

/// <summary>One converted document, read as text. Path is relative to the corpus root so the folder (the domain label) survives.</summary>
public sealed record KnowledgeDocumentFile(string RelativePath, string Content);

/// <summary>
/// Where the converted RCA/incident corpus is read from (today: docs/RCAS-md, produced by
/// markitdown). Abstracted so the import logic stays testable without touching a filesystem, and
/// so the corpus can later move to blob storage without changing the service that consumes it.
/// </summary>
public interface IKnowledgeDocumentSource
{
    /// <summary>False when the corpus is not mounted/configured - the caller reports that instead of importing zero documents and calling it a success.</summary>
    bool IsAvailable { get; }

    /// <summary>Human-readable location, used in the import report so an empty result can be diagnosed.</summary>
    string Location { get; }

    IAsyncEnumerable<KnowledgeDocumentFile> ReadAllAsync(CancellationToken cancellationToken);
}
