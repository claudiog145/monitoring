---
name: spec1000-mc-multi-company-ui
description: "Fase MC (rama company-profile, local): cambiar de compañía cambia todo el portal (datos y tema). Una sección por tarea MC0–MC6."
agent: spec1000-implementer
argument-hint: "TASK=MC<n> WORKTREE=<ruta> ITERATION=<n>"
---
# Fase MC · Multi-company UI

**Reglas (todas las tareas):**
- Prohibido `git push`, `gh pr create`. Commits solo en el branch del worktree.
- Contrato congelado: `.github/spec1000/contracts-multi-company-ui.md` (v6). Léelo completo. Si algo es imposible, documenta en `.spec1000/findings/<TASK>-blocker.md` y termina.
- Mapa de brechas (hechos con archivo:línea): `C:\Arrivia\ai-reliable-platform\docs\architecture\mc-gap-map-2026-11-07.md` (fuera del worktree).
- Ownership/tests/E2E: `.github/spec1000/tasks.json`. Solo tocas `allowedPaths`.
- Reglas de capas del gate: Core puro; Functions solo valida/despacha/mapea (sin SQL/KQL); Web sin SQL ni telemetría.
- Sin warnings nuevos, sin bajar la cuenta de tests.
- La app real (`jm-web :5080`, `jm-functions :8081`) lee del Azure falso (`jm-fake-appinsights :8086`, Kustainer `:8085`) con 4 compañías cargadas en `platformDB`: `arrivia`, `banco`, `kinectify`, `mercadolibre`. **No borres datos de Kustainer ni hagas reset del generador** (`:8087`). Puedes consultar el falso con `X-Api-Key: jm-demo-key` para validar KQL.
- Al terminar: gate `powershell -NoProfile -ExecutionPolicy Bypass -File .github/spec1000/run-gate.ps1 -Task <TASK> -Mode Check -IncludeE2E` verde, commit, `status: "ready-for-test"`. Respuesta final corta.

## MC0 · Contrato en código (va primero, solo)
Solo tipos, SQL, interfaces, DI vacía, rutas proxy y tests de contrato (§2, §3, §7-MC0). Sin lógica de negocio. Los registros DI vacíos (`AddProfileJourneys`, `AddProfileExceptions`, `AddProfileAnalytics`, `AddProfileOverview`) viven cada uno en su propio archivo en `Infrastructure/DependencyInjection/` para que MC1–MC3 no choquen. Rutas proxy nuevas en Web (`/api/profile-journeys`, `/api/profile-journeys/{id}`, `/api/entities`) reenvían el query string; arregla `/api/club-inventory` para que también lo reenvíe. E2E `schema`.

## MC1 · Journeys genéricos y entidades (§4.1, §4.2)
Sync por perfil (todas las compañías habilitadas), store SQL, reader, endpoints Functions. Usa `CompanyKqlTemplater` para clasificar; la KQL usa `app('{{SOURCE:alias}}')` resuelto por el catálogo de fuentes (respeta la pertenencia de alias por perfil). Verifica contra el Azure falso que las 4 compañías producen journeys con estados variados. E2E `portal`.

## MC2 · Excepciones por perfil (§4.3)
Ingesta por perfil desde los alias de cada servicio; dedupe incluye `ProfileKey`; lectura filtrada. El incumbente sigue funcionando. Verifica contra el falso que cada compañía tiene excepciones propias y ninguna ajena. E2E `portal`.

## MC3 · Analytics y Overview por perfil (§4.4, §4.5)
Sync de métricas por perfil y paso; `GET /api/analytics/metrics?profileKey=` incluye servicios/pasos del perfil; overview compuesto solo con datos del perfil y `widgets` aplicables. E2E `portal`.

## MC4 · Lecturas existentes respetan profileKey (§5)
Para cada endpoint de §5: acepta `profileKey` (o usa el activo), filtra, y tiene un test de aislamiento (fila de A no aparece pidiendo B). E2E `portal`.

## MC5 · UI completa + tema (§2 UI, §6)
`app.js`, `index.html`, `styles.css`. Tema con CSS custom properties; `withActiveCompany` en todas las lecturas; recarga total al cambiar; filtros desde el perfil; Customer Journeys sobre §4.1; Analytics/Overview/Exceptions sobre §4.3–4.5; ningún vocabulario fijo de Arrivia en el HTML. Verifica en el navegador (`http://localhost:5080`, después de reconstruir `web`) cambiando entre las 4 compañías. E2E `portal`.

## MC6 · Temas de las 4 compañías + E2E de cambio de compañía
- Agrega `theme` a `Portal/demo/packs/*/profile.json` (colores distintivos por empresa, nombre corto) y aplícalo a los perfiles en `platformDB` vía `PUT /api/settings/company-profiles/{key}` (secreto local).
- `Portal/tests/e2e/company-switch.ps1` (PowerShell 5.1 + Playwright vía `npx playwright` o Node): para cada compañía selecciona en el header, verifica el color del header = `theme.primaryColor`, y que Overview, Customer Journeys, Platform Health, Analytics, Exceptions, Daemons y Memory Bank no muestren datos de otra compañía (p.ej. ningún "Hotels"/"T-Mobile"/"Club" para banco/kinectify/mercadolibre). Sale 0 solo si todo pasa.
- E2E `portal` + correr `company-switch.ps1`.

## MCRV · Revisión final
Diff completo vs contrato v6; build, tests, E2E `full` y `company-switch.ps1`; verificación manual en el navegador de las 4 compañías; informe `docs/architecture/mc-informe-revision-2026-11-07.md`.

## GS1 · Sesiones en el generador y journeys de varios pasos
Hoy cada request generado tiene su propio `operation_Id` y `session_Id` vacío o único, así que todo journey genérico (MC1) tiene un solo paso.
- **DemoGenerator:** asigna `session_Id` determinístico de forma que los requests de un mismo usuario/entidad recorran los pasos de un flujo **en orden** con abandono natural (las proporciones por paso del archetype definen el embudo). **Prohibido** cambiar el número de requests, timestamps por minuto, duraciones, `success`, `resultCode`, entidad o eventos de negocio: las series de KPIs deben quedar **idénticas** byte a byte (los 4 paquetes ya están calibrados). Hacerlo en el modelo de referencia C# y en la KQL a la vez. Test `Sessions_DoNotChangeKpiSeries` compara las series antes/después.
- **MC1 (journeys):** si las filas traen `session_Id`, el journey se agrupa por sesión (pasos ordenados por timestamp y por `order` del paso); si no, sigue por `operation_Id`.
- No resetees el generador ni borres datos de Kustainer: los datos nuevos del feeder ya traen sesiones; documenta en el README del generador cómo regenerar historia con sesiones cuando el usuario lo pida.

## MC7 · Seguimientos de MC5 (antes de MC6)
- **Actividad de daemons por compañía:** las entradas que los daemons escriben para una KPI/perfil concreto deben llevar el `ProfileKey` de ese perfil (KPI ingestion, monitores de pilares, Triage, Scouts). Solo las entradas realmente globales (inicio/fin de corrida sin perfil, token-refresh) quedan sin perfil. `/api/daemons/activity?profileKey=X` nunca devuelve entradas de otro perfil. Las tarjetas de `/api/daemons` muestran, si se pide `profileKey`, el último resumen/error del perfil o un texto neutro sin vocabulario de otra compañía.
- **Simian Army:** `GetMonkeysFunction` y `data/monkeys.json` no usan textos fijos de Arrivia ("across Hotels, Cars, Cruise"): usan los servicios y labels del perfil activo.
- **Limpieza de labels:** no mezclar idiomas en encabezados ("Paso count" → `labels.step` + "count" coherente); los hints de Settings usan ejemplos neutros.
- Tests requeridos en tasks.json; E2E `portal`.
## FXM1–FXM3 · Correcciones de la revisión final (MCRV)
Fuente: `docs/architecture/mc-informe-revision-2026-11-08.md` (en `C:\Arrivia\ai-reliable-platform\docs\architecture\`). Cada tarea corrige SOLO sus hallazgos y prueba en vivo en `http://localhost:5080` (con arrivia activa mientras corre el E2E del gate; restaurar la compañía activa al final). No resetear el generador ni borrar datos de Kustainer. Con el cambio de reglas de journeys (FXM2), las series de KPIs deben seguir idénticas (`Sessions_DoNotChangeKpiSeries`).
- **FXM1:** H1 (Doctor guarda findings bajo la compañía del finding, no la activa; conocimiento legacy sin perfil = incumbente, no compartido; script idempotente de re-etiquetado propuesto en el informe §4.2, ejecutable solo sobre `monitoring.*` de conocimiento/findings) + M1 (todas las rutas proxy de lectura dependientes de compañía reenvían el query string) + L2.
- **FXM2:** M2 (más journeys de varios pasos: que una sesión recorra varios pasos con probabilidad realista; un journey de un solo paso no es "Completed" salvo que el flujo tenga un paso; Overview no infla completion) + M3 (latencia promedio = duración de requests, no duración de sesión) + L1.
- **FXM3:** M4 (el pipeline legacy de Arrivia dejó de recibir datos ~02:30 UTC: diagnosticar con evidencia — logs de jm-functions, consultas al falso — y corregir; si la causa está fuera de tus archivos, documentar en `.spec1000/findings/FXM3-blocker.md`).