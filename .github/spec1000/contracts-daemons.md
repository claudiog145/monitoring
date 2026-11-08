# Spec 1000 — Contratos de monitoreo de daemons (v2, aditivo)

> Extiende [contracts.md](contracts.md) sin modificarlo. D1 copia este documento **literalmente** al código. Tras el PASS de D1 el orquestador crea el tag `spec1000-contracts-v2` y agrega estos archivos a `contractFiles`; D2 y D3 no pueden cambiarlos.

Archivos nuevos de contrato:

| Archivo | Sección |
|---|---|
| `Portal/src/JourneyMonitor.Core/Domain/DaemonActivity.cs` | §1 |
| `Portal/src/JourneyMonitor.Core/Abstractions/IDaemonActivity.cs` | §2 |
| `Portal/src/JourneyMonitor.Core/Options/DaemonMonitoringOptions.cs` | §3 |
| `Portal/src/JourneyMonitor.Infrastructure/Sql/monitoring/CreateMonitoringDaemonActivity.sql` | §4 |

## 1. `Core/Domain/DaemonActivity.cs`

```csharp
namespace JourneyMonitor.Core.Domain;

/// <summary>The Spec 1000 background daemons that report activity. Values equal the existing MonkeyKey constants.</summary>
public static class DaemonKeys
{
    public const string KpiIngestion = MonkeyKey.KpiIngestion;
    public const string ApplicationHealthMonitor = MonkeyKey.ApplicationHealthMonitor;
    public const string BusinessHealthMonitor = MonkeyKey.BusinessHealthMonitor;
    public const string InfrastructureHealthMonitor = MonkeyKey.InfrastructureHealthMonitor;
    public const string TriageMonkey = MonkeyKey.TriageMonkey;

    public static readonly IReadOnlyList<string> All =
        [KpiIngestion, ApplicationHealthMonitor, BusinessHealthMonitor, InfrastructureHealthMonitor, TriageMonkey];

    public static string DisplayName(string daemonKey) => daemonKey switch
    {
        KpiIngestion => "KPI Ingestion",
        ApplicationHealthMonitor => "Application Health Monitor",
        BusinessHealthMonitor => "Business Health Monitor",
        InfrastructureHealthMonitor => "Infrastructure Health Monitor",
        TriageMonkey => "Triage Monkey",
        _ => daemonKey,
    };

    public static string? ForPillar(KpiPillar pillar) => pillar switch
    {
        KpiPillar.Application => ApplicationHealthMonitor,
        KpiPillar.Business => BusinessHealthMonitor,
        KpiPillar.Infrastructure => InfrastructureHealthMonitor,
        _ => null,
    };
}

public enum DaemonActivityLevel { Info, Warning, Error }

/// <summary>Event kinds written to the activity log. Kept as strings so new kinds never need a schema change.</summary>
public static class DaemonActivityKinds
{
    public const string RunStarted = "run-started";
    public const string RunCompleted = "run-completed";
    public const string RunSkipped = "run-skipped";
    public const string IngestionKpi = "ingestion-kpi";
    public const string KpiEvaluated = "kpi-evaluated";
    public const string Anomaly = "anomaly";
    public const string TriageSubmitted = "triage-submitted";
    public const string InvestigationStarted = "investigation-started";
    public const string InvestigationCompleted = "investigation-completed";
    public const string Error = "error";
}

/// <summary>One line of the daemon console. ActivityId is assigned by the store (ignored on append). Message max 1000 chars.</summary>
public sealed record DaemonActivityEntry(
    long ActivityId,
    string DaemonKey,
    Guid RunId,
    DateTimeOffset At,
    DaemonActivityLevel Level,
    string Kind,
    string Message,
    string? DataJson);

public sealed record DaemonActivityPage(IReadOnlyList<DaemonActivityEntry> Entries, long LastId);

public static class DaemonHealthStates
{
    public const string Disabled = "disabled";
    public const string Running = "running";
    public const string Ok = "ok";
    public const string Stale = "stale";
    public const string Error = "error";
    public const string NeverRun = "never-run";
}

/// <summary>Card shown per daemon. Health is one of DaemonHealthStates.</summary>
public sealed record DaemonStatus(
    string DaemonKey,
    string DisplayName,
    string Schedule,
    bool Enabled,
    string? Mode,
    DateTimeOffset? LastRunAt,
    string? LastRunSummary,
    DaemonActivityLevel? LastRunLevel,
    DateTimeOffset? LastErrorAt,
    string? LastErrorMessage,
    string Health);
```

## 2. `Core/Abstractions/IDaemonActivity.cs`

```csharp
using JourneyMonitor.Core.Domain;

namespace JourneyMonitor.Core.Abstractions;

public interface IDaemonActivityLog
{
    /// <summary>Appends entries in order. Implementations must truncate Message to 1000 chars. ActivityId is ignored.</summary>
    Task AppendAsync(IReadOnlyList<DaemonActivityEntry> entries, CancellationToken ct = default);

    /// <summary>
    /// Entries with ActivityId &gt; afterId, ascending by ActivityId, at most top (1..1000).
    /// When afterId == 0 returns the LATEST top entries (still ascending). Filters are optional.
    /// </summary>
    Task<DaemonActivityPage> GetAsync(long afterId, string? daemonKey, DaemonActivityLevel? minLevel, int top, CancellationToken ct = default);

    Task<int> PurgeOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default);
}

/// <summary>Best-effort writer used by the daemons: never throws, never blocks a run on a logging failure.</summary>
public interface IDaemonActivityRecorder
{
    /// <summary>Starts a run scope; entries written through it share the RunId.</summary>
    IDaemonRun BeginRun(string daemonKey);
}

public interface IDaemonRun
{
    Guid RunId { get; }
    string DaemonKey { get; }
    void Info(string kind, string message, object? data = null);
    void Warning(string kind, string message, object? data = null);
    void Error(string kind, string message, object? data = null);
    /// <summary>Flushes buffered entries (best-effort, swallows and logs failures).</summary>
    Task FlushAsync(CancellationToken ct = default);
}

public interface IDaemonStatusReader
{
    /// <summary>Always returns one DaemonStatus per DaemonKeys.All, in that order.</summary>
    Task<IReadOnlyList<DaemonStatus>> GetAsync(CancellationToken ct = default);
}
```

## 3. `Core/Options/DaemonMonitoringOptions.cs`

```csharp
namespace JourneyMonitor.Core.Options;

public sealed class DaemonMonitoringOptions
{
    public const string SectionName = "DaemonMonitoring";
    /// <summary>A daemon whose last run is older than this is "stale".</summary>
    public int StaleAfterMinutes { get; set; } = 5;
    /// <summary>A run-started without run-completed younger than this is "running".</summary>
    public int RunningWindowMinutes { get; set; } = 3;
    public int RetentionDays { get; set; } = 7;
    /// <summary>Shown on the cards; filled from the cron app settings by DI.</summary>
    public string IngestionSchedule { get; set; } = "0 */1 * * * *";
    public string MonitorSchedule { get; set; } = "30 */1 * * * *";
    public string TriageSchedule { get; set; } = "0 */1 * * * *";
}
```

## 4. `CreateMonitoringDaemonActivity.sql`

```sql
IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'monitoring')
    EXEC(N'CREATE SCHEMA monitoring');
GO

IF OBJECT_ID(N'monitoring.DaemonActivity', N'U') IS NULL
BEGIN
    CREATE TABLE monitoring.DaemonActivity
    (
        ActivityId bigint            NOT NULL IDENTITY(1, 1) CONSTRAINT PK_monitoring_DaemonActivity PRIMARY KEY,
        DaemonKey  nvarchar(64)      NOT NULL,
        RunId      uniqueidentifier  NOT NULL,
        [At]       datetimeoffset(7) NOT NULL,
        Level      nvarchar(16)      NOT NULL,
        Kind       nvarchar(32)      NOT NULL,
        Message    nvarchar(1000)    NOT NULL,
        DataJson   nvarchar(max)     NULL,
        CONSTRAINT CK_monitoring_DaemonActivity_Level CHECK (Level IN (N'Info', N'Warning', N'Error')),
        CONSTRAINT CK_monitoring_DaemonActivity_Data CHECK (DataJson IS NULL OR ISJSON(DataJson) = 1)
    );
    CREATE INDEX IX_monitoring_DaemonActivity_Daemon ON monitoring.DaemonActivity (DaemonKey, ActivityId);
    CREATE INDEX IX_monitoring_DaemonActivity_At ON monitoring.DaemonActivity ([At]);
END
GO
```

| Procedimiento | Parámetros | Efecto |
|---|---|---|
| `usp_AddDaemonActivity` | `@EntriesJson nvarchar(max)` — `[{daemonKey, runId, at, level, kind, message, dataJson}]` | Inserta en orden (`OPENJSON ... ORDER BY [key]`), `Message` truncado a 1000 |
| `usp_GetDaemonActivity` | `@AfterId bigint = 0, @DaemonKey nvarchar(64) = NULL, @MinLevel nvarchar(16) = NULL, @Top int = 200` | `@AfterId > 0`: `ActivityId > @AfterId` ascendente, `TOP (@Top)`. `@AfterId = 0`: los últimos `@Top` (desc) devueltos ascendentes. `@MinLevel`: `Info` ⇒ todos, `Warning` ⇒ Warning+Error, `Error` ⇒ Error. Columnas = nombres de la tabla |
| `usp_PurgeDaemonActivity` | `@Cutoff datetimeoffset(7)` | Borra `[At] < @Cutoff`; `SELECT @@ROWCOUNT AS Deleted` |

## 5. Semántica de los eventos (qué escribe cada daemon)

Sólo se escribe actividad cuando el daemon **está habilitado** (`HealthMonitoring:Enabled` + pilar en `Pillars`; Triage: `Triage:Mode != Off`). Con todo apagado no se escribe nada y la tarjeta dice `disabled`.

| Daemon | Kind | Nivel | Mensaje (formato exacto, cultura invariante, horas UTC `HH:mm`) | Data |
|---|---|---|---|---|
| todos | `run-started` | Info | `Run started` | — |
| todos | `run-skipped` | Info | `Skipped: another run is in progress on this instance` | — |
| KPI Ingestion | `ingestion-kpi` | Info | `{Kpi}/{Domain}: {n} bucket(s) [{from:HH:mm}–{to:HH:mm}), {s} sample(s), watermark → {to:HH:mm}` | `{kpiDefinitionId, from, to, buckets, samples}` |
| KPI Ingestion | `ingestion-kpi` | Info | `{Kpi}/{Domain}: up to date (watermark {wm:HH:mm}, next bucket closes {next:HH:mm})` | `{kpiDefinitionId, watermark}` |
| KPI Ingestion | `ingestion-kpi` | Warning | `{Kpi}/{Domain}: source unavailable — {reason}` / `{Kpi}/{Domain}: no source for '{SourceKey}'` | `{kpiDefinitionId}` |
| KPI Ingestion | `error` | Error | `{Kpi}/{Domain}: fetch failed — {exception message}` | `{kpiDefinitionId}` |
| Monitores | `kpi-evaluated` | Info | `{Kpi}/{Domain}{/Stage} {bucket:HH:mm}: {Explanation}` (el `Explanation` del evaluador) | `{kpiDefinitionId, bucketStart, verdict, outcome, observed, reference, deviation, counter}` |
| Monitores | `kpi-evaluated` | Info | `{Kpi}/{Domain}: not due (next evaluation {HH:mm})` o `{Kpi}/{Domain}: no new closed and ingested bucket` | `{kpiDefinitionId}` |
| Monitores | `anomaly` | Warning | `{Kpi}/{Domain}: {Detection}` | `{kpiDefinitionId, anomalyId, bucketStart}` |
| Monitores | `triage-submitted` | Info | `Investigation {id} queued{ (duplicate)}` | `{anomalyId, investigationId, duplicate}` |
| Monitores | `error` | Error | `{Kpi}/{Domain}: {exception message}` | `{kpiDefinitionId}` |
| Triage Monkey | `investigation-started` | Info | `{Pillar} · {Kpi} · {Domain}: investigating ({source})` | `{investigationId}` |
| Triage Monkey | `investigation-completed` | Info (Warning si `red_alert`) | `{alertLevel} · {summary truncado a 200}{ · notified}` | `{investigationId, alertLevel, notified, unavailableDimensions}` |
| Triage Monkey | `error` | Error | `Investigation {id} failed — {message}` | `{investigationId}` |
| todos | `error` | Error | `Run failed — {message}` (excepción no prevista que aborta el run, escrita antes de relanzarla) | — |
| todos | `run-completed` | Info (Warning si hubo errores) | Ingesta: `{p} KPI(s) processed, {b} bucket(s), {s} sample(s) written, {k} skipped` o `Up to date — nothing to ingest` cuando `p == 0 && k == 0`. Monitores: `{e} evaluated, {t} triggered, {u} suppressed, {k} skipped`. Triage: `Investigation {id} {status}` | el resultado del run |

`run-completed` y `run-skipped` también llaman a `IMonkeyRunTracker.RecordRunAsync(daemonKey, now)`.

### Estado (`IDaemonStatusReader`)

Por daemon, a partir de las últimas entradas de su actividad:

- `Enabled` = reglas de habilitación de arriba; `Mode` = `Triage:Mode` para Triage, `null` para los demás.
- `LastRunAt` / `LastRunSummary` / `LastRunLevel` = último `run-completed` (o `run-skipped`).
- `LastErrorAt` / `LastErrorMessage` = último evento `Error`.
- `Health` (en este orden): `disabled` si no está habilitado → `running` si el último `run-started` no tiene `run-completed` posterior y tiene menos de `RunningWindowMinutes` → `never-run` si no hay `LastRunAt` → `stale` si `now - LastRunAt > StaleAfterMinutes` → `error` si `LastRunLevel == Error` o el último `Error` es posterior al último `run-started` → `ok`.
- **Excepción Triage Monkey (idle):** Triage solo corre cuando hay señales. Si su `LastRunAt` es viejo o no existe pero `IMonkeyRunTracker.GetLastRunAsync("inspector-monkey")` es más reciente que `StaleAfterMinutes`, el estado es `ok` (o `error` por la regla de errores) con `LastRunSummary = "Idle — no queued investigations"` (y `LastRunAt` = el de su última investigación, si existe). Solo es `stale` si el Inspector tampoco corrió en ese lapso.
- `Schedule` = `DaemonMonitoringOptions.*Schedule`.

### API (Functions, anónimas: solo lectura)

| Ruta | Respuesta |
|---|---|
| `GET /api/daemons` | 200 `[DaemonStatus]` (`TriageJson.Options`) |
| `GET /api/daemons/activity?afterId=0&daemon=&level=&top=200` | 200 `DaemonActivityPage`; `top` 1..1000, `afterId` ≥ 0, `daemon` ∈ `DaemonKeys.All`, `level` ∈ Info/Warning/Error (case-insensitive); inválido → 400 `{errors}` |

"Run now" reutiliza `POST /api/monkeys/{id}/run` (ya existe en Functions y Web). Triage Monkey se ejecuta con el id `inspector-monkey`.

Retención: `KpiIngestionService` purga actividad con `[At] < now - RetentionDays` al final de cada corrida (best-effort).
