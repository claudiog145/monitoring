using System.Text.Json;
using System.Text.Json.Serialization;

namespace JourneyMonitor.Core.Domain;

/// <summary>Minimal trigger package from a daemon (Source="daemon") or a person (Source="manual"). Pillar uses display names ("Application Health").</summary>
public sealed record TriageSignal(
    DateTimeOffset DetectedAt,
    string Pillar,
    string Kpi,
    string Detection,
    string? Domain,
    string? Stage,
    long? AnomalyId,
    string Source,
    string? ProfileKey = null);

public static class TriageSignalSources
{
    public const string Daemon = "daemon";
    public const string Manual = "manual";
}

public sealed record TriageTrigger(DateTimeOffset DetectedAt, string Pillar, string Kpi, string? Domain, string? Stage, string Condition);

public sealed record TriageCorrelation(
    JsonElement? BusinessHealth,
    JsonElement? ApplicationHealth,
    JsonElement? InfrastructureHealth,
    JsonElement? CodeReliability,
    JsonElement? RecentChanges,
    JsonElement? KnowledgeBase);

public static class TriageAlertLevels
{
    public const string RedAlert = "red_alert";
    public const string Amber = "amber";
    public const string Informational = "informational";
    public static readonly IReadOnlyList<string> All = [RedAlert, Amber, Informational];
}

public sealed record TriageAssessment(
    string AlertLevel,
    bool PossibleRedAlert,
    double? Confidence,
    string Summary,
    IReadOnlyList<string> RecommendedNextActions);

/// <summary>Serialized with <see cref="TriageJson.Options"/>, this is exactly the JSON shape in Specification 1000 §8.</summary>
public sealed record TriageResult(Guid InvestigationId, TriageTrigger Trigger, TriageCorrelation Correlation, TriageAssessment Assessment);

public enum TriageInvestigationStatus { Queued, Running, Completed, Failed, Discarded }

public sealed record TriageInvestigation(
    Guid InvestigationId,
    string DedupKey,
    long? AnomalyId,
    TriageSignal Signal,
    TriageInvestigationStatus Status,
    TriageResult? Result,
    string? AlertLevel,
    bool Notified,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    string? Error);

public static class TriageDimensions
{
    public const string BusinessHealth = "businessHealth";
    public const string ApplicationHealth = "applicationHealth";
    public const string InfrastructureHealth = "infrastructureHealth";
    public const string CodeReliability = "codeReliability";
    public const string RecentChanges = "recentChanges";
    public const string KnowledgeBase = "knowledgeBase";
    public static readonly IReadOnlyList<string> All =
        [BusinessHealth, ApplicationHealth, InfrastructureHealth, CodeReliability, RecentChanges, KnowledgeBase];
}

public sealed record EvidenceSection(string Dimension, bool Available, JsonElement? Data, string? UnavailableReason);

public sealed record TriageSubmission(Guid InvestigationId, bool Duplicate);

public static class TriageDedupKey
{
    /// <summary>"anomaly:{id}" when the signal comes from a recorded anomaly; otherwise pillar|kpi|domain|stage|hour (UTC, yyyyMMddHH), lower-case.</summary>
    public static string For(TriageSignal signal)
    {
        if (signal.AnomalyId is { } id) return $"anomaly:{id}";
        var hour = KpiTime.FloorToBucket(signal.DetectedAt, TimeSpan.FromHours(1)).ToString("yyyyMMddHH");
        return $"{signal.Pillar}|{signal.Kpi}|{signal.Domain ?? ""}|{signal.Stage ?? ""}|{hour}".ToLowerInvariant();
    }
}

public static class TriageJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}
