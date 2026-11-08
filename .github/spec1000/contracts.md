# Spec 1000 — Contratos congelados (v1)

> **Fuente única de verdad.** Fase 0 (F0) copia este documento **literalmente** al código. Desde el tag `spec1000-contracts-v1`, ningún agente puede modificar los archivos de contrato (lo verifica `run-gate.ps1`). Si un agente cree que un contrato está mal, **se detiene y lo reporta** al orquestador; no lo "arregla".

Archivos de contrato (lista exacta en [tasks.json](tasks.json) → `contractFiles`):

| Archivo | Sección |
|---|---|
| `Portal/src/JourneyMonitor.Core/Domain/Kpi.cs` | §1 |
| `Portal/src/JourneyMonitor.Core/Domain/Triage.cs` | §2 |
| `Portal/src/JourneyMonitor.Core/Abstractions/IKpi.cs` | §3 |
| `Portal/src/JourneyMonitor.Core/Abstractions/ITriage.cs` | §4 |
| `Portal/src/JourneyMonitor.Core/Options/HealthMonitoringOptions.cs`, `TriageOptions.cs` | §5 |
| `Portal/src/JourneyMonitor.Infrastructure/Sql/monitoring/CreateMonitoringKpi.sql` | §6 |
| `Portal/src/JourneyMonitor.Infrastructure/Sql/monitoring/CreateMonitoringTriage.sql` | §7 |
| `Portal/src/JourneyMonitor.Infrastructure/Sql/monitoring/SeedKpiDefinitions.sql` | §8 |

Además, F0 agrega constantes (no congeladas como archivo, pero sus valores sí): `AgentWorkPayloadType.TriageSignal = "triage-signal"` en `Core/Domain/Inspector.cs` y en `MonkeyKey` (`Core/Domain/Monitoring.cs`): `KpiIngestion = "kpi-ingestion"`, `ApplicationHealthMonitor = "application-health-monitor"`, `BusinessHealthMonitor = "business-health-monitor"`, `InfrastructureHealthMonitor = "infrastructure-health-monitor"`, `TriageMonkey = "triage-monkey"`.

---

## 1. `Core/Domain/Kpi.cs`

```csharp
using System.Text.Json;

namespace JourneyMonitor.Core.Domain;

public enum KpiPillar { Business, Application, Infrastructure }

/// <summary>Which movement is unhealthy. StaticThreshold does not accept Either.</summary>
public enum KpiDirection { Increase, Decrease, Either }

public enum DetectionMethodKind { StaticThreshold, BaselinePercent, RobustZScore }

/// <summary>Which bucket statistic is evaluated. Count = TotalCount, Value = source-provided value (rate, revenue...).</summary>
public enum KpiAggregation { P95, P50, Mean, Count, Value }

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
    /// <summary>Returns every rule violation; empty means valid. Same ranges as the SQL CHECK constraints.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Kpi) || Kpi.Length > 64) errors.Add("Kpi is required (max 64).");
        if (!KpiDomains.All.Contains(Domain)) errors.Add("Domain must be hotels, cars, cruises or air.");
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
    double? Value);

public sealed record KpiSample(long SampleId, int KpiDefinitionId, DateTimeOffset BucketStart, DateTimeOffset Timestamp, double Value, string? OperationId);

public sealed record KpiDetectionState(
    int KpiDefinitionId,
    int ConsecutiveCount,
    DateTimeOffset? LastBucketEvaluated,
    DateTimeOffset? CooldownUntil,
    DateTimeOffset? LastEvaluatedAt)
{
    public static KpiDetectionState Initial(int kpiDefinitionId) => new(kpiDefinitionId, 0, null, null, null);
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
    Guid? InvestigationId);

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

public sealed record KpiSourceBucket(DateTimeOffset BucketStart, long TotalCount, double? Value);

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
    KpiAnomaly? LastAnomaly);

/// <summary>Status is "offline" (no enabled KPI), "anomalous" (any counter > 0 or active cooldown) or "ok".</summary>
public sealed record PillarHealth(KpiPillar Pillar, string DisplayName, string Status, IReadOnlyList<KpiHealth> Kpis);

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
```

## 2. `Core/Domain/Triage.cs`

```csharp
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
    string Source);

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
```

## 3. `Core/Abstractions/IKpi.cs`

```csharp
using JourneyMonitor.Core.Domain;

namespace JourneyMonitor.Core.Abstractions;

public interface IKpiDefinitionStore
{
    Task<IReadOnlyList<KpiDefinition>> GetAllAsync(CancellationToken ct = default);
    Task<IReadOnlyList<KpiDefinition>> GetEnabledAsync(KpiPillar? pillar, CancellationToken ct = default);
    Task<KpiDefinition?> GetAsync(int kpiDefinitionId, CancellationToken ct = default);
    /// <summary>Insert when KpiDefinitionId == 0, otherwise update. Always appends an audit row. Returns the stored row.</summary>
    Task<KpiDefinition> UpsertAsync(KpiDefinition definition, string changedBy, CancellationToken ct = default);
    Task<IReadOnlyList<KpiDefinitionAuditEntry>> GetAuditAsync(int kpiDefinitionId, int top, CancellationToken ct = default);
}

public interface IKpiBucketStore
{
    /// <summary>Insert or replace by (KpiDefinitionId, BucketStart).</summary>
    Task UpsertAsync(IReadOnlyList<KpiBucket> buckets, CancellationToken ct = default);
    /// <summary>Half-open range [from, to), ordered by BucketStart ascending.</summary>
    Task<IReadOnlyList<KpiBucket>> GetRangeAsync(int kpiDefinitionId, DateTimeOffset fromInclusive, DateTimeOffset toExclusive, CancellationToken ct = default);
}

public interface IKpiSampleStore
{
    Task AddAsync(IReadOnlyList<KpiSample> samples, CancellationToken ct = default);
    Task<IReadOnlyList<KpiSample>> GetAsync(int kpiDefinitionId, DateTimeOffset bucketStart, int top, CancellationToken ct = default);
    Task<int> PurgeOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default);
}

public interface IKpiStateStore
{
    /// <summary>Returns KpiDetectionState.Initial(id) when no row exists.</summary>
    Task<KpiDetectionState> GetAsync(int kpiDefinitionId, CancellationToken ct = default);
    Task SaveAsync(KpiDetectionState state, CancellationToken ct = default);
}

public interface IKpiAnomalyStore
{
    /// <summary>Returns the anomaly with its generated AnomalyId.</summary>
    Task<KpiAnomaly> AddAsync(KpiAnomaly anomaly, CancellationToken ct = default);
    Task AttachInvestigationAsync(long anomalyId, Guid investigationId, CancellationToken ct = default);
    /// <summary>Newest first.</summary>
    Task<IReadOnlyList<KpiAnomaly>> GetRecentAsync(KpiPillar? pillar, string? domain, int top, CancellationToken ct = default);
}

public interface IIngestionWatermarkStore
{
    Task<DateTimeOffset?> GetAsync(int kpiDefinitionId, CancellationToken ct = default);
    Task SetAsync(int kpiDefinitionId, DateTimeOffset watermark, CancellationToken ct = default);
}

public interface IKpiSource
{
    bool CanHandle(string sourceKey);
    /// <summary>Returns bucket totals for [from, to) plus up to samplesPerBucket raw samples per bucket (0 = none). Throws KpiSourceUnavailableException when the source is not configured.</summary>
    Task<KpiSourceResult> FetchAsync(KpiDefinition definition, DateTimeOffset fromInclusive, DateTimeOffset toExclusive, int samplesPerBucket, CancellationToken ct = default);
}

public interface IKpiEvaluator
{
    /// <summary>How much history before the first evaluated bucket the evaluator needs.</summary>
    TimeSpan GetRequiredHistory(KpiDefinition definition);

    /// <summary>Pure and deterministic. Evaluates bucketsToEvaluate in ascending order, threading the state through; history holds earlier buckets.</summary>
    IReadOnlyList<KpiEvaluation> Evaluate(KpiDefinition definition, KpiDetectionState state, IReadOnlyList<KpiBucket> history, IReadOnlyList<KpiBucket> bucketsToEvaluate);
}

public interface IKpiIngestionRunner
{
    Task<KpiIngestionRunResult> RunAsync(CancellationToken ct = default);
}

public interface IPillarHealthMonitor
{
    Task<PillarMonitorRunResult> RunAsync(KpiPillar pillar, CancellationToken ct = default);
}

public interface IPillarHealthReader
{
    Task<IReadOnlyList<PillarHealth>> GetAsync(string? domain, CancellationToken ct = default);
}
```

## 4. `Core/Abstractions/ITriage.cs`

```csharp
using JourneyMonitor.Core.Domain;

namespace JourneyMonitor.Core.Abstractions;

public interface ITriageSignalSubmitter
{
    /// <summary>Creates a Queued investigation and enqueues AgentWorkPayloadType.TriageSignal, unless an open (Queued/Running) one with the same dedup key exists - then returns it with Duplicate=true.</summary>
    Task<TriageSubmission> SubmitAsync(TriageSignal signal, CancellationToken ct = default);
}

public interface IEvidenceProvider
{
    /// <summary>One of TriageDimensions.All.</summary>
    string Dimension { get; }
    Task<EvidenceSection> CollectAsync(TriageSignal signal, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken ct = default);
}

public interface ITriageInvestigationStore
{
    Task CreateAsync(TriageInvestigation investigation, CancellationToken ct = default);
    Task<TriageInvestigation?> GetAsync(Guid investigationId, CancellationToken ct = default);
    /// <summary>Only Queued or Running investigations.</summary>
    Task<TriageInvestigation?> FindOpenByDedupKeyAsync(string dedupKey, CancellationToken ct = default);
    Task UpdateStatusAsync(Guid investigationId, TriageInvestigationStatus status, string? error, CancellationToken ct = default);
    Task CompleteAsync(Guid investigationId, TriageResult result, bool notified, CancellationToken ct = default);
    /// <summary>Newest first.</summary>
    Task<IReadOnlyList<TriageInvestigation>> GetRecentAsync(int top, CancellationToken ct = default);
}
```

## 5. Options

```csharp
// Core/Options/HealthMonitoringOptions.cs
namespace JourneyMonitor.Core.Options;

public sealed class HealthMonitoringOptions
{
    public const string SectionName = "HealthMonitoring";
    public bool Enabled { get; set; }
    /// <summary>Pillars whose daemon is active. Default: Application only.</summary>
    public List<string> Pillars { get; set; } = ["Application"];
    public int MaxRowsPerCycle { get; set; } = 20_000;
    public int MaxKpisPerCycle { get; set; } = 50;
    public int SampleRetentionDays { get; set; } = 7;
}
```

```csharp
// Core/Options/TriageOptions.cs
namespace JourneyMonitor.Core.Options;

public enum TriageMode { Off, Shadow, Live }
public enum TriageEngine { Inspector, FoundryAgent }

public sealed class TriageOptions
{
    public const string SectionName = "Triage";
    public TriageMode Mode { get; set; } = TriageMode.Shadow;
    public TriageEngine Engine { get; set; } = TriageEngine.Inspector;
    /// <summary>Evidence window is [DetectedAt - EvidenceWindowMinutes, DetectedAt + 15 min].</summary>
    public int EvidenceWindowMinutes { get; set; } = 180;
    /// <summary>Agent key passed to the LLM router and the budget store.</summary>
    public string AgentKey { get; set; } = "triage-monkey";
}
```

Configuración (env vars con `__`): `HealthMonitoring__Enabled`, `HealthMonitoring__Pillars__0`, `HealthMonitoring__MaxRowsPerCycle`, `spec1000_ingestion_cron` (default `0 */1 * * * *`), `spec1000_monitor_cron` (default `30 */1 * * * *`) — nombres planos como el existente `platform_reliability_cron_setting`; **deben existir** en `local.settings.json`, compose y QA o el host de Functions no arranca, `Triage__Mode`, `Triage__Engine`, `Triage__EvidenceWindowMinutes`.

## 6. `CreateMonitoringKpi.sql`

Reglas: idempotente (`IF OBJECT_ID(...) IS NULL` para tablas, `CREATE OR ALTER` para procedimientos), `GO` entre lotes, mismo estilo que `CreateMonitoringSettings.sql`. Fechas `datetimeoffset(7)`. JSON de entrada para lotes vía `OPENJSON`.

```sql
IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'monitoring')
    EXEC(N'CREATE SCHEMA monitoring');
GO

IF OBJECT_ID(N'monitoring.KpiDefinition', N'U') IS NULL
BEGIN
    CREATE TABLE monitoring.KpiDefinition
    (
        KpiDefinitionId          int               NOT NULL IDENTITY(1, 1) CONSTRAINT PK_monitoring_KpiDefinition PRIMARY KEY,
        Pillar                   nvarchar(16)      NOT NULL,
        Kpi                      nvarchar(64)      NOT NULL,
        Domain                   nvarchar(32)      NOT NULL,
        Stage                    nvarchar(64)      NOT NULL CONSTRAINT DF_monitoring_KpiDefinition_Stage DEFAULT N'',
        SourceKey                nvarchar(64)      NOT NULL,
        Direction                nvarchar(16)      NOT NULL,
        Aggregation              nvarchar(8)       NOT NULL,
        EvaluationFrequencySec   int               NOT NULL,
        BucketSizeSec            int               NOT NULL,
        LookbackWindowSec        int               NOT NULL,
        SampleSize               int               NOT NULL,
        MinObservationsPerBucket int               NOT NULL,
        DetectionMethod          nvarchar(32)      NOT NULL,
        MethodParamsJson         nvarchar(1000)    NOT NULL,
        ConsecutiveCount         int               NOT NULL,
        CooldownMinutes          int               NOT NULL,
        Enabled                  bit               NOT NULL CONSTRAINT DF_monitoring_KpiDefinition_Enabled DEFAULT 0,
        UpdatedAt                datetimeoffset(7) NOT NULL CONSTRAINT DF_monitoring_KpiDefinition_UpdatedAt DEFAULT SYSDATETIMEOFFSET(),
        UpdatedBy                nvarchar(128)     NOT NULL CONSTRAINT DF_monitoring_KpiDefinition_UpdatedBy DEFAULT N'',
        CONSTRAINT UQ_monitoring_KpiDefinition_Key UNIQUE (Pillar, Kpi, Domain, Stage),
        CONSTRAINT CK_monitoring_KpiDefinition_Pillar CHECK (Pillar IN (N'Business', N'Application', N'Infrastructure')),
        CONSTRAINT CK_monitoring_KpiDefinition_Domain CHECK (Domain IN (N'hotels', N'cars', N'cruises', N'air')),
        CONSTRAINT CK_monitoring_KpiDefinition_Direction CHECK (Direction IN (N'Increase', N'Decrease', N'Either')),
        CONSTRAINT CK_monitoring_KpiDefinition_Aggregation CHECK (Aggregation IN (N'P95', N'P50', N'Mean', N'Count', N'Value')),
        CONSTRAINT CK_monitoring_KpiDefinition_Method CHECK (DetectionMethod IN (N'StaticThreshold', N'BaselinePercent', N'RobustZScore')),
        CONSTRAINT CK_monitoring_KpiDefinition_Freq CHECK (EvaluationFrequencySec >= 60),
        CONSTRAINT CK_monitoring_KpiDefinition_Bucket CHECK (BucketSizeSec >= 60),
        CONSTRAINT CK_monitoring_KpiDefinition_Lookback CHECK (LookbackWindowSec >= BucketSizeSec),
        CONSTRAINT CK_monitoring_KpiDefinition_Sample CHECK (SampleSize BETWEEN 0 AND 100000),
        CONSTRAINT CK_monitoring_KpiDefinition_MinObs CHECK (MinObservationsPerBucket >= 0),
        CONSTRAINT CK_monitoring_KpiDefinition_Consecutive CHECK (ConsecutiveCount BETWEEN 1 AND 48),
        CONSTRAINT CK_monitoring_KpiDefinition_Cooldown CHECK (CooldownMinutes BETWEEN 0 AND 10080),
        CONSTRAINT CK_monitoring_KpiDefinition_Params CHECK (ISJSON(MethodParamsJson) = 1),
        CONSTRAINT CK_monitoring_KpiDefinition_StaticEither CHECK (NOT (DetectionMethod = N'StaticThreshold' AND Direction = N'Either'))
    );
END
GO

IF OBJECT_ID(N'monitoring.KpiDefinitionAudit', N'U') IS NULL
BEGIN
    CREATE TABLE monitoring.KpiDefinitionAudit
    (
        AuditId          bigint            NOT NULL IDENTITY(1, 1) CONSTRAINT PK_monitoring_KpiDefinitionAudit PRIMARY KEY,
        KpiDefinitionId  int               NOT NULL,
        SnapshotJson     nvarchar(max)     NOT NULL,
        ChangedAt        datetimeoffset(7) NOT NULL CONSTRAINT DF_monitoring_KpiDefinitionAudit_ChangedAt DEFAULT SYSDATETIMEOFFSET(),
        ChangedBy        nvarchar(128)     NOT NULL CONSTRAINT DF_monitoring_KpiDefinitionAudit_ChangedBy DEFAULT N''
    );
    CREATE INDEX IX_monitoring_KpiDefinitionAudit_Read ON monitoring.KpiDefinitionAudit (KpiDefinitionId, ChangedAt DESC);
END
GO

IF OBJECT_ID(N'monitoring.KpiBucket', N'U') IS NULL
BEGIN
    CREATE TABLE monitoring.KpiBucket
    (
        KpiDefinitionId int               NOT NULL,
        BucketStart     datetimeoffset(7) NOT NULL,
        TotalCount      bigint            NOT NULL,
        SampledCount    int               NOT NULL,
        P50             float             NULL,
        P95             float             NULL,
        Mean            float             NULL,
        StdDev          float             NULL,
        Value           float             NULL,
        IngestedAt      datetimeoffset(7) NOT NULL CONSTRAINT DF_monitoring_KpiBucket_IngestedAt DEFAULT SYSDATETIMEOFFSET(),
        CONSTRAINT PK_monitoring_KpiBucket PRIMARY KEY (KpiDefinitionId, BucketStart)
    );
END
GO

IF OBJECT_ID(N'monitoring.KpiSample', N'U') IS NULL
BEGIN
    CREATE TABLE monitoring.KpiSample
    (
        SampleId        bigint            NOT NULL IDENTITY(1, 1) CONSTRAINT PK_monitoring_KpiSample PRIMARY KEY,
        KpiDefinitionId int               NOT NULL,
        BucketStart     datetimeoffset(7) NOT NULL,
        [Timestamp]     datetimeoffset(7) NOT NULL,
        Value           float             NOT NULL,
        OperationId     nvarchar(64)      NULL
    );
    CREATE INDEX IX_monitoring_KpiSample_Bucket ON monitoring.KpiSample (KpiDefinitionId, BucketStart);
    CREATE INDEX IX_monitoring_KpiSample_Purge ON monitoring.KpiSample (BucketStart);
END
GO

IF OBJECT_ID(N'monitoring.KpiDetectionState', N'U') IS NULL
BEGIN
    CREATE TABLE monitoring.KpiDetectionState
    (
        KpiDefinitionId     int               NOT NULL CONSTRAINT PK_monitoring_KpiDetectionState PRIMARY KEY,
        ConsecutiveCount    int               NOT NULL,
        LastBucketEvaluated datetimeoffset(7) NULL,
        CooldownUntil       datetimeoffset(7) NULL,
        LastEvaluatedAt     datetimeoffset(7) NULL,
        UpdatedAt           datetimeoffset(7) NOT NULL CONSTRAINT DF_monitoring_KpiDetectionState_UpdatedAt DEFAULT SYSDATETIMEOFFSET()
    );
END
GO

IF OBJECT_ID(N'monitoring.KpiAnomaly', N'U') IS NULL
BEGIN
    CREATE TABLE monitoring.KpiAnomaly
    (
        AnomalyId       bigint            NOT NULL IDENTITY(1, 1) CONSTRAINT PK_monitoring_KpiAnomaly PRIMARY KEY,
        KpiDefinitionId int               NOT NULL,
        DetectedAt      datetimeoffset(7) NOT NULL,
        BucketStart     datetimeoffset(7) NOT NULL,
        ObservedValue   float             NOT NULL,
        ReferenceValue  float             NOT NULL,
        Deviation       float             NOT NULL,
        Detection       nvarchar(400)     NOT NULL,
        InvestigationId uniqueidentifier  NULL
    );
    CREATE INDEX IX_monitoring_KpiAnomaly_Recent ON monitoring.KpiAnomaly (DetectedAt DESC) INCLUDE (KpiDefinitionId);
END
GO

IF OBJECT_ID(N'monitoring.IngestionWatermark', N'U') IS NULL
BEGIN
    CREATE TABLE monitoring.IngestionWatermark
    (
        KpiDefinitionId int               NOT NULL CONSTRAINT PK_monitoring_IngestionWatermark PRIMARY KEY,
        Watermark       datetimeoffset(7) NOT NULL,
        UpdatedAt       datetimeoffset(7) NOT NULL CONSTRAINT DF_monitoring_IngestionWatermark_UpdatedAt DEFAULT SYSDATETIMEOFFSET()
    );
END
GO
```

Procedimientos (todos `CREATE OR ALTER PROCEDURE monitoring.<nombre>`, `SET NOCOUNT ON`). Las columnas devueltas usan **exactamente** los nombres de las tablas para que Dapper mapee 1:1.

| Procedimiento | Parámetros | Devuelve / efecto |
|---|---|---|
| `usp_GetKpiDefinitions` | `@Pillar nvarchar(16) = NULL, @OnlyEnabled bit = 0` | Todas las columnas de `KpiDefinition`, filtradas, `ORDER BY KpiDefinitionId` |
| `usp_GetKpiDefinition` | `@KpiDefinitionId int` | 0 o 1 fila |
| `usp_UpsertKpiDefinition` | Todas las columnas editables + `@KpiDefinitionId int` (0 = insert) + `@ChangedBy nvarchar(128)` | En transacción: insert/update, `UpdatedAt = SYSDATETIMEOFFSET()`, `UpdatedBy = @ChangedBy`, inserta en `KpiDefinitionAudit` el snapshot `FOR JSON PATH, WITHOUT_ARRAY_WRAPPER`; devuelve la fila resultante |
| `usp_GetKpiDefinitionAudit` | `@KpiDefinitionId int, @Top int = 200` | Más reciente primero |
| `usp_UpsertKpiBuckets` | `@BucketsJson nvarchar(max)` — array `[{kpiDefinitionId, bucketStart, totalCount, sampledCount, p50, p95, mean, stdDev, value}]` | `MERGE` por PK; actualiza `IngestedAt` |
| `usp_GetKpiBuckets` | `@KpiDefinitionId int, @From datetimeoffset(7), @To datetimeoffset(7)` | `BucketStart >= @From AND BucketStart < @To ORDER BY BucketStart` |
| `usp_AddKpiSamples` | `@SamplesJson nvarchar(max)` — `[{kpiDefinitionId, bucketStart, timestamp, value, operationId}]` | Insert |
| `usp_GetKpiSamples` | `@KpiDefinitionId int, @BucketStart datetimeoffset(7), @Top int` | Por `[Timestamp]` |
| `usp_PurgeKpiSamples` | `@Cutoff datetimeoffset(7)` | Borra `BucketStart < @Cutoff`; `SELECT @@ROWCOUNT AS Deleted` |
| `usp_GetKpiDetectionState` | `@KpiDefinitionId int` | 0 o 1 fila |
| `usp_SaveKpiDetectionState` | `@KpiDefinitionId, @ConsecutiveCount, @LastBucketEvaluated, @CooldownUntil, @LastEvaluatedAt` | Upsert |
| `usp_AddKpiAnomaly` | Todas las columnas menos `AnomalyId` | `SELECT CAST(SCOPE_IDENTITY() AS bigint) AS AnomalyId` |
| `usp_AttachKpiAnomalyInvestigation` | `@AnomalyId bigint, @InvestigationId uniqueidentifier` | Update |
| `usp_GetRecentKpiAnomalies` | `@Pillar nvarchar(16) = NULL, @Domain nvarchar(32) = NULL, @Top int = 100` | Join con `KpiDefinition` para filtrar; columnas de `KpiAnomaly`; `ORDER BY DetectedAt DESC` |
| `usp_GetIngestionWatermark` | `@KpiDefinitionId int` | `Watermark` o sin filas |
| `usp_SetIngestionWatermark` | `@KpiDefinitionId int, @Watermark datetimeoffset(7)` | Upsert |

## 7. `CreateMonitoringTriage.sql`

```sql
IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'monitoring')
    EXEC(N'CREATE SCHEMA monitoring');
GO

IF OBJECT_ID(N'monitoring.TriageInvestigation', N'U') IS NULL
BEGIN
    CREATE TABLE monitoring.TriageInvestigation
    (
        InvestigationId uniqueidentifier  NOT NULL CONSTRAINT PK_monitoring_TriageInvestigation PRIMARY KEY,
        DedupKey        nvarchar(256)     NOT NULL,
        AnomalyId       bigint            NULL,
        SignalJson      nvarchar(max)     NOT NULL,
        Source          nvarchar(16)      NOT NULL,
        Status          nvarchar(16)      NOT NULL,
        ResultJson      nvarchar(max)     NULL,
        AlertLevel      nvarchar(16)      NULL,
        Notified        bit               NOT NULL CONSTRAINT DF_monitoring_TriageInvestigation_Notified DEFAULT 0,
        CreatedAt       datetimeoffset(7) NOT NULL CONSTRAINT DF_monitoring_TriageInvestigation_CreatedAt DEFAULT SYSDATETIMEOFFSET(),
        CompletedAt     datetimeoffset(7) NULL,
        Error           nvarchar(2000)    NULL,
        CONSTRAINT CK_monitoring_TriageInvestigation_Source CHECK (Source IN (N'daemon', N'manual')),
        CONSTRAINT CK_monitoring_TriageInvestigation_Status CHECK (Status IN (N'Queued', N'Running', N'Completed', N'Failed', N'Discarded')),
        CONSTRAINT CK_monitoring_TriageInvestigation_AlertLevel CHECK (AlertLevel IS NULL OR AlertLevel IN (N'red_alert', N'amber', N'informational')),
        CONSTRAINT CK_monitoring_TriageInvestigation_SignalJson CHECK (ISJSON(SignalJson) = 1),
        CONSTRAINT CK_monitoring_TriageInvestigation_ResultJson CHECK (ResultJson IS NULL OR ISJSON(ResultJson) = 1)
    );
    CREATE UNIQUE INDEX UX_monitoring_TriageInvestigation_OpenDedup
        ON monitoring.TriageInvestigation (DedupKey) WHERE Status IN (N'Queued', N'Running');
    CREATE INDEX IX_monitoring_TriageInvestigation_Recent ON monitoring.TriageInvestigation (CreatedAt DESC);
END
GO
```

| Procedimiento | Parámetros | Devuelve / efecto |
|---|---|---|
| `usp_CreateTriageInvestigation` | `@InvestigationId, @DedupKey, @AnomalyId, @SignalJson, @Source` | Insert con `Status = 'Queued'` |
| `usp_GetTriageInvestigation` | `@InvestigationId uniqueidentifier` | 0 o 1 fila, todas las columnas |
| `usp_FindOpenTriageInvestigationByDedupKey` | `@DedupKey nvarchar(256)` | Fila con `Status IN ('Queued','Running')` |
| `usp_UpdateTriageInvestigationStatus` | `@InvestigationId, @Status, @Error nvarchar(2000) = NULL` | Update; si `Status IN ('Failed','Discarded')` también `CompletedAt` |
| `usp_CompleteTriageInvestigation` | `@InvestigationId, @ResultJson, @AlertLevel, @Notified bit` | `Status = 'Completed'`, `CompletedAt = SYSDATETIMEOFFSET()` |
| `usp_GetRecentTriageInvestigations` | `@Top int = 50` | `ORDER BY CreatedAt DESC` |

## 8. `SeedKpiDefinitions.sql`

`MERGE monitoring.KpiDefinition` por `(Pillar, Kpi, Domain, Stage)`: **inserta si no existe, no pisa filas existentes** (los cambios hechos desde Settings se conservan). 24 filas = 6 KPIs × 4 dominios, todas con `Stage = ''`, `UpdatedBy = 'seed'`.

| Pillar | Kpi | SourceKey | Direction | Aggregation | Freq s | Bucket s | Lookback s | Sample | MinObs | Method | Params | Consec. | Cooldown | Enabled |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| Application | Latency | `appinsights:latency` | Increase | P95 | 300 | 3600 | 86400 | 4000 | 30 | BaselinePercent | `{"lookbackDays":7,"thresholdPercent":50}` | 2 | 120 | 1 solo `hotels` |
| Application | Exceptions | `appinsights:exceptions` | Increase | Count | 300 | 3600 | 86400 | 0 | 0 | RobustZScore | `{"windowBuckets":24,"zThreshold":3}` | 2 | 120 | 1 solo `hotels` |
| Business | Bookings | `fabric:bookings` | Decrease | Count | 3600 | 3600 | 604800 | 0 | 20 | BaselinePercent | `{"lookbackDays":7,"thresholdPercent":30}` | 3 | 240 | 0 |
| Business | Revenue | `fabric:revenue` | Decrease | Value | 3600 | 3600 | 604800 | 0 | 20 | BaselinePercent | `{"lookbackDays":7,"thresholdPercent":30}` | 3 | 240 | 0 |
| Business | ConversionRate | `fabric:conversion` | Decrease | Value | 3600 | 3600 | 604800 | 0 | 20 | BaselinePercent | `{"lookbackDays":7,"thresholdPercent":30}` | 3 | 240 | 0 |
| Business | CancellationRate | `fabric:cancellation` | Increase | Value | 3600 | 3600 | 604800 | 0 | 20 | BaselinePercent | `{"lookbackDays":7,"thresholdPercent":30}` | 3 | 240 | 0 |

## 9. Semántica del evaluador (A1 la implementa; los tests la verifican)

Para cada bucket de `bucketsToEvaluate` en orden ascendente, partiendo de `state`:

1. **Valor observado** según `Aggregation`: `P95`/`P50`/`Mean` → campo homónimo; `Count` → `TotalCount`; `Value` → `Value`.
2. **Datos mínimos:** si `TotalCount < MinObservationsPerBucket` o el valor observado es `null` → `Verdict = NotEvaluated`, `Outcome = Skipped`, el contador **no cambia**, `LastBucketEvaluated = BucketStart`.
3. **Referencia y desvío:**
   - `StaticThreshold {threshold}`: referencia = `threshold`; desvío = `observed - threshold`.
   - `BaselinePercent {lookbackDays, thresholdPercent}`: buckets de `history` con `BucketStart = actual - k días` (k = 1..lookbackDays) y valor no nulo; se necesitan **al menos `min(3, lookbackDays)`**, si no → NotEvaluated/Skipped. Referencia = **mediana**. Si referencia `<= 0` → NotEvaluated/Skipped. Desvío = `(observed - ref) / ref * 100`.
   - `RobustZScore {windowBuckets, zThreshold}`: los `windowBuckets` buckets inmediatamente anteriores (de `history` + buckets ya evaluados en esta llamada) con valor no nulo; se necesitan **al menos `ceil(windowBuckets / 2)`**. Mediana `m`, `MAD = mediana(|x - m|)`, escala `= 1.4826 * MAD`; si escala `== 0` → NotEvaluated/Skipped. Desvío = `(observed - m) / escala`; referencia = `m`.
4. **¿Anómalo?** (comparación estricta, el igual es normal) con umbral `t` (`threshold` en estático, `thresholdPercent`, `zThreshold`):
   - StaticThreshold: `Increase` → `observed > threshold`; `Decrease` → `observed < threshold`.
   - Otros: `Increase` → `desvío > t`; `Decrease` → `desvío < -t`; `Either` → `|desvío| > t`.
5. **Normal:** si el contador era `> 0` → `CounterReset`, si no → `NoChange`; contador = 0.
6. **Anómalo:** contador + 1. `bucketEnd = BucketStart + BucketSize`.
   - Si `CooldownUntil > bucketEnd` → `SuppressedByCooldown` (el contador igual sube).
   - Si no, si contador `>= ConsecutiveCount` → `Triggered` y `CooldownUntil = bucketEnd + CooldownMinutes`.
   - Si no → `CounterIncremented`.
7. Siempre `LastBucketEvaluated = BucketStart`.

`GetRequiredHistory`: Static → `0`; BaselinePercent → `lookbackDays` días; RobustZScore → `windowBuckets × BucketSize`.

**Texto `Detection`** (también usado como `TriageTrigger.Condition`):
`"{ConsecutiveCount} consecutive {increases|decreases|deviations}: {Kpi} {observed:0.##} vs reference {ref:0.##} ({desvío})"` donde `{desvío}` es `+92.0%` (BaselinePercent, 1 decimal con signo), `z=+5.0` (RobustZScore), o `threshold {threshold:0.##}` (Static). Cultura invariante.

### Monitor por pilar (`PillarHealthMonitorService : IPillarHealthMonitor`)

Usa `TimeProvider` para "ahora". Por corrida:

1. Si `HealthMonitoringOptions.Enabled == false` o el pilar no está en `Pillars` → devuelve `(0,0,0,0)` sin tocar stores.
2. `GetEnabledAsync(pillar)`; toma como máximo `MaxKpisPerCycle`.
3. **Vencido:** se evalúa solo si `LastEvaluatedAt == null` o `now - LastEvaluatedAt >= EvaluationFrequency`; si no, cuenta como `Skipped`.
4. **Buckets a evaluar:** cerrados (`BucketStart + BucketSize <= now`) **y** completamente ingeridos (`BucketStart + BucketSize <= watermark`); sin watermark → nada. Con `LastBucketEvaluated == null` → **solo el último** bucket cerrado e ingerido; si no → todos los posteriores a `LastBucketEvaluated`.
5. Historia = `GetRangeAsync(desde primerBucket - GetRequiredHistory, hasta primerBucket)`.
6. Por cada `Triggered`: `AddAsync` de `KpiAnomaly` (`DetectedAt = now`), luego `ITriageSignalSubmitter.SubmitAsync(new TriageSignal(now, KpiPillarNames.ToDisplay(pillar), Kpi, Detection, Domain, Stage == "" ? null : Stage, anomalyId, "daemon"))` y `AttachInvestigationAsync`.
7. Guarda el estado final con `LastEvaluatedAt = now`.

## 10. API HTTP (A4 implementa; A7 consume vía proxy)

| Método y ruta | Seguridad | Request | Respuestas |
|---|---|---|---|
| `POST /api/triage/investigations` | `X-Internal-Api-Secret` (fail-closed, igual que `RunMonkeyNow`) | `{detectedAt, pillar, kpi, detection, domain?, stage?}`; `pillar` acepta display name o enum; `Source = "manual"` | 401 sin secreto; 400 con `{errors:[...]}`; **202** `{investigationId, duplicate}` |
| `GET /api/triage/investigations?top=50` | anónimo | — | 200 `[TriageInvestigation]` (`TriageJson.Options`) |
| `GET /api/triage/investigations/{id:guid}` | anónimo | — | 200 / 404 |
| `GET /api/health/pillars?domain=` | anónimo | — | 200 `[PillarHealth]` |
| `GET /api/health/anomalies?pillar=&domain=&top=100` | anónimo | — | 200 `[KpiAnomaly]` |
| `GET /api/health/kpis/{id:int}/buckets?hours=48` | anónimo | — | 200 `[KpiBucket]` / 404 |
| `GET /api/settings/kpis` | anónimo | — | 200 `[KpiDefinition]` |
| `GET /api/settings/kpis/{id:int}/audit?top=200` | anónimo | — | 200 `[KpiDefinitionAuditEntry]` |
| `POST /api/settings/kpis` | `X-Settings-Write-Secret` | `KpiDefinition` JSON + `changedBy` | 401; 400 con `Validate()`; 200 fila guardada |
| `POST /api/monkeys/{id}/run` (existente) | `X-Internal-Api-Secret` | ids nuevos: `kpi-ingestion`, `application-health-monitor`, `business-health-monitor`, `infrastructure-health-monitor` | 200 con el resultado del runner |

Timers: `KpiIngestion` (`%spec1000_ingestion_cron%`), `ApplicationHealthMonitor`, `BusinessHealthMonitor`, `InfrastructureHealthMonitor` (`%spec1000_monitor_cron%`). Todos retornan sin trabajo si `HealthMonitoring:Enabled=false`.
