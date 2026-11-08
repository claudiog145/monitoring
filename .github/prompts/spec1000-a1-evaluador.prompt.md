---
name: spec1000-a1-evaluador
description: "Spec 1000 tarea A1: implementar KpiEvaluator (3 métodos, dirección, consecutivos, cooldown, mínimo de datos) y PillarHealthMonitorService con sus tests."
agent: spec1000-implementer
argument-hint: "TASK=A1 WORKTREE=<ruta> ITERATION=<n>"
---
# A1 · Evaluador de reglas y monitor por pilar

**Objetivo:** implementar la semántica **exacta** de [contracts.md](../spec1000/contracts.md) §9: un evaluador puro y determinístico, y el servicio que lo ejecuta por pilar.

## Archivos que te pertenecen

`Portal/src/JourneyMonitor.Core/Monitoring/Kpi/**` y los tests `Monitoring/Kpi/KpiEvaluator*Tests.cs`, `PillarHealthMonitor*Tests.cs`, `Monitoring/Kpi/Fakes/**`. No toques `KpiContractTests.cs` (es de F0).

## Diseño obligatorio

- `public sealed class KpiEvaluator : IKpiEvaluator` en `Core/Monitoring/Kpi/KpiEvaluator.cs`. Sin dependencias, sin I/O, sin `DateTimeOffset.UtcNow`.
- Métodos en `Core/Monitoring/Kpi/Methods/`: `StaticThresholdMethod`, `BaselinePercentMethod`, `RobustZScoreMethod` (clases `internal static` o `internal sealed`), cada uno devuelve `(double? reference, double? deviation, string? notEvaluatedReason)`.
- Mediana: para cantidad par, promedio de los dos centrales. MAD con la misma función de mediana. Constante `1.4826`.
- `KpiDetectionText.Format(...)` en `Core/Monitoring/Kpi/KpiDetectionText.cs` produce el texto `Detection` de §9 con `CultureInfo.InvariantCulture`.
- `public sealed class PillarHealthMonitorService : IPillarHealthMonitor` en `Core/Monitoring/Kpi/PillarHealthMonitorService.cs`. Constructor exacto:
  ```csharp
  public PillarHealthMonitorService(
      IKpiDefinitionStore definitions, IKpiBucketStore buckets, IKpiStateStore states, IKpiAnomalyStore anomalies,
      IIngestionWatermarkStore watermarks, IKpiEvaluator evaluator, ITriageSignalSubmitter triage,
      IOptions<HealthMonitoringOptions> options, TimeProvider time, ILogger<PillarHealthMonitorService> logger)
  ```
  Algoritmo: §9 "Monitor por pilar", pasos 1–7, sin desvíos. `Pillars` se compara con `KpiPillar.ToString()` ignorando mayúsculas. Si `SubmitAsync` lanza excepción: loguear error, **conservar** la anomalía y el estado (no reintentar en esta corrida) y seguir con el próximo KPI.
- Un KPI que falla (excepción de store) no detiene a los demás: log + cuenta como `Skipped`.

## Tests requeridos (nombres exactos, en `KpiEvaluatorTests.cs` y `PillarHealthMonitorServiceTests.cs`)

Usá fakes en memoria propios en `Monitoring/Kpi/Fakes/` (no dependas de A3) y `FakeTimeProvider` propio (clase simple derivada de `TimeProvider` con `GetUtcNow()` fijo) en la misma carpeta.

| Test | Escenario y aserción |
|---|---|
| `IsolatedSpike_DoesNotTrigger` | Static 3000, Increase, ConsecutiveCount 2: valores 1000, 3500, 1000 → outcomes NoChange, CounterIncremented, CounterReset; ninguna Triggered |
| `TwoConsecutiveAnomalies_Trigger` | 3500, 3600 → CounterIncremented, Triggered; `CooldownUntil = fin del 2º bucket + Cooldown` |
| `Cooldown_SuppressesDuplicateTrigger` | 3500, 3600, 3700 con Cooldown 120 y buckets de 1 h → tercer outcome SuppressedByCooldown, contador 3 |
| `OppositeDirection_IsIgnored` | BaselinePercent Increase 50%: valor −60% → Normal/NoChange |
| `InsufficientObservations_SkipsBucketWithoutChangingCounter` | estado con contador 1; bucket con TotalCount < MinObservations → Skipped, contador sigue 1, `LastBucketEvaluated` avanza |
| `AfternoonSimulation_MatchesPresentationTable` | Ver tabla de abajo |
| `BaselinePercent_UsesMedianOfSameSlotPreviousDays` | historia con 5 días a la misma hora (valores 100, 200, 300, 400, 10000) → referencia 300 |
| `BaselinePercent_InsufficientHistory_NotEvaluated` | lookbackDays 7 con solo 2 días de historia → NotEvaluated/Skipped |
| `RobustZScore_FlagsOutlierBeyondThreshold` | ventana 8 con valores 10, 11, 9, 10, 12, 10, 11, 9 y observado 30, zThreshold 3 → Anomalous |
| `RobustZScore_ZeroMad_NotEvaluated` | ventana con todos los valores iguales → NotEvaluated |
| `StaticThreshold_Decrease_FlagsBelowThreshold` | Decrease, threshold 100, valor 80 → Anomalous; valor 100 → Normal (igual no es anómalo) |
| `StaticThreshold_Either_IsRejected` | definición Static + Either → todos los buckets NotEvaluated con explicación que menciona "Either" |
| `Monitor_DoesNothingWhenDisabled` | `Enabled=false` → `(0,0,0,0)` y ningún store llamado |
| `Monitor_SkipsKpisNotDue` | `LastEvaluatedAt = now - 2 min`, frecuencia 5 min → Skipped, sin evaluación |
| `Monitor_EvaluatesOnlyClosedAndIngestedBuckets` | buckets abiertos o posteriores al watermark no se evalúan |
| `Monitor_FirstRunEvaluatesOnlyLatestClosedBucket` | `LastBucketEvaluated = null` con 5 buckets cerrados → solo se evalúa el último |
| `Monitor_SubmitsTriageSignalOnTrigger` | trigger → `SubmitAsync` con Pillar `"Application Health"`, Kpi, Detection, Domain, Stage null si `""`, AnomalyId, Source `"daemon"`; luego `AttachInvestigationAsync` con el id devuelto |
| `Monitor_PersistsStateAndAnomaly` | estado final guardado con `LastEvaluatedAt = now`; anomalía con `DetectedAt = now`, valores observados/referencia/desvío |

### Datos exactos de `AfternoonSimulation_MatchesPresentationTable`

Definición: Application/Latency/hotels, P95, Increase, BaselinePercent `{"lookbackDays":7,"thresholdPercent":50}`, ConsecutiveCount 2, Cooldown 120, MinObservations 30, BucketSize 1 h. Historia: para cada hora, los 7 días previos tienen P95 = "normal". Buckets del día evaluado (UTC), todos con TotalCount 5000:

| Hora | Normal | P95 observado | Desvío | Outcome esperado | Contador |
|---|---|---|---|---|---|
| 12:00 | 1800 | 1854 | +3.0% | NoChange | 0 |
| 13:00 | 1820 | 2950 | +62.1% | CounterIncremented | 1 |
| 14:00 | 1880 | 1974 | +5.0% | CounterReset | 0 |
| 15:00 | 1900 | 3230 | +70.0% | CounterIncremented | 1 |
| 16:00 | 1910 | 3534 | +85.0% | Triggered | 2 |
| 17:00 | 1930 | 3667 | +90.0% | SuppressedByCooldown | 3 |
| 18:00 | 1900 | 2660 | +40.0% | CounterReset | 0 |

Además: `CooldownUntil` tras 16:00 = 19:00 del mismo día; el texto `Detection` del bucket 16:00 es exactamente `"2 consecutive increases: Latency 3534 vs reference 1910 (+85.0%)"`.

## Criterios de aceptación

1. `KpiEvaluator` implementa §9 pasos 1–7 sin desvíos (verificable con los tests de la tabla).
2. El evaluador es puro (sin I/O ni reloj); el monitor usa solo `TimeProvider` para "ahora".
3. Los 18 tests requeridos existen con esos nombres, prueban lo descrito y pasan.
4. Un fallo en un KPI o en `SubmitAsync` no detiene al resto ni pierde la anomalía.
5. Sin cambios fuera de `allowedPaths`; contratos intactos.
