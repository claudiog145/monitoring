---
name: spec1000-sd-scout-detection
description: "Fase SD (rama company-profile, local): los Scouts adoptan el enfoque de detección de los daemons (grano por entidad, mismo KpiEvaluator, Shadow). Una sección por tarea SD1–SD7."
agent: spec1000-implementer
argument-hint: "TASK=SD<n> WORKTREE=<ruta> ITERATION=<n>"
---
# Fase SD · Scouts con el mismo enfoque de detección que los daemons

**ESTADO: APROBADO (2026-11-06).** SD0 primero; el resto arranca cuando SD0 se fusiona y el orquestador etiqueta `spec1000-contracts-v4`.

**Reglas:**
- Prohibido `git push` y `gh pr create`.
- Contrato: `contracts-scout-detection.md`, más v1, v2, v3 y `contracts-company.md`.
- Plan completo: `docs/architecture/spec-1000-scouts-deteccion-plan-2026-11-06.md`.
- Ownership y tests requeridos: `tasks.json`.

## SD0 · Contrato v4 en código (va primero, solo)
Implementa **solo tipos, interfaces y tests de contrato**, sin lógica de negocio:
- **`Kpi.cs`:**
  - `enum KpiGrain { Domain, Entity }`;
  - en `KpiDefinition`: `Grain` (init, default `Domain`) y `MaxEntitiesPerBucket` (init, default 500, `Validate()` exige 1–5000);
  - `EntityKey` (init, default `""`) en `KpiBucket`, `KpiDetectionState` y `KpiAnomaly`;
  - `FailedCount` (init, `long?`) en `KpiBucket`;
  - `EntityKey`, `P50`, `P95`, `Mean` y `FailedCount` como miembros opcionales de `KpiSourceBucket` (init o parámetros opcionales al final, sin romper los usos existentes).
- **`Validate()` deja de restringir `Domain` a `KpiDomains.All`.** Pasa a exigir solo que no esté vacío y tenga ≤ 64 caracteres. La validación contra el perfil la hace AG1. `KpiDomains.All` se conserva.
- **Interfaces en `IKpi.cs`:**
  - `IKpiBucketStore.GetEntityRangeAsync(id, from, to, ct)`;
  - `IKpiStateStore.GetEntityStatesAsync(id, ct)`;
  - sobrecargas con `string entityKey` donde corresponda.
  - Todos los miembros nuevos son **default interface methods** que devuelven el comportamiento v3 (entidad `""`) o una lista vacía, así que las implementaciones existentes compilan sin cambios. SD1 los implementa de verdad.
- **Tipos nuevos:**
  - `Core/Domain/ScoutShadow.cs`: `ScoutShadowComparison` (record, §4) y `ScoutShadowAgreement` (`both` / `rules-only` / `method-only`).
  - `Core/Abstractions/IScoutShadow.cs`: `IScoutShadowStore` (`AddRangeAsync`, `GetRecentAsync(board, hours, profileKey, ct)`, `GetCountsAsync`).
  - `ScoutOptions.DetectionMode` (`Rules` | `Method`, default `Rules`).
- **`CompanyModel.cs`:** parámetros opcionales al final de `CompanyProfile`:
  - `Partners`, lista de `PartnerDefinition(Key, DisplayName, Matchers)` con `PartnerMatcher(Kind, Value)` y las constantes de `PartnerMatcherKind` de §5;
  - `Privacy`, de tipo `PrivacySettings(bool AllowRawEvidence = false, IReadOnlyList<string>? PiiDimensions = null, IReadOnlyList<string>? Patterns = null)`.
  - Un JSON de perfil sin esos campos sigue siendo válido.
- **`Core/Monitoring/Privacy/IEvidenceRedactor.cs`:** `RedactionResult Redact(string text, CompanyProfile profile)`, donde `RedactionResult` es `(string Text, IReadOnlyDictionary<string,int> CountsByPattern)`.
- **Tests:** los de `ContractV4Tests` en `requiredTestNames`. Ajusta los tests existentes que afirmaban "Domain must be hotels…" a la nueva regla.
- **E2E `schema`.** No toques SQL: lo hace SD1.

## SD1 · Persistencia por entidad
- Escribe SQL idempotente en dos pasos: columnas, después las PKs nuevas (drop y create solo si la PK actual no incluye `EntityKey`), y después los índices.
- Crea la tabla `monitoring.ScoutShadowComparison` en `CreateMonitoringScoutShadow.sql` y regístrala en `init-sql.sh` y en `MonitoringSchemaGate`.
- Stores SQL e in-memory con `entityKey`. Retención de entidad: 8 días.
- Corre los scripts dos veces sobre `platformDB`, que tiene datos reales, sin perder filas.

## SD2 · KQL desde el perfil
- Convierte `kpi-latency-*` y `kpi-exceptions-*` en plantillas `*.kql.template.txt` que se renderizan con `CompanyKqlTemplater` para el `ProfileKey` de la definición.
- Quita los hosts escritos a mano: el dominio pasa a cubrir todos los hosts del perfil.
- La variante `{{grain}} = entity` agrupa por `EntityIdentity` y etapa, y devuelve P50, P95, Mean, conteo y fallas, sin muestras.
- La salida de grano dominio mantiene el contrato v1.

## SD3 · Ingesta por entidad
- `Grain = entity`: una fila `KpiBucket` por entidad y bucket, sin `KpiSample`, con el tope `MaxEntitiesPerBucket` (las de mayor tráfico primero).
- Reutiliza el watermark y el backfill de H1.
- El grano dominio no cambia.

## SD4 · EntityDetectionMonitor
- Corre en el timer del monitor de Application Health, después del monitor de pilares.
- Usa la **misma** `IKpiEvaluator` por serie de entidad, con estado "n of m" y cooldown por entidad.
- Una anomalía con `EntityKey` no toca el estado del pilar y no envía Triage.
- Escribe una sola línea de actividad por definición, con el texto exacto del contrato §3.

## SD5 · Scout en Shadow
- Al terminar cada ciclo, compara el board (reglas fijas) con las anomalías de entidad de la ventana.
- Guarda `ScoutShadowComparison` según §4 y agrega `GET /api/scouts/{board}/shadow`.
- Con `DetectionMode = Rules`, el escalamiento al Inspector no cambia (test obligatorio).

## SD6 · Seed y configuración
- Seed de KPIs de entidad por dominio: Latency P95 y Exceptions count, bucket 1 h (igual que el daemon), RobustZScore `{windowBuckets:24,zThreshold:3}`, "2 of 2", cooldown 120 min, `MinObservationsPerBucket` 30 para latencia y 0 para excepciones, `Enabled = true` (solo alimenta el Shadow).
- `grain` en la API, en los settings del portal y en HealthConsole.

## SD7 · Vista en Simian Army
- Tarjeta Shadow por Scout: `both`, `rules-only` y `method-only` en 24 h, más la lista de los últimos casos con enlace al paquete si existe.
- Sin lógica de detección en el navegador.

---

# Fase AG · Daemons agnósticos de compañía (contrato v4 §5)

**Objetivo:** que otra compañía (p. ej. Kinectify, AML para casinos) use los daemons con sus propios servicios, operadores, partners y KPIs de negocio, **solo con configuración**.

**Prueba de aceptación global:** el E2E `agnostic`, con el perfil `acme`, que no tiene ningún literal de Arrivia.

## AG1 · Dominios desde el perfil
- `Domain` debe ser un `Services[].Key` del perfil de la definición.
- `KpiDomains.All` queda solo como semilla de `arrivia`.
- Un dominio desconocido devuelve 400 en el POST y, en ejecución, el Warning exacto de §5.

## AG2 · Fuente desde el catálogo
- El servicio se resuelve en este orden: servicio → `TelemetrySourceAliases` → `monitoring.TelemetrySource` → `IKqlSourceResolver`.
- `InsightsOptions` solo es fallback del incumbente.

## AG3 · KPIs `kql:{queryKey}`
- La consulta sale del catálogo auditado `monitoring.KqlQuery`, solo con los tokens permitidos y con dry-run de 1 h al guardar.
- La salida debe respetar el contrato v1 (más las columnas de entidad opcionales).
- Habilita KPIs de negocio y de frescura sin código.

## AG4 · Partners
- Agrega `CompanyProfile.Partners` con matchers sobre `dependencies` y el `SourceKey` `appinsights:dependency` con grano entidad (partner).
- La anomalía de un partner va a Triage con `stage = partner:{key}`.

## AG5 · Evidencia de negocio agnóstica
- Para un perfil sin Fabric, la evidencia de Business sale de sus propios KPIs Business.
- Fabric queda solo para `arrivia`.

## AG6 · Ocultamiento de datos personales
- `IEvidenceRedactor` corre en cada prompt de Triage e Inspector.
- `Privacy` es configurable por perfil y está cerrado por defecto en perfiles nuevos.
- La auditoría guarda solo conteos.

## AG7 · Onboarding sugiere KPIs
- `suggestedKpis[]` va en el draft, todos con `enabled:false`: uno por servicio y uno por partner descubierto.

## AGE2E · Segunda compañía de punta a punta
- Nuevo nivel `agnostic` en `e2e.ps1`: perfil `acme`, fuente, KPI `kql:`, partner, ingesta, detección, Triage con `ProfileKey = acme` y aislamiento en las dos direcciones.
- `CouplingInventoryTests` no crece.

## AG6B · Ocultamiento en el resto de los agentes
- Aplica el mismo IEvidenceRedactor y las mismas reglas por perfil que AG6 a cada prompt de RcaAgentService, ScoutAssessorService y DoctorAgentService.
- En perfiles no incumbentes, la correlación que Triage guarda en TriageResult.Correlation también se guarda sin datos personales.
- Para el incumbente, sin bloque Privacy, todo sigue igual.


> **SD6 (amendment A3):** also seed domain and entity KPIs (latency P95, exceptions) for the incumbent's `shared` service. Entity KPIs use an empty Stage, which aggregates all stages per entity (A1).


> **AG7 (also owns):** fix the save-path bug. `StructuralProfileWriteValidator` in `Core/Onboarding/OnboardingModels.cs` rejects `path-contains` and any other `StepMatcherKind` the model defines. Build its list from `CompanyProfileValidator.StepMatcherKinds` and add a save regression test.


## AG8 · Pertenencia de las fuentes de telemetría (hallazgo del Tester de AG3)
- `monitoring.TelemetrySource` gana `OwnerProfileKey nvarchar(128) NULL` (idempotente). Las filas existentes quedan con `arrivia`. Una fila nueva toma el perfil que la crea; NULL significa compartida.
- Al guardar un perfil (servicios y alias), se rechaza el alias de otro perfil con 400: `Alias '{a}' belongs to profile '{owner}'.`.
- El test de DI comprueba, con el orden de registro de producción, que un revert en uso devuelve 409.


---

# Ciclo de corrección de SDRV (FX1–FX3). Fuente: docs/architecture/spec-1000-informe-revision-sd-ag-2026-11-07.md

## FX1 · Aislamiento entre compañías al leer (corrige un High y un Medium)
- El fallback a `InsightsOptions` (AG2 y AG3) aplica **solo** a `ProfileKey == KpiProfiles.Default`. Para cualquier otro perfil sin alias, o con una fila sin AppId, el resultado es *unavailable* con el mensaje "No telemetry source configured for service '{s}' of profile '{p}'.".
- En cada resolución, la propiedad del alias se verifica también al **leer**, no solo al guardar: KPIs de dominio y entidad, KPIs `kql:` (incluida la prueba de una hora), dependencias y el board de los Scouts.
  - Un alias cuyo `OwnerProfileKey` no es NULL y es distinto del perfil que lee → unavailable, o el Scout omite esa fuente con un warning.
  - Usa una caché corta, de 30 a 60 s, para no consultar el catálogo en cada fetch.
- Después de una transferencia, el dueño anterior pierde el acceso en el ciclo siguiente.

## FX2 · Backfill por entidad acotado a la retención (Medium)
- Los buckets por entidad se guardan 8 días. Si un KPI de entidad pide más historia, el backfill reconsultaba cada minuto.
- Corrección:
  - La historia requerida para el grano entidad se acota a la retención (8 días menos 1 bucket).
  - Guardar un KPI de entidad cuyo método requiera más devuelve 400: "Entity-grain KPIs keep 8 days; method requires {n} days.".
  - Un test confirma que no hay reconsultas en cada ciclo.

## FX3 · Series por entidad en la evidencia de Triage (Medium)
- Los KPIs de entidad (y los de partner) llegaban al prompt con `Buckets: []`.
- Corrección:
  - La evidencia de Application y de Business incluye, para los KPIs de grano entidad, las **top N entidades** de la ventana (N=5 por defecto, por anomalía o por desvío) con sus buckets acotados.
  - Para una señal `partner:{key}`, la serie de ese partner.
  - Todo pasa por el redactor de AG6 como el resto de las secciones.

## FX4 · Aislamiento en el drill-down del Scout y en las herramientas de logs del Inspector (Low de FX1)
- El drill-down del Scout (`ScoutEvidenceCollector` + `scout-drilldown.kql.txt`) y `LogsAgentTools` y los especialistas del Inspector consultan App Insights con las fuentes fijas del incumbente.
- Para un paquete o caso cuyo `ProfileKey` no es el incumbente, deben usar solo los alias de ese perfil, con verificación de propiedad (`TelemetrySourceOwnershipCheck` de FX1). Si el perfil no tiene fuentes, no consultan y lo dicen en la evidencia.
- Para el incumbente, todo sigue igual.

