---
name: spec1000-a3-persistencia
description: "Spec 1000 tarea A3: repositorios SQL (Dapper sobre los usp_* de F0) y en memoria para KPI y Triage, gate de schema y AddKpiPersistence, con tests."
agent: spec1000-implementer
argument-hint: "TASK=A3 WORKTREE=<ruta> ITERATION=<n>"
---
# A3 · Persistencia SQL y en memoria

**Objetivo:** implementar todas las interfaces de almacenamiento de `contracts.md` §3–§4 en dos variantes (SQL y memoria) y una extensión de DI que elija la correcta.

## Archivos que te pertenecen

Ver `tasks.json` → `A3`. Los procedimientos SQL ya existen (F0) y **no** podés cambiarlos: si un procedimiento no sirve → `SPEC1000-BLOCKED.md`.

## Antes de empezar

Leé `SqlLatencySpikeSettingsStore.cs`, `SqlAgentWorkQueue.cs`, `MonitoringSchemaGate.cs` y cómo `AddJourneyMonitorMonitoring` elige SQL vs memoria. Copiá ese patrón (conexión, Dapper, `CommandType.StoredProcedure`, manejo de `datetimeoffset`).

## Implementaciones

| Interfaz | SQL (`Infrastructure/Sql/`) | Memoria (`Infrastructure/Monitoring/`) |
|---|---|---|
| `IKpiDefinitionStore` | `SqlKpiDefinitionStore` | `InMemoryKpiDefinitionStore` |
| `IKpiBucketStore` | `SqlKpiBucketStore` | `InMemoryKpiBucketStore` |
| `IKpiSampleStore` | `SqlKpiSampleStore` | `InMemoryKpiSampleStore` |
| `IKpiStateStore` | `SqlKpiStateStore` | `InMemoryKpiStateStore` |
| `IKpiAnomalyStore` | `SqlKpiAnomalyStore` | `InMemoryKpiAnomalyStore` |
| `IIngestionWatermarkStore` | `SqlIngestionWatermarkStore` | `InMemoryIngestionWatermarkStore` |
| `ITriageInvestigationStore` | `SqlTriageInvestigationStore` | `InMemoryTriageInvestigationStore` |

Reglas:
- Mapeo `KpiDefinition` ↔ fila: `TimeSpan` ↔ segundos (`*Sec`), enums ↔ `nvarchar` con el nombre exacto del enum, `Stage` nunca null (`""`).
- `IKpiStateStore.GetAsync` sin fila → `KpiDetectionState.Initial(id)`.
- Lotes (`UpsertAsync` de buckets, `AddAsync` de muestras) serializados a JSON camelCase para `@BucketsJson`/`@SamplesJson`; en lotes de 1000 elementos como máximo.
- `TriageInvestigation`: `SignalJson`/`ResultJson` con `TriageJson.Options`; `Status` ↔ nombre del enum.
- Memoria: thread-safe (`lock` o colecciones concurrentes), mismas semánticas que SQL (rango semiabierto y orden, dedup "abierto" = Queued/Running, auditoría en cada upsert, ids incrementales).
- `MonitoringSchemaGate`: agregá `IsKpiSchemaPresent(...)` que verifica `OBJECT_ID` de `monitoring.KpiDefinition` **y** `monitoring.TriageInvestigation`, mismo estilo que los métodos existentes. No cambies los métodos existentes.
- `Infrastructure/DependencyInjection/KpiPersistenceServiceCollectionExtensions.cs`: `public static IServiceCollection AddKpiPersistence(this IServiceCollection services, IConfiguration configuration)`. Con `JourneyStore:ConnectionString` y schema presente → SQL; si no → memoria **singleton** + log warning (igual que monitoring). Registrá las 7 interfaces.

## Tests requeridos

| Test | Aserción |
|---|---|
| `InMemoryKpiBucketStore_UpsertIsIdempotent` | dos upserts del mismo (id, BucketStart) dejan 1 fila con los últimos valores |
| `InMemoryKpiBucketStore_GetRangeIsHalfOpenAndOrdered` | incluye `from`, excluye `to`, ascendente |
| `InMemoryKpiStateStore_ReturnsDefaultStateWhenMissing` | `Initial(id)` |
| `InMemoryKpiDefinitionStore_UpsertWritesAudit` | insert (id 0 → id nuevo) + update → 2 entradas de auditoría, la más nueva primero |
| `InMemoryKpiAnomalyStore_AttachInvestigation` | `InvestigationId` queda asignado |
| `InMemoryTriageInvestigationStore_FindOpenByDedupKeyIgnoresCompleted` | Completed no se devuelve; Queued sí |
| `SqlKpiStores_RoundTrip` | contra SQL real: definición, buckets, muestras, estado, anomalía, watermark ida y vuelta |
| `SqlTriageInvestigationStore_RoundTrip` | create → find open → status Running → complete → get |
| `AddKpiPersistence_UsesInMemoryWithoutConnectionString` | sin connection string resuelve las implementaciones en memoria |

Los tests SQL leen la cadena de `SPEC1000_SQL_CONNECTION` (variable de entorno). Si no está definida, el test **retorna sin asserts** y escribe una línea en `ITestOutputHelper` explicando que se omitió (xUnit 2 no tiene skip dinámico). El tester los corre con `SPEC1000_SQL_CONNECTION="Server=localhost,1433;Database=platformDB;User Id=sa;Password=Jm_LocalDev_1!;TrustServerCertificate=True;Encrypt=True"` y el stack Docker arriba; cada test usa datos con prefijo `A3TEST` y los borra al final.

## Criterios de aceptación

1. Las 14 clases existen e implementan exactamente las interfaces de contrato.
2. SQL usa solo los `usp_*` de F0 (nada de SQL inline salvo en tests de limpieza).
3. Semántica idéntica entre SQL y memoria para los casos de los tests.
4. `AddKpiPersistence` elige SQL/memoria como el patrón existente.
5. 9 tests requeridos presentes y verdes, incluidos los SQL con la variable definida.
6. E2E `schema` PASS.
