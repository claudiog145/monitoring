# Spec 1000 · Contrato v5 — Demo Telemetry Server (fase DT)

**Estado:** CONGELADO al etiquetar `spec1000-contracts-v5`. Cambios solo por enmienda del orquestador (sección *Enmiendas*).
**Análisis fuente:** `docs/architecture/demo-telemetry-server-analisis-2026-11-07.html`.
**Regla absoluta:** todo local. Prohibido `git push`, `gh pr create` o cualquier publicación.

## Decisiones tomadas (preferencias del usuario, 2026-11-07)

| # | Decisión | Valor |
|---|---|---|
| D1 | Motor KQL | **Kustainer** local (`mcr.microsoft.com/azuredataexplorer/kustainer-linux:latest`). |
| D2 | Aislamiento | **Stack DEMO separado** (perfil compose `demo`): base `platformDemo`, contenedores y puertos propios. El stack real no cambia de comportamiento. |
| D3 | Volumen | Escalas `tiny` (E2E, ~1 %), `laptop` (default, ~10 M filas totales, 8 días) y `showcase` (×4). |
| D4 | Detección en vivo | Opción C: KPIs de demo con **bucket 5 min / frecuencia 1 min** + **incidentes históricos pre-sembrados**. |
| D5 | Nombres | Los que pidió el usuario, siempre con sufijo **“(demo)”** y banner DEMO: Arrivia (demo), Banco Andino (demo), Kinectify (demo), Mercado Libre (demo). Todo dato es sintético. |
| D6 | Datos de Arrivia | Sintéticos, con la forma del perfil real (pasos, matchers, entidades). **Prohibido copiar filas reales** o PII. |
| D7 | Alcance | Incluye KPIs de negocio (vía `customEvents` y fuente `kql:`) y RCAs sintéticos para el Memory Bank. |

## §1 Componentes y ubicación

```
Portal/demo/
  FakeAppInsights/            ASP.NET Core minimal API (net10.0)
  FakeAppInsights.Tests/      xUnit
  DemoGenerator/              ASP.NET Core minimal API + BackgroundService (net10.0)
  DemoGenerator.Tests/        xUnit
  kusto/schema.kql            esquema App Insights (6 tablas) — idempotente
  packs/<companyKey>/         paquete de empresa (§6)
  scripts/                    demo-up.ps1, demo-reset.ps1, demo-scenario.ps1, demo-check.ps1
  README.md                   guía de uso de la demo
Portal/tests/e2e/demo.ps1     etapa E2E `demo`
```
Los 4 proyectos se agregan a `Portal/JourneyMonitor.slnx` (carpeta `/demo/`). Ninguno referencia `JourneyMonitor.Core`, `Infrastructure`, `Functions` ni `Web` (son independientes de la plataforma). Paquetes permitidos: `Microsoft.Azure.Kusto.Language`, `Microsoft.Azure.Kusto.Data`, `xunit`, `Microsoft.AspNetCore.Mvc.Testing`, `Microsoft.NET.Test.Sdk`, `xunit.runner.visualstudio`. Sin warnings nuevos.

## §2 Servicios Docker (perfil `demo` en `Portal/docker-compose.yml`)

| Servicio | Contenedor | Puerto interno | Host (solo 127.0.0.1) | Notas |
|---|---|---|---|---|
| `kustainer` | `jm-kustainer` | 8080 | 8085 | `ACCEPT_EULA=Y`, volumen `kusto-demo-data`, `mem_limit` configurable (`KUSTAINER_MEM`, default `6g`). |
| `fake-appinsights` | `jm-fake-appinsights` | 8080 | 8086 | `FakeAppInsights__KustoUrl=http://kustainer:8080`, `FakeAppInsights__ApiKey=${DEMO_API_KEY:-jm-demo-key}`. |
| `demo-generator` | `jm-demo-generator` | 8080 | 8087 | `Demo__KustoUrl`, `Demo__PacksPath=/packs` (monta `./demo/packs:ro`), `Demo__Scale`, `Demo__FeederIntervalSeconds=60`. |
| `functions-demo` | `jm-functions-demo` | 80 | 8083 | Misma imagen que `functions`, `MSSQL_DATABASE=platformDemo`, ver §5. |
| `web-demo` | `jm-web-demo` | 8080 | 5090 | Misma imagen que `web`, apunta a `functions-demo`, `Demo__Enabled=true`. |

`sql-init` crea también `platformDemo` (mismo esquema que `platformDB`). Los servicios existentes **no** llevan el perfil `demo` y no cambian. `docker compose up` sin `--profile demo` levanta exactamente lo mismo que hoy.

## §3 Contrato HTTP de FakeAppInsights (imita App Insights REST)

### 3.1 `POST /v1/apps/{appId}/query`
- **Auth:** header `X-Api-Key` igual a `FakeAppInsights:ApiKey` → si no, `401` con el cuerpo de error de §3.4 (`code: "InvalidApiKeyError"`). `Authorization: Bearer …` se rechaza igual (no hay Entra en demo).
- **Body:** `{ "query": "<KQL>", "timespan": "<ISO8601 opcional>" }`. También se acepta `GET /v1/apps/{appId}/query?query=…&timespan=…`.
- **appId → base:** `appId` debe existir como base en Kustainer (las crea el generador, §4). Si no existe → `404` `code: "ApplicationNotFoundError"`.
- **Reescritura (con `Kusto.Language`, nunca regex sobre el texto completo):**
  - `app('<x>')` donde `<x>` es un ARM id `/subscriptions/*/resourceGroups/*/providers/microsoft.insights/components/<name>`, un nombre, o un appId → `database('<db>')`. `<db>` = `<name>` si existe como base; si no, se busca por appId. No resoluble → `400` `code: "BadArgumentError"`, mensaje `Unknown application '<x>'.`
  - `workspace(...)` → `400` (no soportado en demo).
  - Las tablas sin prefijo se resuelven en la base del `appId` de la URL.
- **timespan:** formatos `PT6H`, `P1D`, `<start>/<end>`, `<start>/<duration>`. Cuando viene, **toda** referencia a `requests`, `exceptions`, `dependencies`, `traces`, `pageViews`, `customEvents` (con o sin `database()`) queda filtrada a `timestamp` dentro del rango (fin = ahora si no se indica). Sin timespan, no se filtra.
- **Respuesta 200:**
  ```json
  { "tables": [ { "name": "PrimaryResult", "columns": [ {"name":"…","type":"…"} ], "rows": [ [ … ] ] } ] }
  ```
  Tipos: `datetime`, `string`, `dynamic`, `real`, `long`, `int`, `bool`, `timespan`, `guid`, `decimal` (mapeo directo desde ADX; `double`→`real`, `int64`→`long`, `int32`→`int`, `boolean`→`bool`, `sbyte`→`bool`). `datetime` en ISO-8601 UTC con `Z`. `dynamic` como **string JSON** (como hace App Insights). Varias tablas resultado → `PrimaryResult`, `Table_1`, `Table_2`…
- **Errores de KQL** (sintaxis/semántica) → `400` `code: "BadArgumentError"` con el mensaje del motor en `innererror.message`.

### 3.2 `GET /health` → `200 {"status":"ok","kusto":"reachable|unreachable"}`.
### 3.3 `GET /v1/apps` (extra demo) → lista `[{ "appId","database" }]`.
### 3.4 Cuerpo de error (formato App Insights)
```json
{ "error": { "message": "...", "code": "BadArgumentError", "innererror": { "code": "...", "message": "..." } } }
```

## §4 DemoGenerator

### 4.1 Esquema (`Portal/demo/kusto/schema.kql`)
Una base por app. Seis tablas con el esquema de App Insights (nombres y tipos exactos):
- `requests(timestamp:datetime, id:string, name:string, url:string, resultCode:string, duration:real, success:bool, performanceBucket:string, itemType:string, customDimensions:dynamic, customMeasurements:dynamic, operation_Name:string, operation_Id:string, operation_ParentId:string, session_Id:string, user_Id:string, user_AuthenticatedId:string, client_Type:string, client_Model:string, client_OS:string, client_IP:string, client_City:string, client_StateOrProvince:string, client_CountryOrRegion:string, client_Browser:string, cloud_RoleName:string, cloud_RoleInstance:string, appId:string, appName:string, iKey:string, sdkVersion:string, itemId:string, itemCount:int, source:string)`
- `exceptions(timestamp, problemId, handledAt, type, message, assembly, method, outerType, outerMessage, outerAssembly, outerMethod, innermostType, innermostMessage, innermostAssembly, innermostMethod, severityLevel:int, details:dynamic, customDimensions:dynamic, customMeasurements:dynamic, operation_Name, operation_Id, operation_ParentId, session_Id, user_Id, client_*, cloud_RoleName, cloud_RoleInstance, appId, appName, itemId, itemCount:int)`
- `dependencies(timestamp, id, target, type, name, data, success:bool, resultCode, duration:real, performanceBucket, customDimensions:dynamic, customMeasurements:dynamic, operation_Name, operation_Id, operation_ParentId, session_Id, user_Id, client_*, cloud_RoleName, cloud_RoleInstance, appId, appName, itemId, itemCount:int)`
- `traces(timestamp, message, severityLevel:int, customDimensions:dynamic, customMeasurements:dynamic, operation_Name, operation_Id, operation_ParentId, session_Id, user_Id, client_*, cloud_RoleName, cloud_RoleInstance, appId, appName, itemId, itemCount:int)`
- `pageViews(timestamp, id, name, url, duration:real, performanceBucket, customDimensions:dynamic, customMeasurements:dynamic, operation_Name, operation_Id, session_Id, user_Id, client_*, cloud_RoleName, appId, appName, itemId, itemCount:int)`
- `customEvents(timestamp, name, customDimensions:dynamic, customMeasurements:dynamic, operation_Name, operation_Id, session_Id, user_Id, client_*, cloud_RoleName, appId, appName, itemId, itemCount:int)`

(`client_*` = las 9 columnas `client_` de `requests`; columnas sin tipo = `string`.)

### 4.2 Archetype (`packs/<companyKey>/archetype.json`)
```json
{
  "companyKey": "bank",
  "profileKey": "banco",
  "displayName": "Banco Andino (demo)",
  "apps": [ { "appId": "demo-bank-web-banking", "database": "bank-web-banking", "roleName": "web-banking", "host": "web.bancoandino.demo" } ],
  "entities": { "kind": "branch", "dimension": "BranchId", "count": 120, "zipf": 1.1, "idFormat": "B-{0:000}", "hostTemplate": null },
  "traffic": { "requestsPerDay": 200000, "hourlyCurve": [24 pesos relativos], "weekendFactor": 0.6 },
  "steps": [ {
      "app": "demo-bank-web-banking", "name": "POST /api/transfers", "url": "/api/transfers", "weight": 0.2,
      "p50Ms": 180, "p95Ms": 900, "errorRate": 0.004,
      "exceptions": [ { "type": "System.TimeoutException", "message": "Core gateway timeout", "method": "Transfers.Execute", "weight": 1 } ],
      "dependencies": [ { "partner": "core-banking", "target": "core-gw.bancoandino.internal", "type": "HTTP", "name": "POST /core/transfer", "p50Ms": 120, "p95Ms": 600, "failureRate": 0.002 } ],
      "businessEvent": { "name": "TransferCompleted", "onSuccessOnly": true, "measurements": { "Amount": { "min": 10, "max": 5000 } } }
  } ],
  "scenarios": [ {
      "key": "core-timeout", "title": "Core bancario lento", "description": "…", "defaultDurationMinutes": 30,
      "effects": [
        { "kind": "latency-multiplier", "step": "POST /api/transfers", "factor": 6 },
        { "kind": "error-rate", "step": "POST /api/transfers", "rate": 0.3, "exceptionType": "System.TimeoutException" },
        { "kind": "dependency-failure", "partner": "core-banking", "rate": 0.8 },
        { "kind": "entity-only", "entities": ["B-017"] },
        { "kind": "traffic-multiplier", "factor": 0.6 }
      ] } ],
  "historicalIncidents": [ { "scenario": "core-timeout", "hoursAgo": 30, "durationMinutes": 45 } ]
}
```
Reglas: `effects` sin `entity-only` aplican a todas las entidades; con `entity-only`, solo a esas. `step` omitido = todos los pasos. Todo request genera `operation_Id` único; sus dependencias, excepciones, traces y evento de negocio comparten ese `operation_Id`. La identidad de la entidad se escribe en `customDimensions[entities.dimension]` (y en el host si `hostTemplate` está definido). Latencia log-normal ajustada a p50/p95. Estacionalidad por `hourlyCurve` × `weekendFactor`. Semilla determinística por `companyKey` (`Demo:Seed`) para que un reset sea reproducible.

### 4.3 API del generador (puerto 8080)
| Método | Ruta | Efecto |
|---|---|---|
| `GET` | `/health` | `{"status":"ok","kusto":"reachable","feeder":"running|stopped","lastFedAt":"…"}` |
| `GET` | `/api/companies` | Packs cargados: `[{companyKey, profileKey, displayName, apps:[…], scenarios:[{key,title}]}]` |
| `POST` | `/api/reset` | Body `{ "companies": ["bank"]?, "days": 8?, "scale": "tiny|laptop|showcase"? }`. Borra y recrea las bases de esas empresas, aplica `schema.kql`, hace backfill de `days` días hasta *ahora* e inserta los `historicalIncidents`. Responde `{ companies:[{companyKey, rows:{requests:…, exceptions:…, …}}], elapsedSeconds }`. |
| `POST` | `/api/scenarios/{companyKey}/{scenarioKey}/start` | Body `{ "durationMinutes": n? }` → el feeder aplica los efectos desde ahora. `404` si no existe. |
| `POST` | `/api/scenarios/stop` | Body `{ "companyKey": "…"? }` → detiene uno o todos. |
| `GET` | `/api/scenarios/active` | Escenarios en curso con `startedAt`, `endsAt`. |
| `POST` | `/api/feeder/{start|stop}` | Control del feeder en vivo. |

El **feeder** agrega, cada `Demo:FeederIntervalSeconds`, el intervalo `[lastFedAt, ahora)` para todas las empresas cargadas, con escenarios activos aplicados. La ingesta usa comandos de Kusto (`.set-or-append` con `range`/`datatable`, o `.ingest inline` por lotes); nunca ingesta administrada.

## §5 Plataforma en modo demo (cambios mínimos en la plataforma)
- **`Insights:QueryEndpoint`** (string, default `https://api.applicationinsights.io`): base usada por `AppInsightsTelemetrySource` para `/v1/apps/{appId}/query`. Default ⇒ comportamiento idéntico al actual.
- **`Insights:QueryApiKeys`** acepta la entrada comodín `*` (p.ej. `*:jm-demo-key` o el separador que ya use el parser) que aplica a todo appId sin key propia.
- **`Demo:Enabled`** (bool, default `false`). Web lo expone en `GET /api/demo-mode` → `{"enabled":true,"label":"DEMO — synthetic data"}`. `app.js` muestra un **banner permanente** rojo/naranja arriba del header cuando está activo. Con `false` el portal es idéntico al actual.
- **Corpus por empresa:** `POST /api/knowledge/import` acepta `?subPath=<rel>` que restringe la lectura a `CorpusPath/<rel>` (validado: sin `..`, debe existir). El dominio de cada documento sigue siendo la primera carpeta **bajo** `subPath`.
- En `functions-demo`: `Triage__Mode=Shadow`, notificaciones deshabilitadas, `HealthMonitoring__Enabled=true`, `Insights__QueryEndpoint=http://fake-appinsights:8080`, `Insights__QueryApiKeys=*:${DEMO_API_KEY}`, `KnowledgeImport__CorpusPath=/packs` (monta `./demo/packs:ro`), sin credenciales Azure.

## §6 Paquete de empresa (`Portal/demo/packs/<companyKey>/`)
| Archivo | Contenido | Se carga con |
|---|---|---|
| `archetype.json` | §4.2 | DemoGenerator |
| `profile.json` | CompanyProfile completo (labels, glossary, services con `telemetrySourceAliases`, flows/steps/matchers que **coincidan con las URLs del archetype**, entityIdentitySources con la `dimension` del archetype, partners con los `partner` del archetype, privacy) | `POST /api/settings/company-profiles` |
| `sources.json` | `[{alias, environment:"production", resourceId:"/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-demo/providers/microsoft.insights/components/<database>", appId:"<appId>", ownerProfileKey:"<profileKey>", enabled:true, notes}]` | `POST /api/settings/telemetry-sources` |
| `kpis.json` | KpiDefinitions con `profileKey`, **bucket 00:05:00, frecuencia 00:01:00**, lookback ≥ 1 día; Latency P95 + Exceptions (grano `domain` y `entity`) por servicio, y ≥ 2 KPIs de negocio `kql:` sobre `customEvents`. `enabled:true`. | `POST /api/settings/kpis` |
| `corpus/<Domain>/*.md` | ≥ 10 RCAs **sintéticos** con el formato corporativo (“Root Cause Analysis”, “Bottom Line Up Front”, “What Happened”, “5 Whys”…), coherentes con los escenarios del archetype. | `POST /api/knowledge/import?subPath=<companyKey>/corpus&profileKey=<profileKey>` |
| `README.md` | Guion de demo de esa empresa (qué escenario disparar, qué mirar, qué debe decir el Inspector). | humano |

Empresas: `tourism` (profileKey `arrivia`), `bank` (`banco`), `aml` (`kinectify`), `retail` (`mercadolibre`).
Prefijos de appId/base: `demo-tourism-*`/`tourism-*`, `demo-bank-*`/`bank-*`, `demo-aml-*`/`aml-*`, `demo-retail-*`/`retail-*`. Los alias de `sources.json` son únicos por empresa (`<companyKey>-<role>`), salvo `tourism`, que reutiliza los alias reales de Arrivia (`sor-frontend`, `cruise-core`, `cruise-api`, `club`, `air-ui`, `air-api`) para que el perfil real funcione sin cambios.

## §7 Scripts y E2E
- `scripts/demo-up.ps1` — `docker compose --profile demo up -d --build` + espera de salud de los 5 servicios.
- `scripts/demo-reset.ps1 [-Scale tiny|laptop|showcase] [-Days 8] [-Companies …]` — reset del generador; siembra profiles, sources, KPIs y corpus de cada pack vía APIs de `functions-demo` (con los secretos locales); deshabilita en `platformDemo` toda KPI cuyo `sourceKey` empiece con `fabric:`; deja `arrivia` activo.
- `scripts/demo-scenario.ps1 -Company bank -Scenario core-timeout [-Minutes 30] | -Stop` .
- `scripts/demo-check.ps1` — checklist previo a una demo (salud, datos recientes < 2 min, KPIs evaluándose, LLM configurado, banner activo).
- `Portal/tests/e2e/demo.ps1` (etapa `demo`, invocada por `e2e.ps1 -Level demo`): levanta el perfil demo, `demo-reset -Scale tiny`, verifica contrato del falso, que `functions-demo` lee datos, que para **cada** empresa hay buckets KPI y que un incidente histórico produce `KpiAnomaly` + `TriageSignal` con su `ProfileKey`, y que el stack real (`:5080`) no tiene banner ni datos demo.

## §8 No negociables
1. El stack real no cambia de comportamiento con los defaults (tests existentes verdes, mismo número o más).
2. Ninguna fila real ni PII en packs, corpus o generador.
3. El falso nunca habla con Azure.
4. Todo commit local en el worktree de la tarea; el orquestador fusiona solo con veredicto PASS.

## Enmiendas
- **A1 (2026-11-07, DT0):** `client_*` son las **8** columnas listadas en `requests` (`client_Type, client_Model, client_OS, client_IP, client_City, client_StateOrProvince, client_CountryOrRegion, client_Browser`), no 9.
- **A2 (2026-11-07, orquestador):** el gate busca los tests requeridos en `Portal/tests/**` y en `Portal/demo/*.Tests/**`.
- **A3 (2026-11-07, DT3):** en `sources.json` el `resourceId` usa la suscripción `00000000-0000-0000-0000-000000000000` (GUID válido; la plataforma rechaza `…00000000demo`). El `ownerProfileKey` se envía como `profileKey` al endpoint de fuentes, y cada `kqlText` de un KPI `kql:` se siembra con `PUT /api/settings/kql/{key}` antes de crear el KPI (ver `Portal/demo/DemoGenerator/README.md`).
- **A4 (2026-11-07, DT3):** campos opcionales del archetype: `entities.ids` (lista explícita de ids, p.ej. sites `MLA`, `MLB`…), `partner` en el efecto `latency-multiplier` y `step` en `traffic-multiplier`. Un `traffic-multiplier` acotado a paso o entidad solo puede reducir tráfico (factor ≤ 1). Escala `showcase` requiere `KUSTAINER_MEM` ≥ 12g o menos días.
