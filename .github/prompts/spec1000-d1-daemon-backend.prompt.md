---
name: spec1000-d1-daemon-backend
description: "Spec 1000 tarea D1: registro de actividad de los daemons (tabla, store SQL/memoria, recorder), estado por daemon, API /api/daemons, last-run en el tracker, tarjetas en Simian Army y log 'al día' de la ingesta."
agent: spec1000-implementer
argument-hint: "TASK=D1 WORKTREE=<ruta> ITERATION=<n>"
---
# D1 · Backend de monitoreo de daemons

**Objetivo:** que cada daemon de Spec 1000 deje un rastro legible de lo que hace (para la consola del portal y la CLI) y que su estado se pueda consultar por API.

**Contrato:** [contracts-daemons.md](../spec1000/contracts-daemons.md) §1–§5, literal. Los contratos v1 ([contracts.md](../spec1000/contracts.md)) siguen congelados.

## Archivos que te pertenecen

Ver `tasks.json` → `D1`. Los servicios existentes (`KpiIngestionService`, `PillarHealthMonitorService`, `TriageMonkeyService`) se modifican **solo** para emitir actividad: no cambies su comportamiento ni sus resultados.

## Pasos

1. **Contrato:** creá `Core/Domain/DaemonActivity.cs`, `Core/Abstractions/IDaemonActivity.cs`, `Core/Options/DaemonMonitoringOptions.cs` copiando §1–§3 literalmente, y `Infrastructure/Sql/monitoring/CreateMonitoringDaemonActivity.sql` con el DDL de §4 + los 3 procedimientos (`CREATE OR ALTER`, `SET NOCOUNT ON`). Registralo **al final** de la lista de `Portal/docker/init-sql.sh`.
2. **Stores:** `Infrastructure/Sql/SqlDaemonActivityLog.cs` (Dapper + procedimientos, lotes JSON camelCase, nivel como nombre del enum) e `Infrastructure/Monitoring/InMemoryDaemonActivityLog.cs` (thread-safe, ids incrementales, misma semántica de paginación y filtros, tope en memoria de 10.000 entradas). En `MonitoringSchemaGate.cs` agregá `IsDaemonActivitySchemaPresent` (patrón existente).
3. **Recorder** `Core/Monitoring/Daemons/DaemonActivityRecorder.cs : IDaemonActivityRecorder` (singleton): `BeginRun` devuelve un `IDaemonRun` que acumula entradas en memoria con `TimeProvider` y las escribe en `FlushAsync` (y cada 50 entradas). **Nunca** lanza: cualquier error del store se loguea con `ILogger` y se descarta. `data` se serializa con `TriageJson.Options`. Mensaje truncado a 1000.
4. **Status** `Core/Monitoring/Daemons/DaemonStatusReader.cs : IDaemonStatusReader`: reglas exactas de §5 "Estado". Lee la actividad con `GetAsync(0, daemonKey, null, 200)` por daemon. Habilitación desde `HealthMonitoringOptions` y `TriageOptions`.
5. **Instrumentar** (dependencia nueva **opcional** al final de cada constructor: `IDaemonActivityRecorder? activity = null`, para no romper construcciones existentes):
   - `KpiIngestionService`: run-started, una `ingestion-kpi` por KPI (escritos / al día / source unavailable / sin source), `error` por fallo, run-completed con el texto exacto (incluido `Up to date — nothing to ingest`), run-skipped si el lock está tomado; purga de actividad (`RetentionDays`) al final. No escribir nada si `Enabled = false`.
   - `PillarHealthMonitorService`: run-started, `kpi-evaluated` por bucket evaluado (con `Explanation`) y por KPI no vencido / sin bucket, `anomaly`, `triage-submitted`, `error`, run-completed, run-skipped. Clave = `DaemonKeys.ForPillar(pillar)`. Nada si el pilar está deshabilitado.
   - `TriageMonkeyService`: run por investigación con key `triage-monkey`: run-started, investigation-started, investigation-completed (Warning si `red_alert`), `error`, run-completed. Nada si `Mode = Off`.
   - Después de `run-completed` / `run-skipped`, `IMonkeyRunTracker.RecordRunAsync(daemonKey, now)` si el tracker está disponible (dependencia opcional también).
6. **Mensajes de log** de los timers (`KpiIngestionFunction`, `ApplicationHealthMonitorFunction` y el helper compartido de los monitores): cuando no hay nada que hacer, `[KpiIngestion] Up to date — nothing to ingest.` en lugar de "0 KPI(s) processed".
7. **API** `Functions/Functions/DaemonFunctions.cs`: `GetDaemons` y `GetDaemonActivity` según §5 "API" (validación → 400 `{errors}`; respuestas con `TriageJson.Options`). Solo validar, despachar y mapear.
8. **Simian Army:** en `GetMonkeysFunction.cs`, agregá una tarjeta por daemon (`IDaemonStatusReader`) con el mismo formato que las tarjetas existentes (id = daemon key, nombre, estado, último run). No cambies las tarjetas existentes.
9. **DI** en `Spec1000ServiceCollectionExtensions.cs`: `Configure<DaemonMonitoringOptions>` (con `IngestionSchedule`/`MonitorSchedule` leídos de `spec1000_ingestion_cron` / `spec1000_monitor_cron` si existen), store SQL si hay connection string y `IsDaemonActivitySchemaPresent`, si no memoria; recorder y status reader singleton; pasar el recorder y el tracker a los 3 servicios.

## Tests requeridos

| Test | Aserción |
|---|---|
| `Recorder_NeverThrowsWhenStoreFails` | store que lanza → `FlushAsync` no lanza y se loguea |
| `Recorder_EntriesShareRunIdAndKeepOrder` | 3 entradas → mismo RunId, orden preservado |
| `Recorder_TruncatesMessageTo1000` | mensaje de 1500 → 1000 |
| `StatusReader_ReturnsOneStatusPerDaemonInOrder` | 5 estados en el orden de `DaemonKeys.All` |
| `StatusReader_DisabledWhenHealthMonitoringOff` | `Enabled=false` → 4 daemons `disabled`; triage según `Mode` |
| `StatusReader_RunningWhenStartedWithoutCompletion` | run-started reciente sin completed → `running` |
| `StatusReader_StaleWhenLastRunTooOld` | último run hace 10 min → `stale` |
| `StatusReader_ErrorWhenLastRunHadErrors` | error posterior al último run-started → `error` |
| `StatusReader_NeverRunWithoutActivity` | sin actividad y habilitado → `never-run` |
| `InMemoryLog_AfterIdZeroReturnsLatestAscending` | 300 entradas, top 200 → ids 101..300 ascendentes |
| `InMemoryLog_FiltersByDaemonAndMinLevel` | filtros combinados |
| `SqlLog_RoundTripAndPaging` | contra SQL real con `SPEC1000_SQL_CONNECTION` (usá `127.0.0.1`), datos con daemonKey `D1TEST` borrados al final; sin variable, retorna con mensaje |
| `Ingestion_WritesActivityForEachKpiAndRun` | 2 KPIs → started, 2 ingestion-kpi, completed, mismo RunId |
| `Ingestion_UpToDateRunSaysUpToDate` | nada que ingerir → completed `Up to date — nothing to ingest` |
| `Monitor_WritesKpiEvaluatedAnomalyAndTriageActivity` | trigger → kpi-evaluated, anomaly, triage-submitted, completed |
| `Monitor_DisabledWritesNoActivity` | deshabilitado → 0 entradas |
| `Triage_WritesStartedAndCompletedActivity` | started + completed con alertLevel |
| `DaemonsApi_ReturnsFiveStatuses` | 200 con 5 |
| `DaemonsActivityApi_InvalidQuery_Returns400` | top 0, level x, daemon x → 400 |
| `DaemonsActivityApi_PagesWithAfterId` | afterId devuelve solo posteriores y `lastId` correcto |
| `GetMonkeys_IncludesDaemonCards` | las 5 tarjetas aparecen sin perder las existentes |

Si para estos tests necesitás tocar archivos de test existentes de A1/A2/A5 (están en tus `allowedPaths`), solo **agregá** casos; no modifiques ni borres los existentes.

## Criterios de aceptación

1. Los 4 archivos de contrato son literales a `contracts-daemons.md`.
2. Los mensajes y kinds siguen exactamente la tabla de §5; con todo deshabilitado no se escribe actividad.
3. Un fallo del store de actividad nunca afecta a un daemon (test + revisión).
4. Los resultados y el comportamiento de ingesta, monitores y triage no cambian (todos los tests existentes siguen verdes sin modificaciones).
5. `GET /api/daemons` y `/api/daemons/activity` cumplen §5; tarjetas de Simian Army agregadas.
6. E2E nivel `daemons` PASS (tabla, 5 estados, actividad del flujo de detección y del triage, paginación).
7. 21 tests requeridos presentes y verdes.
