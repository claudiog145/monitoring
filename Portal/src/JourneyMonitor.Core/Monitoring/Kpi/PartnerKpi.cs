using JourneyMonitor.Core.Domain;

namespace JourneyMonitor.Core.Monitoring.Kpi;

/// <summary>
/// Contract v4 §5, partners: the <c>appinsights:dependency</c> source is entity grain only and its
/// EntityKey is a <see cref="Company.PartnerDefinition.Key"/>. A partner anomaly is the one entity
/// anomaly that goes to Triage, as pillar Application with stage <c>partner:{key}</c>.
/// </summary>
public static class PartnerKpi
{
    public const string SourceKey = "appinsights:dependency";

    public const string StagePrefix = "partner:";

    public static bool IsPartnerDefinition(KpiDefinition definition) =>
        definition is not null
        && definition.Grain == KpiGrain.Entity
        && string.Equals(definition.SourceKey?.Trim(), SourceKey, StringComparison.OrdinalIgnoreCase);

    public static string TriageStage(string partnerKey) => StagePrefix + (partnerKey ?? "").Trim();
}
