---
name: spec1000-dt-demo-telemetry
description: "Fase DT (rama company-profile, local): Demo Telemetry Server — App Insights falso sobre Kustainer, generador de telemetría sintética para 4 empresas y modo demo de la plataforma. Una sección por tarea DT0–DT5."
agent: spec1000-implementer
argument-hint: "TASK=DT<n> WORKTREE=<ruta> ITERATION=<n>"
---
# Fase DT · Demo Telemetry Server

**ESTADO: APROBADO (2026-11-07).** DT0 va primero y solo. Después DT1, DT2 y DT3 en paralelo; los cuatro paquetes DT4* cuando DT3 está fusionado; DT5 al final; DTRV revisa todo.

**Reglas (todas las tareas):**
- **Prohibido `git push`, `gh pr create` o publicar nada.** Todo commit va al branch del worktree.
- Contrato congelado: `.github/spec1000/contracts-demo-telemetry.md` (v5). Léelo completo antes de empezar. Si algo del contrato es imposible, **no lo cambies**: documenta el bloqueo en `.spec1000/findings/<TASK>-blocker.md` y termina.
- Análisis y diagramas: `docs/architecture/demo-telemetry-server-analisis-2026-11-07.html` (puede no estar en el worktree: está en `C:\Arrivia\ai-reliable-platform\docs\architecture\`).
- Ownership, tests requeridos y nivel E2E: `.github/spec1000/tasks.json`. Solo tocas `allowedPaths`.
- Sin warnings nuevos (baseline), sin bajar la cuenta de tests, todo lo existente verde.
- **Nada de datos reales:** ninguna fila, nombre de cliente real, email o id copiado de producción.
- Kustainer ya está descargado: `mcr.microsoft.com/azuredataexplorer/kustainer-linux:latest`. Arranca en < 10 s. API: `POST /v1/rest/query` y `POST /v1/rest/mgmt` con `{"db","csl"}`, sin auth. Probado: `.create database X volatile|persist`, `.set-or-append T <| range …` (200 k filas en segundos), `union database('a').T, database('b').T`.
- El stack real (`docker compose up` sin perfil) **no se debe romper ni reiniciar sin necesidad**: otros agentes lo usan. Para pruebas manuales usa contenedores con nombre propio de tu tarea (`jm-<task>-…`) y puertos 81xx libres, y bórralos al terminar.
- Al terminar: gate en modo Check verde (`powershell -NoProfile -ExecutionPolicy Bypass -File .github/spec1000/run-gate.ps1 -Task <TASK> -Mode Check -IncludeE2E`), commit, y `status: "ready-for-test"` en `.spec1000/current-task.json`.

---

## DT0 · Esqueleto, esquema Kusto, reescritor `app()` y compatibilidad (go / no-go)
1. **Proyectos** (net10.0, nullable, warnings como el repo) y agrégalos a `Portal/JourneyMonitor.slnx` en carpeta `/demo/`:
   - `Portal/demo/FakeAppInsights/FakeAppInsights.csproj` (Web SDK) con un `Program.cs` mínimo (`/health`) — DT1 lo completa.
   - `Portal/demo/FakeAppInsights.Tests/` (xUnit).
   - `Portal/demo/DemoGenerator/DemoGenerator.csproj` (Web SDK) con `Program.cs` mínimo — DT3 lo completa.
   - `Portal/demo/DemoGenerator.Tests/` (xUnit) con un test trivial de humo.
2. **`Portal/demo/kusto/schema.kql`:** las 6 tablas de §4.1, idempotente (`.create-merge table`).
3. **Reescritor** en `Portal/demo/FakeAppInsights/Rewrite/` usando **`Microsoft.Azure.Kusto.Language`** (árbol sintáctico, no regex sobre el texto):
   - `AppReferenceRewriter.Rewrite(string kql, IApplicationCatalog catalog)` → KQL con `app('…')` → `database('…')` según §3.1 (ARM id, nombre o appId). `workspace()` → excepción tipada `UnsupportedKqlException`. App desconocida → `UnknownApplicationException`.
   - `TimespanFilter.Apply(string kql, DateTimeOffset from, DateTimeOffset to)` → cada referencia a las 6 tablas (con o sin `database()`) queda filtrada por `timestamp between (from .. to)`. `TimespanParser.Parse(string iso, DateTimeOffset now)` para `PT6H`, `P1D`, `<start>/<end>`, `<start>/<duration>`.
4. **Compose (perfil `demo`, dueño único de `docker-compose.yml` en esta fase):** agrega los 5 servicios de §2 con sus puertos, env, volúmenes (`kusto-demo-data`), `profiles: ["demo"]`, `mem_limit` y healthchecks. `fake-appinsights` y `demo-generator` construyen desde `Portal/demo/FakeAppInsights/Dockerfile` y `Portal/demo/DemoGenerator/Dockerfile` (créalos: multi-stage `dotnet/sdk:10.0` → `dotnet/aspnet:10.0`). `functions-demo` y `web-demo` reutilizan las imágenes/build de `functions`/`web` con el env de §5. `sql-init` crea también `platformDemo` (revisa cómo se crea `platformDBQA` y replica). **Verifica que `docker compose config` sin perfil produce los mismos servicios que antes** (lista idéntica).
5. **Compatibilidad (go/no-go):** `Portal/demo/compat/run-compat.ps1` levanta un Kustainer temporal (`jm-dt0-kusto`, puerto 8198), aplica el esquema en 2 bases de prueba con ~10 k filas sintéticas, obtiene **todas** las queries built-in del catálogo de la plataforma (busca `IKqlQueryTextProvider` / recursos embebidos / `KqlQueryCatalog` y los templates de Scouts/Inspector/KPIs; sustituye `{{SOURCE:x}}` y placeholders por valores de prueba), las pasa por el reescritor y las ejecuta. Escribe `Portal/demo/compat/compat-report.md` con: total, OK, fallidas (con el error y la causa). **Criterio go:** ≥ 95 % OK. Si es menor, el reporte lista exactamente qué falta y por qué; el tester decide.
6. **Tests requeridos** (FakeAppInsights.Tests): ver `requiredTestNames` en tasks.json.
7. E2E nivel `schema` (el stack real sigue igual).

## DT1 · FakeAppInsights (servidor HTTP que imita App Insights)
- Completa `Portal/demo/FakeAppInsights` según §3 al pie de la letra: `POST`/`GET /v1/apps/{appId}/query`, `GET /v1/apps`, `GET /health`, cuerpo de error §3.4, mapeo de tipos §3.1, `dynamic` como string JSON, `datetime` ISO-8601 con `Z`, múltiples tablas.
- Detrás de una interfaz `IKustoExecutor` (implementación real con `Microsoft.Azure.Kusto.Data` o HTTP directo a `/v1/rest/query`) para que los tests unitarios usen un fake y un test de integración opcional use Kustainer real.
- `IApplicationCatalog` real: lista bases (`.show databases`) con caché corta (30 s); appId → base: la base cuyo nombre es igual al appId sin prefijo `demo-` **o** la que tenga una tabla de metadatos `_app(appId:string)`; documenta en `README` del proyecto cuál usaste y respétalo con DT3 (el generador crea la base `<database>` del archetype y registra su `appId`; la forma de registro la defines tú en `Portal/demo/FakeAppInsights/README.md`, DT3 la consume).
- Logs: una línea por consulta con appId, ms, filas, sin el texto completo de la query en nivel Information.
- Smoke manual: `docker compose --profile demo build fake-appinsights`, y un contenedor temporal contra un Kustainer temporal; bórralos al terminar.
- Tests requeridos en tasks.json. E2E `schema`.

## DT2 · Plataforma en modo demo
- `InsightsOptions.QueryEndpoint` (default `https://api.applicationinsights.io`) usado por `AppInsightsTelemetrySource` (hoy es constante). El default deja el comportamiento idéntico.
- Comodín `*` en `Insights:QueryApiKeys` (usa el separador que ya parsea `ParseApiKeys`; documenta el formato exacto en el XML doc).
- `DemoOptions` (`Demo:Enabled`, `Demo:Label` default `"DEMO — synthetic data"`) en Core/Options; Web expone `GET /api/demo-mode` (sin SQL ni telemetría: solo opciones); `app.js` pide ese endpoint al iniciar y, si `enabled`, muestra un banner fijo arriba del header (`role="status"`, clase `demo-banner`, estilo en `styles.css`). Con `false` no se agrega nada al DOM.
- `POST /api/knowledge/import?subPath=` (§5): restringe la lectura a la subcarpeta, rechaza `..`, rutas absolutas o subcarpetas inexistentes con 400; el dominio del documento es la primera carpeta bajo `subPath`.
- **No toques `docker-compose.yml`** (es de DT0). Si falta algo en el env de `functions-demo`/`web-demo`, escríbelo en `.spec1000/findings/DT2-compose.md` para el orquestador.
- Tests requeridos en tasks.json. E2E `portal` (demuestra que el stack real no cambió).

## DT3 · DemoGenerator (historia, feeder en vivo, escenarios)
- Completa `Portal/demo/DemoGenerator` según §4: modelo del archetype + validación con mensajes claros, síntesis determinística por semilla, latencia log-normal ajustada a p50/p95, estacionalidad, entidades Zipf, `operation_Id` compartido entre tablas, eventos de negocio, efectos de escenario (§4.2), escalas `tiny`/`laptop`/`showcase`.
- Ingesta **dentro de Kusto** siempre que se pueda (`.set-or-append … <| range …` con `hash()`/`rand()` sembrado por fila para reproducibilidad), o `datatable`/`.ingest inline` por lotes. Mide y reporta en `/api/reset` filas por tabla y segundos. Objetivo `laptop` (4 empresas, 8 días) en < 10 min en esta máquina.
- Registro appId ↔ base según lo que definió DT1 en `Portal/demo/FakeAppInsights/README.md`.
- API §4.3 completa, feeder `BackgroundService`, estado de escenarios en memoria (y en `GET /api/scenarios/active`).
- **`PackValidator`** público (lo usan los tests de DT4*): `PackValidator.Validate(string packDir)` → lista de errores. Verifica: archetype válido; `profile.json` parsea y sus matchers cubren las URLs de cada step; cada `app` del archetype tiene un `sources.json` con ese appId/base; `entityIdentitySources` incluye la `dimension`; los `partner` del archetype existen en `profile.partners`; `kpis.json` válidos (bucket 5 min, frecuencia 1 min, `profileKey` correcto, ≥ 2 KPIs `kql:` sobre `customEvents`); `corpus` con ≥ 10 `.md` que contienen “Root Cause Analysis”. Incluye un pack mínimo de ejemplo **solo en tests** (`DemoGenerator.Tests/Fixtures/`), no en `packs/`.
- Dockerfile ya existe (DT0); ajústalo si hace falta.
- Tests requeridos en tasks.json. E2E `schema`.

## DT4T · Pack `tourism` (Arrivia demo) — DT4B `bank` · DT4A `aml` · DT4R `retail`
Cada uno crea **solo** `Portal/demo/packs/<companyKey>/**` y su test `Portal/demo/DemoGenerator.Tests/Packs/<Name>PackTests.cs` (`PackValidator.Validate` sin errores + chequeos propios). Contenido según §6:
- **Realismo:** volúmenes, curvas, p50/p95 y tasas de error plausibles para la industria; nombres de servicios, rutas y excepciones creíbles; partners concretos de la industria (con nombres genéricos/ficticios, sin marcas reales de terceros salvo la propia empresa demo).
- **≥ 4 escenarios** (latencia tras deploy, tormenta de excepciones en un paso, caída de partner, degradación de una sola entidad) + **≥ 1 caída de negocio** + **1 pico legítimo que no debe alertar**; **≥ 3 `historicalIncidents`** dentro de los últimos 3 días.
- **Corpus:** ≥ 10 RCAs sintéticos en Markdown con el formato corporativo, cada uno coherente con un escenario (mismas apps, pasos, partners, excepciones), con fechas en los últimos 18 meses. Nada copiado de RCAs reales.
- **README.md** con el guion de demo de 15 min de esa empresa.
- **Específico:**
  - **DT4T tourism:** reusa el perfil real de Arrivia (lee `GET /api/settings/company-profiles` del stack real en `http://localhost:5080` o el seed SQL del repo): mismos servicios, pasos, matchers y `entityIdentitySources`; apps `sor-frontend`, `cruise-core`, `cruise-api`, `club`, `air-ui`, `air-api` con los **mismos alias** (§6). Incluye `air` como servicio. Escenarios: pool de conexiones de Cruise Core agotado (latencia >100 s en Total Price), caída de búsqueda de NG Air, tormenta de excepciones en Hotel PreBook, un club degradado, caída de bookings.
  - **DT4B bank** (`banco`, Banco Andino (demo)): web-banking, mobile-api, payments-svc, core-gateway; entidad sucursal/canal; partners core bancario, red de tarjetas, cámara ACH, antifraude.
  - **DT4A aml** (`kinectify`, Kinectify (demo)): screening-api, onboarding-kyc, case-mgmt, alerts-worker; entidad cliente del SaaS; partners proveedor de sanciones, verificación de identidad, analítica blockchain; `privacy` con `piiDimensions` y `patterns`.
  - **DT4R retail** (`mercadolibre`, Mercado Libre (demo)): storefront, search-api, checkout-api, payments, shipping; entidad seller/site (MLA, MLB, MLM, MCO, MLC); partners pasarela de pago y transportistas; pico Hot Sale.
- E2E `schema`.

## DT5 · Kit de demo y E2E `demo`
- `Portal/demo/scripts/` (§7): `demo-up.ps1`, `demo-reset.ps1`, `demo-scenario.ps1`, `demo-check.ps1` (PowerShell 5.1 compatible: sin `&&`, `||`, `??`). Usan los secretos locales del `.env`/compose, nunca los imprimen.
- `Portal/tests/e2e/demo.ps1` (etapa `demo`) según §7: imprime `[PASS]`/`[FAIL]` por chequeo y sale con 0 solo si todo pasa. Debe correr con `-Scale tiny` en < 15 min.
- `Portal/demo/README.md`: arquitectura (enlaza el HTML de análisis), puesta en marcha, reset, escenarios por empresa, checklist pre-demo, troubleshooting, límites (funciones solo-Arrivia, licencia de Kustainer).
- Corre el ciclo completo con `-Scale laptop` al menos una vez y anota tiempos y filas en el README.
- **Hallazgos heredados del tester de DT0 (obligatorios):**
  - `functions-demo` hereda del compose los appIds reales de App Insights y `JourneySync__Domains`: la sincronización de journeys pegaría al falso con 404. Sobrescríbelos (vacíos, o apuntando a los appIds `demo-tourism-*` del pack tourism para que Customer Journeys funcione con datos demo).
  - `functions-demo` no tiene configuración de LLM: documenta en el README cómo configurarlo (Settings → LLM Client del portal demo) y que `demo-check.ps1` lo verifique.
  - `run-compat.ps1` no cubre el KQL que la plataforma arma en código (`TelemetryProbeKql`, `ClubDiscoveryKql`, variantes de `ClubFilter`): agrégalos a la etapa E2E `demo` como consultas reales contra el falso.
- E2E nivel `demo`.
- **Memoria de Kustainer (obligatorio, hallazgo de DT4):** un solo pack a escala `laptop` deja Kustainer en ~5.97 de 6 GB, y con 4 GB falla con errores 520. Con las 4 empresas juntas 6 GB no alcanza. Mide el pico real con los 4 packs a `laptop` y ajusta el default de `KUSTAINER_MEM` en compose (p.ej. `10g`/`12g`, dejando margen para el stack real en una máquina de 16 GB) y/o haz que `demo-reset.ps1` reduzca `-Days` automáticamente si el límite es bajo. `demo-check.ps1` debe avisar si la memoria de Kustainer supera el 85 %. Documenta los números en el README.
- **Tiempos de reset:** tourism solo tardó 270 s (8 días). Mide el reset de las 4 empresas y documenta; si supera ~10 min, permite resetear por empresa (`-Companies`) y deja `laptop` con 8 días solo como recomendación.

## DTRV · Revisión final (solo lectura + verificación)
- Revisa el diff completo de la fase contra el contrato v5 y el análisis. Ejecuta build, todos los tests, `e2e.ps1 -Level demo` y `e2e.ps1 -Level full` (el stack real no cambió).
- Verifica a mano en el navegador (`http://localhost:5090`): banner DEMO, selector con las 4 empresas, pestaña Daemons con actividad, un escenario disparado en vivo detectado por un KPI de 5 min, Memory Bank con RCAs sintéticos de la empresa activa, y que `http://localhost:5080` no muestra banner ni datos demo.
- Escribe `docs/architecture/demo-telemetry-informe-revision-2026-11-07.md` con hallazgos por severidad y veredicto. Si hay High/Medium, el orquestador abre tareas FX.
