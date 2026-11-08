---
name: spec1000-a4-functions-api
description: "Spec 1000 tarea A4: timers de ingesta y daemons por pilar, API de Triage (POST/GET), lecturas de salud, CRUD de KpiDefinition, RunMonkeyNow, TriageSignalSubmitter y GetPillarHealthService, con tests."
agent: spec1000-implementer
argument-hint: "TASK=A4 WORKTREE=<ruta> ITERATION=<n>"
---
# A4 · Functions, API de Triage y lecturas de salud

**Objetivo:** exponer el motor como timers y HTTP según `contracts.md` §10, usando **solo interfaces** de contrato (los servicios concretos de A1/A2/A3/A5 no existen en tu rama).

## Archivos que te pertenecen

Ver `tasks.json` → `A4`. **No** edites `Program.cs` (el registro DI lo hace I1).

## Antes de empezar

Leé `RunMonkeyNowFunction.cs`, `SettingsWriteSecretGuard.cs`, `UpsertLatencySpikeSettingFunction.cs`, `GetFindingsFunction.cs`, `SyncJourneysFunction.cs` y los tests en `tests/Functions/` (incluido `FakeHttpRequestData`). Copiá esos patrones (respuestas JSON, guardas de secreto fail-closed, logging).

## Core (lógica)

1. **`Core/UseCases/TriageSignalSubmitter.cs`** — `public sealed class TriageSignalSubmitter : ITriageSignalSubmitter`, constructor `(ITriageInvestigationStore store, IAgentWorkQueue queue, TimeProvider time, ILogger<TriageSignalSubmitter> logger)`:
   - `key = TriageDedupKey.For(signal)`; si `FindOpenByDedupKeyAsync(key)` devuelve algo → `(id, Duplicate: true)` sin encolar.
   - Si no: `id = Guid.NewGuid()`, `CreateAsync(new TriageInvestigation(id, key, signal.AnomalyId, signal, Queued, null, null, false, now, null, null))`, luego `EnqueueAsync(new AgentWorkItem { SourceMonkey = <ver tabla>, PayloadType = AgentWorkPayloadType.TriageSignal, PayloadId = id.ToString(), DedupKey = $"triage:{id}", Priority = 100 })`.
     `SourceMonkey`: `Source == "manual"` → `"portal"`; si no, según el pilar de la señal: Application Health → `MonkeyKey.ApplicationHealthMonitor`, Business Health → `MonkeyKey.BusinessHealthMonitor`, Infrastructure Health → `MonkeyKey.InfrastructureHealthMonitor`.
   - Si `EnqueueAsync` falla → `UpdateStatusAsync(id, Failed, "enqueue failed: …")` y relanzar.
2. **`Core/UseCases/GetPillarHealthService.cs`** — `public sealed class GetPillarHealthService : IPillarHealthReader`, constructor `(IKpiDefinitionStore, IKpiBucketStore, IKpiStateStore, IKpiAnomalyStore, TimeProvider)`. Devuelve **siempre 3** `PillarHealth` (Business, Application, Infrastructure, en ese orden) con `DisplayName` de `KpiPillarNames`. Por KPI (filtrado por `domain` si viene): último bucket de las últimas 48 h, estado, última anomalía. Status: `"offline"` sin KPIs habilitados; `"anomalous"` si algún KPI tiene contador > 0 o `CooldownUntil > now`; si no `"ok"`.

## Functions (solo validar → despachar → mapear)

| Archivo | Function | Trigger | Comportamiento |
|---|---|---|---|
| `KpiIngestionFunction.cs` | `KpiIngestion` | `TimerTrigger("%spec1000_ingestion_cron%")` | si `HealthMonitoring:Enabled` → `IKpiIngestionRunner.RunAsync`; log del resultado |
| `ApplicationHealthMonitorFunction.cs` | `ApplicationHealthMonitor` | `%spec1000_monitor_cron%` | `IPillarHealthMonitor.RunAsync(KpiPillar.Application)` |
| `BusinessHealthMonitorFunction.cs` | `BusinessHealthMonitor` | ídem | `KpiPillar.Business` |
| `InfrastructureHealthMonitorFunction.cs` | `InfrastructureHealthMonitor` | ídem | `KpiPillar.Infrastructure` |
| `TriageInvestigationsFunction.cs` | `SubmitTriageInvestigation`, `GetTriageInvestigations`, `GetTriageInvestigation` | HTTP | §10 |
| `HealthReadFunctions.cs` | `GetPillarHealth`, `GetKpiAnomalies`, `GetKpiBuckets` | HTTP | §10 |
| `KpiDefinitionAdminFunctions.cs` | `GetKpiDefinitions`, `GetKpiDefinitionAudit`, `UpsertKpiDefinition` | HTTP | §10 |
| `RunMonkeyNowFunction.cs` | (existente) | — | agregar casos para los 4 ids nuevos (`MonkeyKey.*`); no cambiar los existentes |

Reglas HTTP:
- Serialización con `TriageJson.Options` en todas las respuestas nuevas.
- `POST /api/triage/investigations`: secreto `X-Internal-Api-Secret` con `SettingsWriteSecretGuard.HasMatchingSecret(..., InternalApiSecretHeaderName)` contra `MonitoringOptions.InternalApiSecret` → 401 si falta o no coincide. Body: `detectedAt` (requerido, ISO), `pillar` (requerido, `KpiPillarNames.TryParseDisplay`), `kpi` (1..64), `detection` (1..400), `domain` opcional (debe estar en `KpiDomains.All`), `stage` opcional (≤ 64). Errores → 400 `{ "errors": [..] }`. OK → 202 `{ investigationId, duplicate }`. Pillar se normaliza al display name; `Source = "manual"`, `AnomalyId = null`.
- `POST /api/settings/kpis`: secreto `X-Settings-Write-Secret` (patrón de `UpsertLatencySpikeSettingFunction`). Body = campos de `KpiDefinition` + `changedBy` (se ignoran `updatedAt`/`updatedBy` del body). `Validate()` con errores → 400 `{errors}`. OK → 200 con la fila guardada.
- Query params: `top` 1..500 (default 50/100/200 según §10), `hours` 1..720 (default 48). Fuera de rango → 400.
- `local.settings.json`: agregá `"spec1000_ingestion_cron": "0 */1 * * * *"`, `"spec1000_monitor_cron": "30 */1 * * * *"`, `"HealthMonitoring__Enabled": "false"`, `"Triage__Mode": "Shadow"`.

## Tests requeridos

Fakes propios en `tests/Functions/Spec1000Fakes/`. Usá `FakeHttpRequestData` existente.

`PostInvestigation_WithoutSecret_Returns401`, `PostInvestigation_InvalidBody_Returns400`, `PostInvestigation_Valid_Returns202WithInvestigationId`, `PostInvestigation_Duplicate_ReturnsSameIdAndDuplicateTrue`, `GetInvestigation_Unknown_Returns404`, `UpsertKpiDefinition_WithoutSettingsSecret_Returns401`, `UpsertKpiDefinition_InvalidRanges_Returns400`, `TimerFunctions_DoNothingWhenHealthMonitoringDisabled` (los 4 timers), `RunMonkeyNow_SupportsSpec1000Ids` (los 4 ids), `Submitter_CreatesInvestigationAndEnqueuesWork` (PayloadType `triage-signal`, PayloadId = id, DedupKey `triage:{id}`), `Submitter_OpenDuplicate_DoesNotEnqueueAgain`, `PillarHealth_OfflineWhenNoEnabledKpis`, `PillarHealth_AnomalousWhenCounterOrCooldownActive`.

## Criterios de aceptación

1. Rutas, métodos, seguridad y códigos de estado exactamente como §10.
2. Functions sin lógica de negocio ni SQL/KQL (el gate lo verifica); lógica en los 2 servicios de Core.
3. Timers inertes con `HealthMonitoring:Enabled=false`; nombres de settings de cron exactos.
4. `RunMonkeyNow` mantiene los ids existentes y agrega los 4 nuevos.
5. 13 tests requeridos presentes y verdes.
