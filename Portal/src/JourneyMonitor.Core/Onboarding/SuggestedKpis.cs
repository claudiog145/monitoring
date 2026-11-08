using System.Globalization;
using JourneyMonitor.Core.Company;
using JourneyMonitor.Core.Domain;
using JourneyMonitor.Core.Monitoring.Kpi;

namespace JourneyMonitor.Core.Onboarding;

/// <summary>
/// One KPI the onboarding draft proposes (contract v4 §5, AG7). <see cref="Definition"/> is the exact
/// body POST /api/settings/kpis accepts, so the portal forwards it verbatim and decides nothing.
/// It always arrives with <c>enabled = false</c>: creating it and switching detection on are two decisions.
/// </summary>
public sealed record SuggestedKpi(
    /// <summary>Stable handle within one draft, e.g. <c>latency:domain:checkout-api</c>.</summary>
    string Key,
    string Title,
    /// <summary>Why it is proposed, in one sentence a reviewer can disagree with.</summary>
    string Reason,
    SuggestedKpiDefinition Definition);

/// <summary>Wire shape of a KPI definition for POST /api/settings/kpis (camelCase under Web JSON defaults).</summary>
public sealed record SuggestedKpiDefinition(
    string ProfileKey,
    string Pillar,
    string Kpi,
    string Domain,
    string Stage,
    string SourceKey,
    string Direction,
    string Aggregation,
    string EvaluationFrequency,
    string BucketSize,
    string LookbackWindow,
    int SampleSize,
    int MinObservationsPerBucket,
    string DetectionMethod,
    string MethodParamsJson,
    int ConsecutiveCount,
    int CooldownMinutes,
    bool Enabled,
    string Grain,
    int MaxEntitiesPerBucket)
{
    public int KpiDefinitionId => 0;

    /// <summary>The definition the KPI API would build from this body, for validation before it is ever sent.</summary>
    public KpiDefinition ToKpiDefinition(DateTimeOffset now, string changedBy = "onboarding") =>
        new(
            KpiDefinitionId: 0,
            Pillar: Enum.Parse<KpiPillar>(Pillar, ignoreCase: true),
            Kpi: Kpi,
            Domain: Domain,
            Stage: Stage,
            SourceKey: SourceKey,
            Direction: Enum.Parse<KpiDirection>(Direction, ignoreCase: true),
            Aggregation: Enum.Parse<KpiAggregation>(Aggregation, ignoreCase: true),
            EvaluationFrequency: TimeSpan.ParseExact(EvaluationFrequency, "c", CultureInfo.InvariantCulture),
            BucketSize: TimeSpan.ParseExact(BucketSize, "c", CultureInfo.InvariantCulture),
            LookbackWindow: TimeSpan.ParseExact(LookbackWindow, "c", CultureInfo.InvariantCulture),
            SampleSize: SampleSize,
            MinObservationsPerBucket: MinObservationsPerBucket,
            DetectionMethod: Enum.Parse<DetectionMethodKind>(DetectionMethod, ignoreCase: true),
            MethodParamsJson: MethodParamsJson,
            ConsecutiveCount: ConsecutiveCount,
            CooldownMinutes: CooldownMinutes,
            Enabled: Enabled,
            UpdatedAt: now,
            UpdatedBy: changedBy)
        {
            ProfileKey = ProfileKey,
            Grain = string.Equals(Grain, "entity", StringComparison.OrdinalIgnoreCase) ? KpiGrain.Entity : KpiGrain.Domain,
            MaxEntitiesPerBucket = MaxEntitiesPerBucket,
        };
}

/// <summary>The suggestions for one profile, plus the plain-language reasons anything was not suggested.</summary>
public sealed record SuggestedKpiPlan(IReadOnlyList<SuggestedKpi> Kpis, IReadOnlyList<string> Notes);

/// <summary>
/// Proposes the KPIs a newly onboarded profile needs to be watched by the daemons, from the profile alone.
/// Pure: no I/O, no clock, no model.
///
/// Per service: Latency P95 and Exceptions count at domain grain (the same rules the incumbent seed
/// uses), plus both at entity grain with an empty Stage (every stage of the service per entity,
/// amendment A1). Per DISTINCT set of telemetry source aliases, and only when the profile declares
/// partners: one <c>appinsights:dependency</c> KPI. The dependency query scopes by the service's
/// sources and classifies by the profile's partners, so two services sharing their sources would
/// produce two identical series - the first such service carries it.
/// </summary>
public static class SuggestedKpiBuilder
{
    public const string LatencyKpi = "Latency";
    public const string ExceptionsKpi = "Exceptions";
    public const string PartnerFailuresKpi = "PartnerFailures";

    private const string LatencySourceKey = "appinsights:latency";
    private const string ExceptionsSourceKey = "appinsights:exceptions";

    private const string Application = "Application";
    private const string Increase = "Increase";
    private const string FiveMinutes = "00:05:00";
    private const string OneHour = "01:00:00";
    private const string OneDay = "1.00:00:00";
    private const string BaselineParams = "{\"lookbackDays\":7,\"thresholdPercent\":50}";
    private const string RobustParams = "{\"windowBuckets\":24,\"zThreshold\":3}";
    private const int ConsecutiveBuckets = 2;
    private const int CooldownMinutes = 120;
    private const int MaxEntities = 500;

    public static SuggestedKpiPlan Build(CompanyProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var profileKey = (profile.ProfileKey ?? "").Trim();
        var kpis = new List<SuggestedKpi>();
        var notes = new List<string>();

        var eligible = new List<ServiceDefinition>();
        foreach (var service in profile.Services ?? [])
        {
            var key = service?.Key?.Trim() ?? "";
            if (key.Length == 0) continue;
            if (key.Length > KpiDomainPolicy.MaxStoredLength)
            {
                notes.Add($"No KPI suggested for service \"{key}\": a KPI domain is at most {KpiDomainPolicy.MaxStoredLength} characters. Shorten the service key, then add its KPIs from Settings > KPIs.");
                continue;
            }
            eligible.Add(service!);
        }

        foreach (var service in eligible)
        {
            var domain = service.Key.Trim();
            kpis.Add(new SuggestedKpi(
                $"latency:domain:{domain}",
                $"Latency P95 · {service.DisplayName}",
                "Request latency P95 against its own 7-day baseline (+50%), 1 h buckets, 2 of 2 - the same rule the daemons run for the incumbent's services.",
                Definition(profileKey, LatencyKpi, domain, LatencySourceKey, "P95", 4000, 30, "BaselinePercent", BaselineParams, "domain")));
            kpis.Add(new SuggestedKpi(
                $"exceptions:domain:{domain}",
                $"Exceptions · {service.DisplayName}",
                "Exception count per 1 h bucket against a robust z-score over the last 24 buckets (z > 3), 2 of 2.",
                Definition(profileKey, ExceptionsKpi, domain, ExceptionsSourceKey, "Count", 0, 0, "RobustZScore", RobustParams, "domain")));
            kpis.Add(new SuggestedKpi(
                $"latency:entity:{domain}",
                $"Latency P95 per entity · {service.DisplayName}",
                "Latency P95 per entity across every step of the service (empty stage), robust z-score 24/3; up to 500 entities per bucket.",
                Definition(profileKey, LatencyKpi, domain, LatencySourceKey, "P95", 0, 30, "RobustZScore", RobustParams, "entity")));
            kpis.Add(new SuggestedKpi(
                $"exceptions:entity:{domain}",
                $"Exceptions per entity · {service.DisplayName}",
                "Exception count per entity across every step of the service (empty stage), robust z-score 24/3; up to 500 entities per bucket.",
                Definition(profileKey, ExceptionsKpi, domain, ExceptionsSourceKey, "Count", 0, 0, "RobustZScore", RobustParams, "entity")));
        }

        AddDependencyKpis(profile, profileKey, eligible, kpis, notes);
        return new SuggestedKpiPlan(kpis, notes);
    }

    private static void AddDependencyKpis(
        CompanyProfile profile,
        string profileKey,
        IReadOnlyList<ServiceDefinition> eligible,
        List<SuggestedKpi> kpis,
        List<string> notes)
    {
        var partners = (profile.Partners ?? []).Where(p => p is not null && !string.IsNullOrWhiteSpace(p.Key)).ToList();
        if (partners.Count == 0)
        {
            notes.Add("No partner dependency KPI suggested: the profile declares no partners. Add partners (dependency matchers) to the profile and the draft will propose one.");
            return;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var service in eligible)
        {
            var aliases = (service.TelemetrySourceAliases ?? [])
                .Select(a => a?.Trim().ToLowerInvariant() ?? "")
                .Where(a => a.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(a => a, StringComparer.Ordinal)
                .ToList();
            if (aliases.Count == 0)
            {
                notes.Add($"No partner dependency KPI for service \"{service.Key.Trim()}\": it declares no telemetry source alias, so there is no dependencies table to read.");
                continue;
            }

            var set = string.Join("|", aliases);
            if (!seen.Add(set)) continue;

            var domain = service.Key.Trim();
            var partnerList = string.Join(", ", partners.Select(p => p.Key.Trim()));
            kpis.Add(new SuggestedKpi(
                $"dependency:entity:{domain}",
                $"Partner failures · {string.Join(", ", aliases)}",
                $"Failed-call percentage per partner ({partnerList}) on the dependencies of {string.Join(", ", aliases)}; one KPI per distinct set of telemetry sources, carried by service \"{domain}\". Robust z-score 24/3, 1 h buckets.",
                Definition(profileKey, PartnerFailuresKpi, domain, PartnerKpi.SourceKey, "Value", 0, 20, "RobustZScore", RobustParams, "entity")));
        }
    }

    private static SuggestedKpiDefinition Definition(
        string profileKey,
        string kpi,
        string domain,
        string sourceKey,
        string aggregation,
        int sampleSize,
        int minObservations,
        string method,
        string methodParams,
        string grain) =>
        new(
            ProfileKey: profileKey,
            Pillar: Application,
            Kpi: kpi,
            Domain: domain,
            Stage: "",
            SourceKey: sourceKey,
            Direction: Increase,
            Aggregation: aggregation,
            EvaluationFrequency: FiveMinutes,
            BucketSize: OneHour,
            LookbackWindow: OneDay,
            SampleSize: sampleSize,
            MinObservationsPerBucket: minObservations,
            DetectionMethod: method,
            MethodParamsJson: methodParams,
            ConsecutiveCount: ConsecutiveBuckets,
            CooldownMinutes: CooldownMinutes,
            Enabled: false,
            Grain: grain,
            MaxEntitiesPerBucket: MaxEntities);
}
