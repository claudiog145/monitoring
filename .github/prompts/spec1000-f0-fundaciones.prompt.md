---
name: spec1000-f0-fundaciones
description: "Spec 1000 tarea F0: crear los contratos (modelos, interfaces, options), el schema SQL KPI/Triage, el seed de 24 KPIs y los tests de contrato."
agent: spec1000-implementer
argument-hint: "TASK=F0 WORKTREE=<ruta> ITERATION=<n>"
---
# F0 · Fundaciones: contratos, SQL y seed

**Objetivo:** dejar en el código, literalmente, todo lo definido en [contracts.md](../spec1000/contracts.md) §1–§8, compilando, con tests de contrato y con el schema creado por `docker compose`. Nada de lógica de negocio.

## Archivos que te pertenecen

Los de `tasks.json` → `F0.allowedPaths`. Ningún otro.

## Pasos

1. **`Portal/src/JourneyMonitor.Core/Domain/Kpi.cs`**: copiá el bloque C# de §1 **sin cambiar nombres, orden de parámetros ni tipos**.
2. **`Portal/src/JourneyMonitor.Core/Domain/Triage.cs`**: copiá §2.
3. **`Portal/src/JourneyMonitor.Core/Abstractions/IKpi.cs`** y **`ITriage.cs`**: copiá §3 y §4.
4. **Options**: creá `Portal/src/JourneyMonitor.Core/Options/HealthMonitoringOptions.cs` y `TriageOptions.cs` con §5.
5. **Constantes existentes** (solo agregar, no modificar lo que hay):
   - `Core/Domain/Inspector.cs` → en `AgentWorkPayloadType`: `public const string TriageSignal = "triage-signal";` con un `<summary>` de una línea ("PayloadId is a TriageInvestigation id").
   - `Core/Domain/Monitoring.cs` → en `MonkeyKey`: `KpiIngestion = "kpi-ingestion"`, `ApplicationHealthMonitor = "application-health-monitor"`, `BusinessHealthMonitor = "business-health-monitor"`, `InfrastructureHealthMonitor = "infrastructure-health-monitor"`, `TriageMonkey = "triage-monkey"`.
   - Antes de agregar, buscá en el repo (`search`) que esas cadenas no existan ya; si existen con otro significado → `SPEC1000-BLOCKED.md`.
6. **`Portal/src/JourneyMonitor.Infrastructure/Sql/monitoring/CreateMonitoringKpi.sql`**:
   - Copiá literalmente el DDL de §6.
   - Implementá **cada** procedimiento de la tabla de §6 con `CREATE OR ALTER PROCEDURE monitoring.<nombre>`, `SET NOCOUNT ON`, parámetros y columnas exactamente como en la tabla. Seguí el estilo de `CreateMonitoringSettings.sql` e `CreateMonitoringInspector.sql`.
   - `usp_UpsertKpiDefinition`: `BEGIN TRAN`; si `@KpiDefinitionId = 0` → `INSERT` y tomar `SCOPE_IDENTITY()`; si no → `UPDATE` (si no existe la fila → `THROW 50001, 'KpiDefinition not found', 1`); insertar en `KpiDefinitionAudit` el snapshot `(SELECT * FROM monitoring.KpiDefinition WHERE KpiDefinitionId = @id FOR JSON PATH, WITHOUT_ARRAY_WRAPPER)`; `COMMIT`; devolver la fila.
   - `usp_UpsertKpiBuckets` y `usp_AddKpiSamples`: `OPENJSON(@json) WITH (...)` con nombres camelCase como en la tabla; `MERGE` por PK para buckets.
7. **`CreateMonitoringTriage.sql`**: DDL de §7 + procedimientos de su tabla.
8. **`SeedKpiDefinitions.sql`**: `MERGE monitoring.KpiDefinition AS t USING (VALUES ...) AS s (...) ON t.Pillar = s.Pillar AND t.Kpi = s.Kpi AND t.Domain = s.Domain AND t.Stage = s.Stage WHEN NOT MATCHED THEN INSERT ...;` con las 24 filas de §8 (`Enabled = 1` solo para Application/Latency y Application/Exceptions de `hotels`). **Sin** `WHEN MATCHED` (no pisar cambios hechos en Settings).
9. **`Portal/docker/init-sql.sh`**: agregá al final de la lista `for script in`, en este orden: `monitoring/CreateMonitoringKpi.sql`, `monitoring/CreateMonitoringTriage.sql`, `monitoring/SeedKpiDefinitions.sql`. No cambies nada más del script.
10. **Tests de contrato** (xUnit, mismo estilo que los tests existentes):
    - `Portal/tests/JourneyMonitor.Tests/Monitoring/Kpi/KpiContractTests.cs`:
      - `FloorToBucket_AlignsToUtcEpoch`: `2026-11-04T14:37:12+02:00` con 1 h → `2026-11-04T12:00:00+00:00`; con 5 min → `2026-11-04T12:35:00+00:00`; offset del resultado = 0.
      - `KpiPillarNames_MapToSpecDisplayNames`: los 3 display names exactos; `TryParseDisplay("application health")` → Application; `TryParseDisplay("Business")` → Business; `TryParseDisplay("x")` → false.
      - Agregá además `KpiDefinition_Validate_*` para: rangos válidos sin errores, `StaticThreshold` + `Either` rechazado, JSON inválido rechazado, `BaselinePercent` sin `lookbackDays` rechazado.
    - `Portal/tests/JourneyMonitor.Tests/Monitoring/Triage/TriageContractTests.cs`:
      - `TriageResult_SerializesToSpecJsonShape`: serializá con `TriageJson.Options` y verificá con `JsonDocument` que existan exactamente `investigationId`, `trigger.{detectedAt,pillar,kpi,domain,stage,condition}`, `correlation.{businessHealth,applicationHealth,infrastructureHealth,codeReliability,recentChanges,knowledgeBase}`, `assessment.{alertLevel,possibleRedAlert,confidence,summary,recommendedNextActions}`.
      - `TriageSignal_RoundTripsThroughJson`: serializar/deserializar produce un record igual.
      - `TriageDedupKey_UsesAnomalyIdWhenPresent`: `AnomalyId = 42` → `"anomaly:42"`.
      - `TriageDedupKey_FallsBackToHourBucket`: `DetectedAt = 2026-11-04T14:05:00Z`, `Pillar = "Application Health"`, `Kpi = "Latency"`, `Domain = "hotels"`, `Stage = null` → `"application health|latency|hotels||2026110414"`.
11. **Verificación local del SQL** (recomendada antes del self-check): `powershell -NoProfile -ExecutionPolicy Bypass -File .github/spec1000/run-gate.ps1 -Task F0 -Mode Check -IncludeE2E` (nivel `schema`: crea tablas en `platformDB` y `platformDBQA`, 24 filas de seed, re-ejecución idempotente).

## Criterios de aceptación (el tester los verifica uno por uno)

1. Los 6 archivos de contrato C# son **idénticos en API pública** a `contracts.md` §1–§5 (mismos tipos, miembros, parámetros, orden, valores por defecto).
2. Las 6 constantes nuevas existen con los valores exactos y no se modificó ninguna constante existente.
3. `CreateMonitoringKpi.sql` y `CreateMonitoringTriage.sql` contienen todas las tablas, constraints, índices y **todos** los procedimientos listados, idempotentes.
4. El seed crea exactamente 24 filas en una base vacía y 0 filas nuevas al re-ejecutarse; solo 2 filas con `Enabled = 1`.
5. `init-sql.sh` registra los 3 scripts al final y en ese orden.
6. Los 6 tests requeridos existen, prueban lo descrito y pasan; ningún test existente se rompió.
7. E2E `schema` PASS.
8. Sin lógica de negocio, sin DI, sin cambios fuera de `allowedPaths`.
