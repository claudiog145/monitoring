---
name: spec1000-i1-integracion
description: "Spec 1000 tarea I1: registrar en DI todos los servicios de la ola 1A (AddSpec1000), llamarlo desde Functions/Program.cs y agregar al docker-compose las settings de cron/flags, con E2E de detección."
agent: spec1000-implementer
argument-hint: "TASK=I1 WORKTREE=<ruta> ITERATION=<n>"
---
# I1 · Integración de la ola 1 y registro DI

**Contexto:** el orquestador ya mergeó A1, A3, A2, A4 y A5 (cada uno con PASS) en `feature/spec-1000`; tu worktree parte de ahí. Tu trabajo es que todo funcione **junto** en el host real.

## Archivos que te pertenecen

`Infrastructure/DependencyInjection/Spec1000ServiceCollectionExtensions.cs`, `Functions/Program.cs`, `Portal/docker-compose.yml` (solo el bloque `&functions-env`) y `tests/Functions/Spec1000DependencyInjectionTests.cs`.

## Pasos

1. **Inventario:** listá las clases concretas creadas en la ola 1A (`search` por `: IKpi`, `: IPillar`, `: ITriage`, `: IEvidenceProvider`, `TriageMonkeyService`, `AppInsightsKpiSource`, `FabricBusinessEvidenceProvider`).
2. **`AddSpec1000(this IServiceCollection services, IConfiguration configuration)`** en `Spec1000ServiceCollectionExtensions.cs`:
   - `Configure<HealthMonitoringOptions>`, `Configure<TriageOptions>` y `Configure<KpiIngestionOptions>` (decisión del orquestador en A2, sección `HealthMonitoring:Ingestion`) desde sus `SectionName`.
   - `TryAddSingleton(TimeProvider.System)`.
   - `services.AddKpiPersistence(configuration)` (A3).
   - `IKpiEvaluator → KpiEvaluator` (singleton), `IPillarHealthMonitor → PillarHealthMonitorService`, `IKpiIngestionRunner → KpiIngestionService`, `IPillarHealthReader → GetPillarHealthService`, `ITriageSignalSubmitter → TriageSignalSubmitter`, `TriageMonkeyService`, `IKpiSource → AppInsightsKpiSource` (`AddSingleton<IKpiSource, …>` para permitir varias fuentes), los 6 `IEvidenceProvider` (`TryAddEnumerable`).
   - Lifetimes coherentes con el registro existente de `AddJourneyMonitorMonitoring` (si un servicio depende de algo scoped, que sea scoped).
3. **`Program.cs`**: una sola línea `services.AddSpec1000(context.Configuration);` (o el equivalente según el estilo del archivo), **después** de `AddJourneyMonitorMonitoring`. Nada más.
4. **`docker-compose.yml`**, bloque `&functions-env` (compartido con `functions-qa`), agregar:
   ```yaml
   spec1000_ingestion_cron: "0 */1 * * * *"
   spec1000_monitor_cron: "30 */1 * * * *"
   HealthMonitoring__Enabled: "${HealthMonitoring__Enabled:-false}"
   Triage__Mode: "${Triage__Mode:-Shadow}"
   ```
   Sin otros cambios en el archivo.
5. **Tests** (`Spec1000DependencyInjectionTests.cs`): construí un `ServiceCollection` como lo hace `Program.cs` (configuración en memoria, sin connection string) y verificá:
   - `AddSpec1000_ResolvesEverySpec1000Service`: se resuelven sin excepción `IKpiEvaluator`, `IPillarHealthMonitor`, `IKpiIngestionRunner`, `IPillarHealthReader`, `ITriageSignalSubmitter`, `TriageMonkeyService`, `IEnumerable<IEvidenceProvider>` (6 dimensiones distintas) y `IEnumerable<IKpiSource>` (≥ 1). Usá `ValidateOnBuild = true, ValidateScopes = true`.
   - `AddSpec1000_WithoutSql_UsesInMemoryStores`.
6. **E2E local** antes del self-check: `run-gate.ps1 -Task I1 -Mode Check -IncludeE2E` (nivel `detection`: schema + API + regla de punta a punta + Inspector procesa la señal + sin emails).
   - Si el E2E falla por un bug en código de otra tarea (no en DI/compose), **no lo corrijas**: escribí `SPEC1000-BLOCKED.md` con el fallo exacto, el archivo culpable y la tarea dueña. El orquestador reabre esa tarea.

## Criterios de aceptación

1. Todos los servicios de Spec 1000 se resuelven con validación de scopes.
2. `Program.cs` solo agrega la llamada a `AddSpec1000`.
3. El host de Functions arranca en Docker (los timers encuentran sus settings).
4. E2E `detection` PASS: 2 buckets anómalos → 1 anomalía → 1 investigación encolada → el Inspector la procesa → 0 emails.
5. 2 tests requeridos presentes y verdes; suite completa verde.
