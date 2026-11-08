---
name: spec1000-a7-portal
description: "Spec 1000 tarea A7: dejar el portal solo como presentación: quitar la detección del navegador, leer /api/health y /api/triage vía proxy, botón manual de Triage y editor de KPIs en Settings."
agent: spec1000-implementer
argument-hint: "TASK=A7 WORKTREE=<ruta> ITERATION=<n>"
---
# A7 · Portal solo lectura

**Objetivo:** que cerrar el portal no afecte nada y que el portal no calcule anomalías. Toda la información viene del backend.

## Archivos que te pertenecen

`Portal/src/JourneyMonitor.Web/wwwroot/**`, `Portal/src/JourneyMonitor.Web/Program.cs`, `Portal/src/JourneyMonitor.Web/Services/JourneyMonitorApiClient.cs`.

## Antes de empezar

- Leé en `app.js` la sección Reliability Watch (`RW_LATENCY_SAMPLE`, `rwLatencyMetric`, `getJourneyLatencyMetrics`, `rwLatencyBars`, `rwLatencyChart`, `rwSignalBars`) y en `index.html` el markup de Platform Health (pilares, tarjeta de Triage Monkey, texto "Run Triage Monkey investigation").
- Leé cómo `Web/Program.cs` mapea proxies (p. ej. `/api/findings`, `/api/settings/latency`) y cómo `JourneyMonitorApiClient` agrega `X-Settings-Write-Secret`, `X-Internal-Api-Secret` y antiforgery.

## Pasos

1. **API client + proxies** (mismo patrón exacto que los existentes):
   - GET: `/api/health/pillars?domain=`, `/api/health/anomalies?pillar=&domain=&top=`, `/api/health/kpis/{id}/buckets?hours=`, `/api/triage/investigations?top=`, `/api/triage/investigations/{id}`, `/api/settings/kpis`, `/api/settings/kpis/{id}/audit`.
   - POST `/api/triage/investigations`: el **servidor web** agrega `X-Internal-Api-Secret` (nunca el navegador); con Entra activo, valida antiforgery.
   - POST `/api/settings/kpis`: agrega `X-Settings-Write-Secret` y `changedBy` desde la identidad (igual que latency settings); antiforgery con Entra.
2. **Quitar la detección del navegador:** eliminá `RW_LATENCY_SAMPLE` y toda función que muestree journeys, calcule promedios/máximos/percentiles, marque spikes o tendencias para Reliability Watch. Ninguno de estos identificadores puede quedar: `RW_LATENCY_SAMPLE`, `getJourneyLatencyMetrics`, `rwLatencyMetric`.
3. **Pilares desde el backend:** las 3 tarjetas (Business, Application, Infrastructure) se renderizan con `GET /api/health/pillars` (status `ok`/`anomalous`/`offline`, KPIs, último bucket, contador "n de m", cooldown, última anomalía). El gráfico de cada KPI usa `GET /api/health/kpis/{id}/buckets` y dibuja el valor del backend; la línea de umbral/referencia se muestra solo si el backend la provee en la anomalía. Refresco cada 60 s mientras la pestaña está visible.
4. **Triage Monkey en la UI:** lista de investigaciones recientes (`/api/triage/investigations`) con estado, nivel de alerta, resumen y acciones recomendadas; detalle con las 6 dimensiones de `correlation` (marcando las no disponibles). El botón "Run Triage Monkey investigation" envía `POST /api/triage/investigations` con `pillar`, `kpi`, `detection` ("manual: <usuario o 'portal'>"), `domain` del filtro activo, y muestra `investigationId`/`duplicate`.
5. **Settings → KPIs:** tabla de `KpiDefinition` con edición de dirección, frecuencia, bucket, ventana, muestra, mínimo, método + parámetros, consecutivos, cooldown y habilitado; muestra errores 400 tal como los devuelve el backend; historial de auditoría.
6. Mantené el estilo visual existente (clases CSS, componentes); no agregues librerías.

## Verificación (antes del self-check)

- `node --check Portal/src/JourneyMonitor.Web/wwwroot/app.js` (y cualquier otro `.js` tocado).
- `run-gate.ps1 -Task A7 -Mode Check -IncludeE2E` (nivel `portal`: proxies 200 y `app.js` servido sin detección).
- Abrí `http://localhost:5080` con el stack arriba y verificá a mano los 3 pilares, la lista de investigaciones, el botón manual y Settings → KPIs. Describí lo que viste en tu entrega.

## Criterios de aceptación

1. Cero lógica de detección/estadística en `wwwroot` (check estático + revisión).
2. Los 3 pilares, investigaciones, botón manual y editor de KPIs funcionan contra el backend.
3. Los secretos los agrega el servidor web; el navegador nunca los ve.
4. Todos los `.js` pasan `node --check`; E2E `portal` PASS.
5. Ninguna otra pestaña del portal cambió de comportamiento.
