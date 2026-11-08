---
name: spec1000-a2-ingesta
description: "Spec 1000 tarea A2: ingesta muestreada por buckets desde Application Insights (KQL, AppInsightsKpiSource, estadísticas, watermark, KpiIngestionService) con tests."
agent: spec1000-implementer
argument-hint: "TASK=A2 WORKTREE=<ruta> ITERATION=<n>"
---
# A2 · Ingesta muestreada por buckets

**Objetivo:** que un timer pueda llenar `KpiBucket`/`KpiSample` para cada KPI habilitado, por buckets de tiempo y con muestra distribuida, sin depender del portal.

## Archivos que te pertenecen

`Core/Monitoring/Ingestion/**`, `Core/UseCases/KpiIngestionService.cs`, `Infrastructure/AppInsights/AppInsightsKpiSource.cs`, `Infrastructure/Kql/kpi-*.txt` y sus tests (`tasks.json` → `A2`).

## Antes de empezar (investigar, no inventar)

- Leé `ITelemetrySource` y cómo lo usan `GetJourneysService`, `LatencySpikeDetectorService` y `SyncIncidentsService` (app id por dominio, Hotels/Cars comparten app, Air requiere tenant).
- Leé `Infrastructure/Kql/hotel-latency-spikes.kql.txt`, `cars-…`, `cruise-…` e `incident-exceptions.txt`: de ahí salen los filtros por dominio/etapa.
- Los KQL embebidos se registran con `<EmbeddedResource Include="Kql\*.txt" />`: tus archivos van **planos** en `Infrastructure/Kql/` (no en subcarpetas) para que queden embebidos sin tocar el `.csproj`.

## Diseño obligatorio

1. **`Core/Monitoring/Ingestion/KpiBucketStatistics.cs`** (`public static`):
   - `double? Percentile(IReadOnlyList<double> values, double p)` = PERCENTILE.INC de Excel (interpolación lineal sobre valores ordenados, `p` en [0,1]); lista vacía → `null`.
   - `KpiBucket Build(int kpiDefinitionId, KpiSourceBucket sourceBucket, IReadOnlyList<KpiSample> samples)` → `SampledCount = samples.Count`, `P50`, `P95`, `Mean`, `StdDev` (poblacional) de `samples.Value`; todos `null` si no hay muestras; `TotalCount` y `Value` del source.
2. **`Core/Monitoring/Ingestion/KpiSampling.cs`**: `int SamplesPerBucket(KpiDefinition d)` = `SampleSize == 0 ? 0 : ceil(SampleSize / max(1, LookbackWindow / BucketSize))`. Ej.: 4000 / (24 h / 1 h) = 167.
3. **`Core/UseCases/KpiIngestionService.cs`** — `public sealed class KpiIngestionService : IKpiIngestionRunner`, constructor:
   ```csharp
   public KpiIngestionService(IKpiDefinitionStore definitions, IEnumerable<IKpiSource> sources, IKpiBucketStore buckets,
       IKpiSampleStore samples, IIngestionWatermarkStore watermarks, IOptions<HealthMonitoringOptions> options,
       TimeProvider time, ILogger<KpiIngestionService> logger)
   ```
   Por corrida:
   - `Enabled == false` → `(0,0,0,0)` sin tocar stores.
   - Para cada definición habilitada (todos los pilares), en orden de `KpiDefinitionId`:
     - Sin `IKpiSource` con `CanHandle(SourceKey)` → log warning una vez por corrida, `Skipped++`, seguir.
     - `to = KpiTime.FloorToBucket(now, BucketSize)` (solo buckets cerrados). `from = watermark ?? (to - LookbackWindow)`. Si `from >= to` → nada que hacer.
     - `FetchAsync(def, from, to, SamplesPerBucket(def))`. `KpiSourceUnavailableException` → warning, `Skipped++`, **no** avanzar watermark.
     - Construir buckets con `KpiBucketStatistics.Build` (muestras agrupadas por `BucketStart`), `UpsertAsync`, `AddAsync(samples)`, luego `SetAsync(watermark = to)`.
     - Llevar la suma de muestras escritas; al superar `MaxRowsPerCycle` terminar la corrida (los KPI restantes van en la próxima).
   - Al final: `PurgeOlderThanAsync(now - SampleRetentionDays)`.
4. **KQL** — un archivo por dominio y fuente: `kpi-latency-{hotels,cars,cruises,air}.txt` y `kpi-exceptions-{hotels,cars,cruises,air}.txt`. Tokens obligatorios (el código los reemplaza): `{{from}}`, `{{to}}`, `{{bucket}}`, `{{perBucket}}`, `{{stage}}` (cadena vacía = todas las etapas).
   - Salida de **latencia**: filas con columnas `kind:string ("bucket"|"sample")`, `BucketStart:datetime`, `TotalCount:long`, `Value:real`, `Timestamp:datetime`, `OperationId:string`. Las filas `bucket` traen `TotalCount = sum(itemCount)` por `bin(timestamp, {{bucket}})`; las filas `sample` traen hasta `{{perBucket}}` requests por bucket con `Value = duration` (usá `partition hint.strategy=native by BucketStart (sample {{perBucket}})` o `top-nested`).
   - Salida de **excepciones**: solo filas `bucket` con `TotalCount = sum(itemCount)` de excepciones y `Value = tasa % sobre requests` del mismo bucket.
5. **`Infrastructure/AppInsights/AppInsightsKpiSource.cs`** — `CanHandle` acepta `appinsights:latency` y `appinsights:exceptions`. Carga el KQL por `SourceKey` + `Domain`, **valida y formatea** cada token (fechas `datetime(yyyy-MM-ddTHH:mm:ssZ)`, bucket `timespan` tipo `1h`/`5m`, `perBucket` entero 0..100000, `stage` solo `[A-Za-z0-9 _-]{0,64}` escapado como string KQL) y nunca concatena texto libre. Dominio sin app configurada → `KpiSourceUnavailableException`. Mapea filas a `KpiSourceResult`.

## Tests requeridos (nombres exactos)

| Test | Aserción |
|---|---|
| `Ingestion_DistributesSampleAcrossBuckets` | SampleSize 4000, ventana 24 h, bucket 1 h → source llamado con `samplesPerBucket = 167` |
| `Ingestion_BucketWithFewerRowsTakesAll` | bucket con 50 muestras disponibles → `SampledCount = 50` |
| `Ingestion_AdvancesWatermarkToLastClosedBucket` | now 14:37 con bucket 1 h → watermark 14:00 |
| `Ingestion_FirstRunStartsAtLookbackWindow` | sin watermark → `from = to - LookbackWindow` |
| `Ingestion_RespectsMaxRowsPerCycle` | 3 KPIs, el 1º supera el máximo → el 2º y 3º no se consultan |
| `Ingestion_SkipsDefinitionsWithoutSource` | `SourceKey = "e2e:none"` → Skipped, sin excepción |
| `Ingestion_SourceUnavailable_DoesNotAdvanceWatermark` | source lanza `KpiSourceUnavailableException` → watermark igual |
| `Ingestion_PurgesSamplesOlderThanRetention` | `PurgeOlderThanAsync(now - 7 días)` |
| `Statistics_PercentileMatchesExcelPercentileInc` | [1,2,3,4] p=0.95 → 3.85; [15,20,35,40,50] p=0.5 → 35 |
| `Statistics_EmptySamples_ProduceNullStats` | sin muestras → P50/P95/Mean/StdDev null |
| `Source_ReplacesAllTokensWithValidatedValues` | el texto final no contiene `{{`; stage con `'` o `;` es rechazado |
| `Source_MapsBucketAndSampleRows` | filas `bucket`/`sample` falsas → buckets y muestras correctos |

## Criterios de aceptación

1. Algoritmo de ingesta exactamente como el punto 3; buckets abiertos nunca se escriben.
2. El reparto de muestra y las estadísticas coinciden con los tests.
3. 8 archivos KQL con los tokens y columnas de salida especificados; ningún valor sin validar entra al texto KQL.
4. Air (o cualquier dominio sin configuración) no rompe la corrida.
5. 12 tests requeridos presentes, significativos y verdes; suite completa verde.
