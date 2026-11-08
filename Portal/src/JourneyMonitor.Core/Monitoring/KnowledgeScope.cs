using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Domain;

namespace JourneyMonitor.Core.Monitoring;

/// <summary>
/// Which company a stored knowledge chunk belongs to (contract v6 §1/§3). A blank ProfileKey is the
/// shared corpus, EXCEPT for <see cref="KnowledgeSourceType.FindingHistory"/>: those chunks are the
/// Doctor's diagnoses of findings from the incumbent's legacy pipeline, written before chunks carried
/// a company, so an untagged one is the incumbent's - never a passage every company should read.
/// <c>usp_GetAllKnowledgeChunks</c>, <c>usp_GetKnowledgeDocuments</c> and
/// <c>usp_GetKnowledgeChunksByDocument</c> apply the same rule in SQL.
/// </summary>
public static class KnowledgeScope
{
    /// <summary>The owner of a stored chunk: a profile key, or "" for the shared corpus.</summary>
    public static string OwnerOf(string? sourceType, string? storedProfileKey)
    {
        if (!string.IsNullOrWhiteSpace(storedProfileKey)) return storedProfileKey.Trim();
        return string.Equals(sourceType, KnowledgeSourceType.FindingHistory, StringComparison.OrdinalIgnoreCase)
            ? KpiProfiles.Default
            : "";
    }

    /// <summary>
    /// Whether a stored chunk is readable by <paramref name="requestedProfileKey"/>: null/blank reads the
    /// whole corpus; a company reads its own chunks plus the shared corpus.
    /// </summary>
    public static bool IsVisibleTo(string? sourceType, string? storedProfileKey, string? requestedProfileKey)
    {
        if (string.IsNullOrWhiteSpace(requestedProfileKey)) return true;
        var owner = OwnerOf(sourceType, storedProfileKey);
        return owner.Length == 0 || string.Equals(owner, requestedProfileKey.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
