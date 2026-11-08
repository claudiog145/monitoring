---
name: spec1000-a6-consola
description: "Spec 1000 tarea A6: proyecto JourneyMonitor.HealthConsole para calibrar reglas KPI (simulate, evaluate, replay) sin efectos secundarios, con tests."
agent: spec1000-implementer
argument-hint: "TASK=A6 WORKTREE=<ruta> ITERATION=<n>"
---
# A6 · Consola de calibración

**Objetivo:** la consola que pidió Miguel para validar y ajustar el algoritmo heurístico **sin la UI y sin escribir nada**.

## Archivos que te pertenecen

`Portal/src/JourneyMonitor.HealthConsole/**`, `Portal/JourneyMonitor.slnx`, `Portal/tests/JourneyMonitor.Tests/JourneyMonitor.Tests.csproj` (solo agregar el `ProjectReference`) y `tests/HealthConsole/**`.

## Diseño

- Proyecto `net10.0` de consola `JourneyMonitor.HealthConsole` que referencia Core e Infrastructure; agregalo a `JourneyMonitor.slnx` siguiendo el formato existente.
- Lógica de comandos en una clase pública testeable: `public static Task<int> HealthConsoleApp.RunAsync(string[] args, TextWriter output, IServiceProvider? services = null)`. `Program.cs` solo la invoca.
- Usá `KpiEvaluator` directamente; para lecturas usá `AddKpiPersistence` con la configuración (`appsettings.json` opcional + env + `--connection`).
- **Nunca escribe**: envolvé los stores en decoradores de solo lectura que lanzan `InvalidOperationException` ante cualquier escritura.

## Comandos

| Comando | Comportamiento | Exit code |
|---|---|---|
| `simulate --scenario afternoon` | Corre en memoria la simulación de A1 (mismos datos que `AfternoonSimulation_MatchesPresentationTable`) e imprime la tabla | 0 |
| `evaluate --kpi <id> [--hours 48]` | Lee definición, estado y buckets reales y muestra qué haría el evaluador ahora (sin guardar) | 0; KPI inexistente → 2 |
| `replay --kpi <id> --from <iso> --to <iso> [--method <m>] [--param k=v ...] [--consecutive n] [--cooldown m]` | Re-evalúa buckets históricos con la definición real o con overrides; muestra tabla y resumen (triggers, suprimidos, saltados) | 0; KPI inexistente → 2 |
| sin args / `--help` / opción inválida | Uso | 1 (0 para `--help`) |

Formato de tabla (columnas separadas por ` | `): `Hora | Observado | Referencia | Desvío | Veredicto | Outcome | Contador`. Horas en UTC `HH:mm`, desvío como en `Detection` (`+85.0%`).

## Tests requeridos (`tests/HealthConsole/HealthConsoleTests.cs`)

- `Simulate_Afternoon_PrintsPresentationTable`: la salida contiene, en orden, las 7 filas con los outcomes `NoChange, CounterIncremented, CounterReset, CounterIncremented, Triggered, SuppressedByCooldown, CounterReset`.
- `Replay_DoesNotWriteToStores`: con stores en memoria espiados, `replay` no llama a ningún método de escritura.
- `Evaluate_UnknownKpi_ReturnsExitCode2`.
- `Args_InvalidOption_ReturnsExitCode1WithUsage`.

## Criterios de aceptación

1. El proyecto compila dentro de la solución y aparece en `JourneyMonitor.slnx`.
2. Ningún comando escribe en stores (verificado por test y por los decoradores).
3. `simulate` reproduce exactamente la tabla de la presentación.
4. 4 tests requeridos presentes y verdes.
