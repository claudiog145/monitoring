namespace JourneyMonitor.Core.Abstractions;

/// <summary>
/// Where in the source a symbol or a route lives. This is the CodeAgent's source.
///
/// It returns locations, never source code: the transcript is persisted and served, so pulling file
/// contents into it would put production source into this platform's database as a side effect of
/// diagnosing a latency spike.
/// </summary>
public interface ICodeSearch
{
    /// <summary>False when nothing is configured. The caller must report that as "no source", never as "found nothing".</summary>
    bool HasSource { get; }

    /// <summary>Why there is no source, for the blackboard entry. Empty when <see cref="HasSource"/> is true.</summary>
    string NoSourceReason { get; }

    /// <summary>
    /// Searches for one identifier. One term per call on purpose: the index combines multiple terms
    /// with AND, so "searchrooms TimeoutException" returns almost nothing while either alone returns
    /// the real call sites.
    /// </summary>
    Task<CodeSearchResult> SearchAsync(string term, CancellationToken ct = default);
}

/// <summary>Three outcomes, never two - see <see cref="DocumentSearchResult"/> for why.</summary>
public sealed record CodeSearchResult(bool Succeeded, string? Error, IReadOnlyList<CodeHit> Hits)
{
    public static CodeSearchResult Ok(IReadOnlyList<CodeHit> hits) => new(true, null, hits);

    public static CodeSearchResult Failed(string error) => new(false, error, []);
}

/// <summary>
/// One location. No snippet and no line number, and both absences are deliberate:
/// - content is excluded by decision, so production source never lands in this database;
/// - the index reports character offsets rather than lines, and turning those into line numbers
///   would mean downloading the file, which is the same thing by another name.
/// </summary>
public sealed record CodeHit(
    string Repository,
    string Path,
    string FileName,
    /// <summary>Where the term is DEFINED, as opposed to merely mentioned. The single most useful signal here.</summary>
    bool IsDefinition,
    /// <summary>def / ref / comment / strlit / content, as the index classified them.</summary>
    IReadOnlyList<string> MatchKinds,
    int MatchCount,
    /// <summary>The repository name says it is not live code (legacy, fork, proof of concept). Reported, never silently dropped.</summary>
    bool RepositoryLooksNotLive,
    string? Url);
