using JourneyMonitor.Core.Abstractions;
using JourneyMonitor.Core.Domain;

namespace JourneyMonitor.Core.Monitoring.Kpi;

/// <summary>
/// A <c>kql:</c> KPI source that can also run a candidate query text instead of the stored one,
/// so an edit of a catalog query can be dry-run against the KPIs that use it before it is saved.
/// </summary>
public interface IKqlKpiTextSource : IKpiSource
{
    Task<KpiSourceResult> FetchWithTextAsync(KpiDefinition definition, string kqlText, DateTimeOffset fromInclusive, DateTimeOffset toExclusive,
        int samplesPerBucket, CancellationToken ct = default);
}

/// <summary>
/// Which KPIs use a custom catalog query, and a dry run of a candidate text for one of them. The
/// KQL admin service is a singleton while the KPI sources are scoped, so the dry run comes in as a
/// delegate the host builds (it opens a scope per call).
/// </summary>
public sealed class KqlQueryUsage(
    IKpiDefinitionStore definitions,
    Func<KpiDefinition, string, CancellationToken, Task<string?>> dryRunWithText)
{
    /// <summary>Every definition whose SourceKey is <c>kql:{queryKey}</c>, of every profile.</summary>
    public async Task<IReadOnlyList<KpiDefinition>> GetUsersAsync(string queryKey, CancellationToken ct = default)
    {
        var all = await definitions.GetAllAsync(ct).ConfigureAwait(false);
        return all
            .Where(d => string.Equals(KqlKpiContract.QueryKeyOf(d.SourceKey), queryKey, StringComparison.Ordinal))
            .OrderBy(d => d.KpiDefinitionId)
            .ToList();
    }

    public Task<string?> DryRunAsync(KpiDefinition definition, string kqlText, CancellationToken ct = default) =>
        dryRunWithText(definition, kqlText, ct);
}
