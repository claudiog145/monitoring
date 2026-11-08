---
name: spec1000-d3-console-watch
description: "Spec 1000 tarea D3: comandos status y watch en JourneyMonitor.HealthConsole para ver el estado y seguir en vivo la actividad de los daemons desde la terminal."
agent: spec1000-implementer
argument-hint: "TASK=D3 WORKTREE=<ruta> ITERATION=<n>"
---
# D3 · `status` y `watch` en la consola

**Objetivo:** la misma vista del tab Daemons, desde la terminal (útil contra QA o cuando el portal no está disponible). Solo lectura.

**Contrato:** [contracts-daemons.md](../spec1000/contracts-daemons.md) (`IDaemonActivityLog`, `IDaemonStatusReader`).

## Archivos que te pertenecen

`Portal/src/JourneyMonitor.HealthConsole/**` y `Portal/tests/JourneyMonitor.Tests/HealthConsole/**`.

## Comandos

| Comando | Comportamiento | Exit |
|---|---|---|
| `status` | Una línea por daemon: `{DisplayName,-30} {Health,-9} last {HH:mm:ss UTC o "never"}  {LastRunSummary}` y, si hay, `  ! {LastErrorAt:HH:mm:ss} {LastErrorMessage}` | 0 |
| `watch [--daemon <key>] [--level info|warning|error] [--interval <s>] [--since <n>] [--max-polls <n>]` | Imprime las últimas `--since` (default 50) entradas y luego hace polling cada `--interval` (default 3, 1..60) segundos con `afterId`, imprimiendo solo entradas nuevas: `HH:mm:ss {LEVEL,-7} {daemon,-28} {kind,-24} {message}`. Ctrl+C termina limpio (exit 0). `--max-polls` (para tests/automatización) corta tras N polls | 0; argumentos inválidos → 1 |

- Usa la misma resolución de stores que `evaluate`/`replay` (SQL con `--connection` o configuración; si no hay, memoria con aviso).
- Nunca escribe (los decoradores de solo lectura existentes deben cubrir el nuevo store: si `IDaemonActivityLog.AppendAsync`/`PurgeOlderThanAsync` se llaman, lanzan).
- Actualizá el texto de ayuda (`--help`).
- Inyectá el reloj y el "sleep" (p. ej. un delegado) para que los tests no esperen tiempo real.

## Tests requeridos (`tests/HealthConsole/HealthConsoleDaemonTests.cs`)

- `Status_PrintsOneLinePerDaemon`
- `Watch_PrintsNewEntriesOnlyOnce` (entradas agregadas entre polls aparecen una sola vez)
- `Watch_FiltersByDaemonAndLevel`
- `Watch_StopsAfterMaxPolls`

## Criterios de aceptación

1. `status` y `watch` funcionan con el formato indicado y contra SQL real (probalo con `127.0.0.1,1433`).
2. Solo lectura garantizada.
3. Los 4 tests requeridos y los existentes de la consola en verde.
