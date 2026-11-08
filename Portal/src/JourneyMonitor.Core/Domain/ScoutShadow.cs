namespace JourneyMonitor.Core.Domain;

/// <summary>
/// Contract v4 §4: one (EntityKey, Stage, Bucket) seen by the Scout's fixed rules, by the entity
/// detection method, or by both, in one Scout cycle. Rows where neither side flags anything are
/// never stored. RuleRank is null when the board did not flag the bucket (method-only);
/// KpiAnomalyId is null when no entity anomaly covers it (rules-only).
/// </summary>
public sealed record ScoutShadowComparison(
    Guid RunId,
    string Board,
    string ProfileKey,
    string EntityKey,
    string Stage,
    DateTimeOffset BucketStart,
    int? RuleRank,
    string MethodVerdict,
    long? KpiAnomalyId,
    string Agreement);

public static class ScoutShadowAgreement
{
    public const string Both = "both";
    public const string RulesOnly = "rules-only";
    public const string MethodOnly = "method-only";

    public static readonly IReadOnlyList<string> All = [Both, RulesOnly, MethodOnly];
}

public static class ScoutShadowMethodVerdict
{
    public const string Anomalous = "anomalous";
    public const string Normal = "normal";
    public const string NotEvaluated = "not-evaluated";

    public static readonly IReadOnlyList<string> All = [Anomalous, Normal, NotEvaluated];
}

/// <summary>Per-agreement totals of a board's shadow rows in a window.</summary>
public sealed record ScoutShadowCounts(int Both, int RulesOnly, int MethodOnly)
{
    public int Total => Both + RulesOnly + MethodOnly;
}
