---
name: spec1000-d2-daemons-tab
description: "Spec 1000 tarea D2: tab Daemons en el portal con tarjetas de estado por daemon y una consola en vivo (polling de /api/daemons/activity) con filtros, pausa, auto-scroll, pantalla completa, detalle por línea y Run now."
agent: spec1000-implementer
argument-hint: "TASK=D2 WORKTREE=<ruta> ITERATION=<n>"
---
# D2 · Tab "Daemons" con consola en vivo

**Objetivo:** que un operador abra el portal y vea **qué están haciendo los daemons ahora**, como una consola, sin depender de logs del host.

**Contrato:** [contracts-daemons.md](../spec1000/contracts-daemons.md) §5 (API, kinds, mensajes). Formato JSON: camelCase, enums en camelCase (`level: "info" | "warning" | "error"`), igual que el resto de Spec 1000 — verificalo leyendo `DaemonFunctions.cs`.

## Archivos que te pertenecen

`Portal/src/JourneyMonitor.Web/wwwroot/**`, `Portal/src/JourneyMonitor.Web/Program.cs`, `Portal/src/JourneyMonitor.Web/Services/JourneyMonitorApiClient.cs`.

## Pasos

1. **Proxies Web** (mismo patrón que `/api/health/*`): `GET /api/daemons` y `GET /api/daemons/activity` (pasá el query string tal cual). "Run now" usa el proxy existente `POST /api/monkeys/{id}/run` (el servidor ya agrega el secreto); para Triage Monkey el id es `inspector-monkey`.
2. **Tab** en `index.html`: botón `id="tab-btn-daemons" data-tab="daemons" aria-controls="tab-daemons"` después de "Simian Army", y el panel `id="tab-daemons"` con la misma estructura/accesibilidad que los demás tabs.
3. **Tarjetas de estado** (fila superior): una por daemon de `GET /api/daemons`, en orden: nombre, badge de salud (`ok` verde, `running` azul animado, `stale` ámbar, `error` rojo, `disabled` gris, `never-run` gris), schedule, modo (Triage), "último run hace X" + resumen, último error (si hay, con hora), botón **Run now** (deshabilitado si `disabled`; feedback de resultado). Refresco cada 15 s mientras el tab está visible.
4. **Consola** (componente principal):
   - Panel oscuro, fuente monoespaciada, una línea por entrada: `HH:mm:ss  LEVEL  daemon  kind  mensaje` (hora local del navegador; nivel con color; nombre corto del daemon con color propio por daemon).
   - **Polling incremental** cada 3 s con `afterId = último id recibido` (primera carga: `afterId=0&top=200`), solo mientras el tab está visible y la consola no está en pausa. Nunca duplicar líneas. Mantener como máximo 2.000 líneas (descartar las más viejas).
   - **Barra de controles:** filtro por daemon (todos / cada uno), nivel mínimo (Info/Warning/Error), búsqueda de texto (filtra lo ya cargado), **Pausa/Reanudar**, **Limpiar** (solo la vista), **Auto-scroll** (on por defecto; se desactiva solo si el usuario sube manualmente), **Pantalla completa** (el panel ocupa toda la ventana; `Esc` sale), contador de líneas y estado de conexión ("en vivo", "pausado", "error al consultar" con reintento).
   - Cambiar el filtro de daemon o de nivel **recarga** desde el servidor con esos filtros (`daemon=`, `level=`).
   - **Click en una línea** la expande y muestra `runId` y `dataJson` formateado; un botón "filtrar por este run" muestra solo las líneas de ese `runId`.
   - Cuando no hay actividad, mostrar un mensaje claro: si todos los daemons están `disabled`, "Los daemons están apagados (HealthMonitoring:Enabled=false)".
5. **Seguridad/XSS:** todo texto del backend se inserta escapado (usá el helper de escape existente o `textContent`). Ningún secreto en el navegador.
6. **No es detección:** la consola solo muestra; no calcula anomalías ni estadísticas.
7. Estilo coherente con el portal (variables y clases CSS existentes); sin librerías nuevas.

## Verificación

- `node --check` de los `.js` tocados.
- `run-gate.ps1 -Task D2 -Mode Check -IncludeE2E` (nivel `daemons-web`: incluye el flujo de detección, los proxies y la presencia del tab).
- Con el stack arriba, describí en tu entrega lo que muestra el tab tras el E2E (tarjetas y líneas de consola).

## Criterios de aceptación

1. Tab Daemons visible con tarjetas de los 5 daemons y su salud.
2. Consola en vivo incremental, sin duplicados, con filtros, búsqueda, pausa, limpiar, auto-scroll, pantalla completa, detalle por línea y filtro por run.
3. Sin llamadas al backend de daemons con el tab oculto.
4. Run now funciona vía el proxy existente.
5. Texto escapado; sin secretos en el navegador.
6. `node --check` y E2E `daemons-web` PASS; las demás pestañas no cambian.
