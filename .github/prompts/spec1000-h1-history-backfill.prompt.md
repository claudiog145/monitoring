---
name: spec1000-h1-history-backfill
description: "Spec 1000 tarea H1: la ingesta trae también la historia que necesita el método de detección de cada KPI (p. ej. 7 días para BaselinePercent), en la primera corrida y cuando falta historia, sin cambiar la muestra por bucket."
agent: spec1000-implementer
argument-hint: "TASK=H1 WORKTREE=<ruta> ITERATION=<n>"
---
# H1 · Backfill de la historia requerida por el método

**Problema (visto en el stack real):** `Latency/hotels` usa `BaselinePercent {lookbackDays:7}` y necesita ≥ 3 días de la misma hora. La primera ingesta solo trae `LookbackWindow` (24 h), así que el monitor responde `Not evaluated: Insufficient baseline history: 1 of 3 required same-slot buckets` durante ~3 días. Lo mismo pasa al cambiar el método de un KPI existente a uno que pide más historia.

**Contratos:** v1 y v2 congelados. No se cambia ninguna interfaz; se usa `IKpiEvaluator.GetRequiredHistory(definition)` que ya existe.

## Archivos que te pertenecen

Ver `tasks.json` → `H1`.

## Diseño

1. `KpiIngestionService` recibe `IKpiEvaluator? evaluator = null` como parámetro **opcional al final** del constructor (DI lo resuelve porque ya está registrado; los tests existentes compilan sin cambios).
2. Por KPI: `required = max(LookbackWindow, evaluator?.GetRequiredHistory(def) + BucketSize ?? LookbackWindow)` (redondeado a múltiplos de `BucketSize`). `historyStart = FloorToBucket(to - required)`.
3. **Primera corrida (sin watermark):** `from = historyStart` (en vez de `to - LookbackWindow`).
4. **Hueco de historia (con watermark):** antes de la ingesta normal, si `GetRangeAsync(id, historyStart, historyStart + BucketSize)` no devuelve el bucket más viejo requerido, buscar el bucket más viejo existente en `[historyStart, watermark)` (una consulta `GetRangeAsync(id, historyStart, watermark)`) y traer **solo** `[historyStart, oldestExisting)` (o `[historyStart, watermark)` si no hay ninguno). Esto no mueve el watermark. Hacerlo como mucho **una vez por KPI por corrida**.
5. La muestra por bucket **no cambia**: sigue `KpiSampling.SamplesPerBucket(def)` (calculada con `LookbackWindow`, p. ej. 167/h).
6. Respetar `MaxRowsPerCycle`: si el backfill supera el máximo, cortar la corrida como hoy (los demás KPIs siguen en la próxima). Un backfill de 7 días × 167 = ~28k muestras puede ocupar una corrida entera: es aceptable, pero si `MaxRowsPerCycle` es menor que lo que traería un solo backfill, partirlo en tramos de días hacia atrás desde `oldestExisting` (más reciente primero) de modo que cada corrida avance.
7. Actividad (contrato v2 §5, kind `ingestion-kpi`, Info): `{Kpi}/{Domain}: backfilled history {n} bucket(s) [{from:yyyy-MM-dd HH:mm}–{to:yyyy-MM-dd HH:mm}), {s} sample(s)`. Si la fuente no está disponible, el mismo Warning de hoy.
8. Errores de la fuente durante el backfill: no fallan la corrida ni mueven el watermark; se reintenta en la próxima.

## Tests requeridos (en `KpiIngestionServiceTests.cs`, colección no paralela existente)

| Test | Aserción |
|---|---|
| `Ingestion_FirstRun_BackfillsRequiredHistory` | BaselinePercent 7 días, bucket 1 h, Lookback 24 h → primera fetch desde `to - 8 días` (7 días + 1 bucket), muestra por bucket 167 |
| `Ingestion_HistoryGap_BackfillsOnlyMissingRange` | buckets existentes desde `to - 24 h`, método cambiado a BaselinePercent 7 días → una fetch `[to - 8d, to - 24h)` y watermark sin cambios |
| `Ingestion_NoGap_DoesNotBackfill` | historia completa → ninguna fetch extra |
| `Ingestion_BackfillRespectsMaxRowsPerCycle` | backfill partido en tramos; corridas sucesivas avanzan hasta completar |
| `Ingestion_BackfillSourceFailure_KeepsWatermarkAndRetries` | fuente lanza en el backfill → watermark igual, la próxima corrida reintenta |
| `Ingestion_WithoutEvaluator_KeepsOldBehaviour` | sin `IKpiEvaluator` → primera fetch desde `to - LookbackWindow` |

## Criterios de aceptación

1. Con BaselinePercent 7 días, el monitor puede evaluar desde la primera hora tras la primera ingesta (demostrado en test de servicio + verificación en vivo).
2. El backfill nunca mueve el watermark ni cambia la muestra por bucket.
3. Todos los tests existentes siguen verdes sin modificaciones; 6 tests requeridos verdes.
4. La actividad del backfill aparece en la consola de Daemons con el formato indicado.
