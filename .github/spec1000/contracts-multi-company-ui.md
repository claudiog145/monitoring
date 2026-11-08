# Spec 1000 · Contrato v6 — Multi-company UI (fase MC)

**Estado:** CONGELADO al etiquetar `spec1000-contracts-v6`. Cambios solo por enmienda del orquestador.
**Pedido del usuario (2026-11-07):** "cuando cambio de company debe cambiar todo, incluso el tema, para saber que estoy en otra compañía". Decisiones: alcance **completo** (todas las pestañas generalizadas), tema = **colores + nombre**.
**Regla absoluta:** todo local; prohibido `git push`, `gh pr create`.
**Contexto:** la app real (`:5080`) lee del Azure falso (`Insights__QueryEndpoint=http://fake-appinsights:8080`, ver `contracts-demo-telemetry.md`). Las 4 empresas demo están cargadas en `platformDB`: `arrivia` (Arrivia (demo)), `banco`, `kinectify`, `mercadolibre`. Mapa de brechas de partida: `docs/architecture/mc-gap-map-2026-11-07.md`.

## §1 Principio
Toda lectura que la UI muestra está **filtrada por la compañía activa**. La UI manda `profileKey=<activo>` en **todas** las lecturas; el backend lo respeta y, si falta, usa la compañía activa del servidor (`monitoring.ActiveCompanyProfile`). Nunca se mezclan filas de dos compañías en una respuesta. Lo que no tenga dato para esa compañía responde vacío con un mensaje honesto, nunca datos de otra.

## §2 Tema por compañía (contrato de código)
- `Core/Company/CompanyModel.cs`: nuevo `public sealed record CompanyTheme(string? PrimaryColor = null, string? AccentColor = null, string? ShortName = null);` y parámetro opcional **al final** de `CompanyProfile`: `CompanyTheme? Theme = null`. Un JSON sin `theme` sigue siendo válido.
- `CompanyProfileValidator`: colores `^#[0-9A-Fa-f]{6}$`; `ShortName` ≤ 32 caracteres, sin `<`/`>`.
- UI (§6): al cargar y al cambiar de compañía aplica `--company-primary`, `--company-accent` (CSS custom properties consumidas por header, tab-nav, botones primarios y banner) y muestra el nombre (`ShortName` o `DisplayName`) junto al título. Sin tema → paleta actual (navy/teal). Contraste: texto del header en blanco o navy según luminancia del primario.

## §3 Persistencia por compañía (SQL idempotente)
`ProfileKey nvarchar(128) NOT NULL DEFAULT ('arrivia')` (filas existentes = `arrivia`, el incumbente) e índice por `ProfileKey` en:
- `incident.Incident` (y la clave de deduplicación de incidentes incluye `ProfileKey`);
- `dashboard.MetricPoint` (clave natural incluye `ProfileKey`);
- nueva tabla **`monitoring.ProfileJourney`** (§4.1) y **`monitoring.ProfileJourneyStep`**.
Registrar en `init-sql.sh` y en el schema gate correspondiente (`MonitoringSchemaGate`/`SqlIncidentSchemaGate`). Las tablas legacy `hotel/air/car/cruise.*` **no se modifican** (quedan como pipeline legacy del incumbente, ya no usado por la UI).

## §4 Lecturas genéricas por perfil (nuevas)
### 4.1 Journeys genéricos
- Construidos desde telemetría con el perfil: requests de los alias de cada servicio, clasificados por `CompanyKqlTemplater` (servicio → flujo → paso, first-match-wins), entidad por `entityIdentitySources`, agrupados por `operation_Id` (journey = una operación; si el perfil define sesión, por `session_Id`). Estado: `Completed` si llegó al último paso del flujo, `Dropped at step N` si no, `Failed` si algún request falló.
- **Sync** (timer existente de journeys o uno nuevo `ProfileJourneySync`, cada 5 min, ventana configurable) escribe `monitoring.ProfileJourney(ProfileKey, JourneyId, ServiceKey, FlowKey, EntityKey, StartedAt, EndedAt, DurationMs, Status, LastStepOrder, LastStepKey, StepCount, Failed bit, SampleOperationId)` y `ProfileJourneyStep(ProfileKey, JourneyId, StepOrder, StepKey, Name, Timestamp, DurationMs, Success, ResultCode)`; retención 8 días; tope por corrida (default 500 journeys/servicio).
- API: `GET /api/profile-journeys?profileKey=&service=&entity=&status=&win=&top=` → `{ profileKey, labels:{service,flow,step,entity}, journeys:[…] }` y `GET /api/profile-journeys/{journeyId}?profileKey=` → journey + pasos.
### 4.2 Entidades
`GET /api/entities?profileKey=&service=&win=&top=` → `{ entityLabel, entities:[{ key, requests, failures }] }` (top por volumen, desde telemetría con `entityIdentitySources`, caché 5 min). Reemplaza a `/api/clubs` en el filtro del header.
### 4.3 Excepciones por perfil
Ingesta de excepciones: para **cada perfil habilitado**, consulta `exceptions` de los alias de sus servicios (KQL vía templater, no `incident-exceptions.txt` fijo para no-incumbentes), clasifica servicio/paso/entidad y persiste con `ProfileKey`. `GET /api/exceptions?profileKey=` filtra.
### 4.4 Analytics por perfil
Sync de dashboard por perfil: P95 por paso y excepciones por paso para cada servicio del perfil → `dashboard.MetricPoint` con `ProfileKey`. `GET /api/analytics/metrics?profileKey=` filtra; la respuesta incluye la lista de servicios/pasos del perfil para que la UI arme los gráficos sin nombres fijos.
### 4.5 Overview por perfil
`GET /api/overview?profileKey=` compone solo con datos del perfil: estado de pilares, KPIs del perfil, incidentes/excepciones del perfil, investigaciones de Triage del perfil, top entidades afectadas. Los widgets solo-incumbente (Fabric hotel business health, inventario de clubs) se omiten para no-incumbentes (`widgets` en la respuesta dice cuáles aplican).

## §5 Lecturas existentes que deben respetar `profileKey`
`/api/health/pillars`, `/api/health/kpis/{id}/buckets`, `/api/triage/investigations[/{id}]`, `/api/monkeys`, `/api/scouts/*`, `/api/scout/packages/*`, `/api/inspector/queue|toasts|cases/*`, `/api/findings/*`, `/api/knowledge/documents|search|document`, `/api/daemons/activity` (entradas sin perfil = globales, se muestran siempre), `/api/club-inventory` (solo incumbente; otro perfil → vacío + `applicable:false`), `/api/hotels/business-health` (solo incumbente). El Web proxy reenvía el query string en **todas** estas rutas (hoy `/api/club-inventory` no lo hace).

## §6 UI (app.js / index.html / styles.css)
- `withActiveCompany()` en **todas** las lecturas de §4–§5 y de Settings que dependan de la compañía (KPIs, telemetry sources, KQL "used by").
- Al cambiar de compañía: aplicar tema, invalidar estado/cachés en memoria, reconstruir filtros y **recargar todas las pestañas** (la visible inmediatamente; las demás al abrirse).
- Filtros del header desde el perfil: "Domain" → servicios del perfil con su `labels.service`; "Club" → `labels.entity` con valores de `/api/entities`. Ningún `<option>` fijo hotels/cruises/cars en el HTML.
- Customer Journeys usa §4.1 para **todas** las compañías (incluida Arrivia); columnas con los labels del perfil; texto "End-to-end transaction explorer across …" con los servicios del perfil.
- Analytics, Overview y Exceptions usan §4.3–4.5; widgets no aplicables se ocultan con una nota.
- Textos con vocabulario Arrivia en la UI salen de `labels`/`glossary` del perfil.

## §7 Tareas (ownership en `tasks.json`)
| Tarea | Entrega |
|---|---|
| **MC0** | Contrato en código: `CompanyTheme` + validación; SQL §3; interfaces/DTOs de §4 en Core (`IProfileJourneyStore`, `IProfileJourneyReader`, `IEntityCatalog`, records de respuesta); extensiones DI vacías por tarea (`AddProfileJourneys`, `AddProfileExceptions`, `AddProfileAnalytics`, `AddProfileOverview`) llamadas desde Functions `Program.cs`; rutas proxy nuevas en Web `Program.cs` (forward con query string) incl. arreglo de `/api/club-inventory`; tests de contrato. |
| **MC1** | §4.1 + §4.2 (sync, store, reader, endpoints). |
| **MC2** | §4.3 (ingesta y lectura de excepciones por perfil). |
| **MC3** | §4.4 + §4.5 (analytics y overview por perfil). |
| **MC4** | §5 (todas las lecturas existentes respetan `profileKey`, con test de aislamiento por endpoint). |
| **MC5** | §6 UI completa + tema §2. |
| **MC6** | Temas en los 4 perfiles (packs `profile.json` + perfiles en `platformDB`), E2E Playwright `Portal/tests/e2e/company-switch.ps1`: para cada compañía cambia, verifica tema (color del header) y que cada pestaña muestre solo datos de esa compañía (sin "Hotels"/"T-Mobile" en compañías no turismo). |
| **MCRV** | Revisión final. |

## Enmiendas
(ninguna)
