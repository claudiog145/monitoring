using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;

namespace JourneyMonitor.Core.Monitoring;

/// <summary>
/// Shared by both Latency Spike Monkey detectors (KQL and SQL-journey-store variants): after a
/// run computes which stages currently qualify, any previously OPEN finding for the domain whose
/// CorrelationId is no longer among them is stale. "Refresh in place" alone (see both detectors'
/// RunAsync) only updates a still-qualifying finding's Title/Severity/EvidenceJson - it never
/// looks at findings that stopped qualifying entirely (e.g. a Settings threshold/occurrence
/// increase), which would otherwise stay open forever with frozen data.
/// </summary>
internal static class StaleFindingReconciler
{
    internal static async Task ResolveStaleFindingsAsync(
        IFindingsRepository findingsRepository,
        string domain,
        IReadOnlySet<string> stillQualifyingCorrelationIds,
        CancellationToken ct,
        string originMonkeyKey = MonkeyKey.LatencySpikeMonkey)
    {
        var open = await findingsRepository.GetOpenByDomainAsync(domain, ct);
        foreach (var stale in open)
        {
            if (stale.OriginMonkeyKey != originMonkeyKey) continue;
            if (stillQualifyingCorrelationIds.Contains(stale.CorrelationId)) continue;

            stale.Status = "resolved";
            await findingsRepository.UpdateAsync(stale, ct);
        }
    }
}
