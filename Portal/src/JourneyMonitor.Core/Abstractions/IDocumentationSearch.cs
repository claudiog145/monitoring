namespace JourneyMonitor.Core.Abstractions;

/// <summary>
/// What the organisation has already written down: wiki pages and recorded defects. This is the
/// DocsAgent's source.
///
/// It exists as its own contract rather than as a method on a generic search client because the
/// honest states matter more than the transport: see <see cref="DocumentSearchResult"/>.
/// </summary>
public interface IDocumentationSearch
{
    /// <summary>False when nothing is configured. The caller must report that as "no source", never as "found nothing".</summary>
    bool HasSource { get; }

    /// <summary>Why there is no source, for the blackboard entry. Empty when <see cref="HasSource"/> is true.</summary>
    string NoSourceReason { get; }

    /// <summary>
    /// Searches written documentation and recorded defects for the given terms. Never throws for a
    /// transport failure - a search that could not run returns
    /// <see cref="DocumentSearchResult.Failed"/>, because an investigation must not die because a
    /// wiki was briefly unavailable.
    /// </summary>
    Task<DocumentSearchResult> SearchAsync(string terms, CancellationToken ct = default);
}

/// <summary>
/// Three outcomes, never two. "Searched and found nothing" is evidence; "the search itself failed"
/// is not, and collapsing them would let an outage read as an all-clear.
/// </summary>
public sealed record DocumentSearchResult(bool Succeeded, string? Error, IReadOnlyList<DocumentHit> Hits)
{
    public static DocumentSearchResult Ok(IReadOnlyList<DocumentHit> hits) => new(true, null, hits);

    public static DocumentSearchResult Failed(string error) => new(false, error, []);
}

/// <summary>
/// One document. <see cref="Reference"/> is the stable identifier a model is expected to cite and
/// grounding later checks, which is why it is a separate field from the human-readable title.
/// </summary>
public sealed record DocumentHit(
    string Kind,
    string Reference,
    string Title,
    string? Path,
    string? Status,
    string? Url);

public static class DocumentHitKind
{
    public const string WikiPage = "wiki-page";

    /// <summary>A recorded defect or task. Its state matters: a closed defect describing this exact failure is a different fact from an open one.</summary>
    public const string WorkItem = "work-item";
}
