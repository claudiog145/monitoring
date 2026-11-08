using System.Text.Json;
using JourneyMonitor.Core.Company;

namespace JourneyMonitor.Core.Domain;

public enum KpiPillar { Business, Application, Infrastructure }

/// <summary>Which movement is unhealthy. StaticThreshold does not accept Either.</summary>
public enum KpiDirection { Increase, Decrease, Either }

public enum DetectionMethodKind { StaticThreshold, BaselinePercent, RobustZScore }

/// <summary>Which bucket statistic is evaluated. Count = TotalCount, Value = source-provided value (rate, revenue...).</summary>
public enum KpiAggregation { P95, P50, Mean, Count, Value }

/// <summary>Domain = one aggregate series per definition (v1-v3). Entity = one series per EntityKey (contract v4 §1).</summary>
public enum KpiGrain { Domain, Entity }

public static class KpiDomains
{
    public static readonly IReadOnlyList<string> All = ["hotels", "cars", "cruises", "air"];
}

public static class KpiPillarNames
{
    public static string ToDisplay(KpiPillar pillar) => pillar switch
    {
        KpiPillar.Business => "Business Health",
        KpiPillar.Application => "Application Health",
        KpiPillar.Infrastructure => "Infrastructure Health",
        _ => throw new ArgumentOutOfRangeException(nameof(pillar)),
    };

    public static bool TryParseDisplay(string? value, out KpiPillar pillar)
    {
        foreach (var candidate in Enum.GetValues<KpiPillar>())
        {
            if (string.Equals(ToDisplay(candidate), value, StringComparison.OrdinalIgnoreCase)
                || string.Equals(candidate.ToString(), value, StringComparison.OrdinalIgnoreCase))
            {
                pillar = candidate;
                return true;
            }
        }
        pillar = default;
        return false;
    }
}

/// <summary>One configurable KPI rule. Stage is "" when the KPI covers every stage of the domain.</summary>
public sealed record KpiDefinition(
    int KpiDefinitionId,
    KpiPillar Pillar,
    string Kpi,
    string Domain,
    string Stage,
    string SourceKey,
    KpiDirection Direction,
    KpiAggregation Aggregation,
    TimeSpan EvaluationFrequency,
    TimeSpan BucketSize,
    TimeSpan LookbackWindow,
    int SampleSize,
    int MinObservationsPerBucket,
    DetectionMethodKind DetectionMethod,
    string MethodParamsJson,
    int ConsecutiveCount,
    int CooldownMinutes,
    bool Enabled,
    DateTimeOffset UpdatedAt,
    string UpdatedBy)
{
    /// <summary>Company profile (monitoring.CompanyProfile.ProfileKey) this KPI belongs to.</summary>
    public string ProfileKey { get; init; } = KpiProfiles.Default;

    /// <summary>Contract v4 §1. Part of the unique key (ProfileKey, Pillar, Kpi, Domain, Stage, Grain).</summary>
    public KpiGrain Grain { get; init; } = KpiGrain.Domain;

    /// <summary>Entity grain only: cap of entity series kept per bucket, 1..5000.</summary>
    public int MaxEntitiesPerBucket { get; init; } = 500;

    /// <summary>Returns every rule violation; empty means valid. Same ranges as the SQL CHECK constraints.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(ProfileKey)) errors.Add("ProfileKey is required.");
        if (string.IsNullOrWhiteSpace(Kpi) || Kpi.Length > 64) errors.Add("Kpi is required (max 64).");
        // Contract v4 §5: membership in the profile's services is checked by the caller (AG1), not here.
        if (string.IsNullOrWhiteSpace(Domain) || Domain.Length > 64) errors.Add("Domain is required (max 64).");
        if (!Enum.IsDefined(Grain)) errors.Add("Grain must be domain or entity.");
        if (MaxEntitiesPerBucket is < 1 or > 5000) errors.Add("MaxEntitiesPerBucket must be 1..5000.");
        if (Stage is null || Stage.Length > 64) errors.Add("Stage must be \"\" or max 64 chars.");
        if (string.IsNullOrWhiteSpace(SourceKey) || SourceKey.Length > 64) errors.Add("SourceKey is required (max 64).");
        if (EvaluationFrequency < TimeSpan.FromMinutes(1)) errors.Add("EvaluationFrequency must be >= 1 minute.");
        if (BucketSize < TimeSpan.FromMinutes(1)) errors.Add("BucketSize must be >= 1 minute.");
        if (LookbackWindow < BucketSize) errors.Add("LookbackWindow must be >= BucketSize.");
        if (SampleSize is < 0 or > 100_000) errors.Add("SampleSize must be 0..100000.");
        if (MinObservationsPerBucket < 0) errors.Add("MinObservationsPerBucket must be >= 0.");
        if (ConsecutiveCount is < 1 or > 48) errors.Add("ConsecutiveCount must be 1..48.");
        if (CooldownMinutes is < 0 or > 10_080) errors.Add("CooldownMinutes must be 0..10080.");
        if (DetectionMethod == DetectionMethodKind.StaticThreshold && Direction == KpiDirection.Either)
            errors.Add("StaticThreshold does not support Direction=Either.");
        try
        {
            using var doc = JsonDocument.Parse(MethodParamsJson);
            var root = doc.RootElement;
            switch (DetectionMethod)
            {
                case DetectionMethodKind.StaticThreshold:
                    if (!root.TryGetProperty("threshold", out var t) || t.ValueKind != JsonValueKind.Number)
                        errors.Add("StaticThreshold requires {\"threshold\": number}.");
                    break;
                case DetectionMethodKind.BaselinePercent:
                    if (!root.TryGetProperty("lookbackDays", out var d) || !d.TryGetInt32(out var days) || days is < 1 or > 28)
                        errors.Add("BaselinePercent requires lookbackDays 1..28.");
                    if (!root.TryGetProperty("thresholdPercent", out var p) || !p.TryGetDouble(out var pct) || pct <= 0)
                        errors.Add("BaselinePercent requires thresholdPercent > 0.");
                    break;
                case DetectionMethodKind.RobustZScore:
                    if (!root.TryGetProperty("windowBuckets", out var w) || !w.TryGetInt32(out var wb) || wb is < 4 or > 1000)
                        errors.Add("RobustZScore requires windowBuckets 4..1000.");
                    if (!root.TryGetProperty("zThreshold", out var z) || !z.TryGetDouble(out var zt) || zt <= 0)
                        errors.Add("RobustZScore requires zThreshold > 0.");
                    break;
            }
        }
        catch (JsonException)
        {
            errors.Add("MethodParamsJson must be valid JSON.");
        }
        return errors;
    }
}

public sealed record KpiDefinitionAuditEntry(long AuditId, int KpiDefinitionId, string SnapshotJson, DateTimeOffset ChangedAt, string ChangedBy);

/// <summary>Aggregated statistics of one time bucket. Stats are null when the bucket had no samples.</summary>
public sealed record KpiBucket(
    int KpiDefinitionId,
    DateTimeOffset BucketStart,
    long TotalCount,
    int SampledCount,
    double? P50,
    double? P95,
    double? Mean,
    double? StdDev,
    double? Value)
{
    /// <summary>Contract v4 §1. "" is the domain aggregate (v1-v3 behaviour).</summary>
    public string EntityKey { get; init; } = "";

    /// <summary>Failed observations in the bucket; null when the source does not report it.</summary>
    public long? FailedCount { get; init; }
}

public sealed record KpiSample(long SampleId, int KpiDefinitionId, DateTimeOffset BucketStart, DateTimeOffset Timestamp, double Value, string? OperationId);

public sealed record KpiDetectionState(
    int KpiDefinitionId,
    int ConsecutiveCount,
    DateTimeOffset? LastBucketEvaluated,
    DateTimeOffset? CooldownUntil,
    DateTimeOffset? LastEvaluatedAt)
{
    /// <summary>Contract v4 §1. "" is the domain aggregate (v1-v3 behaviour).</summary>
    public string EntityKey { get; init; } = "";

    public static KpiDetectionState Initial(int kpiDefinitionId) => new(kpiDefinitionId, 0, null, null, null);

    public static KpiDetectionState Initial(int kpiDefinitionId, string? entityKey) =>
        Initial(kpiDefinitionId) with { EntityKey = entityKey ?? "" };
}

public sealed record KpiAnomaly(
    long AnomalyId,
    int KpiDefinitionId,
    DateTimeOffset DetectedAt,
    DateTimeOffset BucketStart,
    double ObservedValue,
    double ReferenceValue,
    double Deviation,
    string Detection,
    Guid? InvestigationId)
{
    /// <summary>Contract v4 §1. "" is a domain anomaly (v1-v3 behaviour); otherwise the entity series that fired.</summary>
    public string EntityKey { get; init; } = "";
}

public enum KpiBucketVerdict { NotEvaluated, Normal, Anomalous }

public enum KpiEvaluationOutcome { Skipped, NoChange, CounterIncremented, CounterReset, Triggered, SuppressedByCooldown }

public sealed record KpiEvaluation(
    KpiBucket Bucket,
    KpiBucketVerdict Verdict,
    double? ObservedValue,
    double? ReferenceValue,
    double? Deviation,
    KpiDetectionState StateAfter,
    KpiEvaluationOutcome Outcome,
    string Explanation);

/// <summary>Domain-grain sources fill only the positional members (contract v1). Entity-grain sources also fill the optional stats (contract v4 §2) and return no samples.</summary>
public sealed record KpiSourceBucket(DateTimeOffset BucketStart, long TotalCount, double? Value)
{
    /// <summary>"" for domain grain.</summary>
    public string EntityKey { get; init; } = "";
    public double? P50 { get; init; }
    public double? P95 { get; init; }
    public double? Mean { get; init; }
    public long? FailedCount { get; init; }
}

public sealed record KpiSourceResult(IReadOnlyList<KpiSourceBucket> Buckets, IReadOnlyList<KpiSample> Samples);

public sealed class KpiSourceUnavailableException(string message) : Exception(message);

public sealed record PillarMonitorRunResult(int KpisEvaluated, int Triggered, int Suppressed, int Skipped);

public sealed record KpiIngestionRunResult(int KpisProcessed, int BucketsWritten, int SamplesWritten, int Skipped);

public sealed record KpiHealth(
    int KpiDefinitionId,
    string Kpi,
    string Domain,
    string Stage,
    KpiDirection Direction,
    bool Enabled,
    KpiBucket? LatestBucket,
    KpiDetectionState State,
    KpiAnomaly? LastAnomaly)
{
    /// <summary>Profile of the KPI definition.</summary>
    public string ProfileKey { get; init; } = KpiProfiles.Default;
}

/// <summary>Status is "offline" (no enabled KPI), "anomalous" (any counter > 0 or active cooldown) or "ok".</summary>
public sealed record PillarHealth(KpiPillar Pillar, string DisplayName, string Status, IReadOnlyList<KpiHealth> Kpis)
{
    /// <summary>Profile the read was scoped to (contract v3 §1.5); "*" when every profile was read, null when unscoped.</summary>
    public string? ProfileKey { get; init; }
}

public static class KpiTime
{
    /// <summary>Floors to the bucket boundary counted from the Unix epoch in UTC. Result has offset zero.</summary>
    public static DateTimeOffset FloorToBucket(DateTimeOffset value, TimeSpan bucketSize)
    {
        if (bucketSize <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(bucketSize));
        var utcTicks = value.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks;
        var floored = utcTicks - (utcTicks % bucketSize.Ticks);
        return new DateTimeOffset(DateTimeOffset.UnixEpoch.UtcTicks + floored, TimeSpan.Zero);
    }
}
