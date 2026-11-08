---
name: spec1000-rv-revision
description: "Spec 1000 tarea RV: revisión final de toda la rama feature/spec-1000 contra el plan y los contratos, E2E completo y redacción del informe para el PR."
agent: spec1000-implementer
argument-hint: "TASK=RV WORKTREE=<ruta> ITERATION=<n>"
---
# RV · Revisión final y E2E completo

**Objetivo:** demostrar con evidencia que la Fase 1 cumple el spec y dejar el informe que acompaña al PR. **No** corregís código: si encontrás un problema, lo documentás para que el orquestador reabra la tarea dueña.

## Archivo que te pertenece

`docs/architecture/spec-1000-informe-revision-<YYYY-MM-DD>.md` (fecha de hoy).

## Pasos

1. `git log --oneline main..HEAD` y `git diff --stat main...HEAD`: inventario de lo entregado por tarea.
2. Recorré cada fila de §2 "Estado actual" de `docs/architecture/spec-1000-propuesta-arquitectura-2026-11-05.md` y registrá el estado nuevo con evidencia (archivo, test o E2E).
3. Verificá el flujo end-to-end del spec (§11 de Specification 1000) paso por paso con el stack Docker:
   `run-gate.ps1 -Task RV -Mode Check -IncludeE2E` (nivel `full`).
4. Revisá los veredictos de todas las tareas (`../ai-reliable-platform-wt/<id>/.spec1000/verdicts/*.json`): iteraciones usadas y hallazgos Low pendientes.
5. Escribí el informe con estas secciones exactas:
   - **Resumen** (5 líneas máximo).
   - **Cumplimiento del spec**: tabla requisito → estado (Cumple/Parcial/No) → evidencia.
   - **Tareas**: tabla tarea → iteraciones → SHA → tests agregados.
   - **E2E**: resultado de cada nivel con fecha.
   - **Pendientes y riesgos**: decisiones abiertas (Red Alert, fuentes de Business para Cars/Cruises/Air, acceso Air, KPIs de infraestructura), hallazgos Low, deuda conocida.
   - **Cómo activar en QA**: pasos de modo sombra.
6. Si encontrás un problema que impide afirmar "Cumple" en un requisito de Fase 1 → además del informe, `SPEC1000-BLOCKED.md` con el problema, la tarea dueña y la evidencia.

## Criterios de aceptación

1. El informe existe con las 6 secciones y cada afirmación tiene evidencia verificable.
2. E2E `full` PASS en el HEAD revisado.
3. Checks estáticos `noBrowserDetection`, `nodeSyntax` y `minReplicas` PASS.
4. Ningún archivo de código modificado.
