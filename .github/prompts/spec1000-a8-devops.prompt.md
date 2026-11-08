---
name: spec1000-a8-devops
description: "Spec 1000 tarea A8: Functions QA siempre activo (minReplicas >= 1), settings de Spec 1000 en QA y compose, workflow de producción deshabilitado y documentación de publicación."
agent: spec1000-implementer
argument-hint: "TASK=A8 WORKTREE=<ruta> ITERATION=<n>"
---
# A8 · DevOps: siempre activo, settings y workflow de producción

**Objetivo:** que los timers corran siempre en QA (sin escalar a cero), que todos los ambientes tengan las settings nuevas y dejar preparado (apagado) el despliegue a producción.

## Archivos que te pertenecen

`Portal/docker-compose.yml`, `Portal/.env.example`, `Portal/scripts/publish-func-qa.ps1`, `Portal/scripts/publish-func-qa-test.ps1`, `Portal/PUBLISHING.md`, `.github/workflows/deploy-qa.yml`, `.github/workflows/deploy-prod.yml`.

## Pasos

1. **`publish-func-qa.ps1`** (y `publish-func-qa-test.ps1` si actualiza la misma Container App): en el `az containerapp update` de Functions agregá `--min-replicas 1` (y `--max-replicas` con el valor actual o 1 si no hay). Agregá las settings `spec1000_ingestion_cron`, `spec1000_monitor_cron`, `HealthMonitoring__Enabled` (default `false`), `Triage__Mode` (default `Shadow`) con `--set-env-vars`, sin pisar las existentes.
2. **`deploy-qa.yml`**: el paso que actualiza la Container App de Functions también fija `--min-replicas 1` y las mismas settings.
3. **`deploy-prod.yml`** (nuevo): copia de la estructura de QA para `platform-reliability-ca-func-prd` / `platform-reliability-ca-wa-prd` (nombres a confirmar, dejalos como `env:` al inicio con comentario `# TODO confirmar`), **solo** `workflow_dispatch`, `environment: production` (aprobación manual), y `if: ${{ vars.SPEC1000_PROD_ENABLED == 'true' }}` en el job para que no corra hasta habilitarlo.
4. **`docker-compose.yml`**: no dupliques lo que agregó I1. Solo agregá comentarios explicando `HealthMonitoring__Enabled`/`Triage__Mode` y cómo activarlos desde `.env`.
5. **`.env.example`**: `HealthMonitoring__Enabled=false`, `Triage__Mode=Shadow` con comentario.
6. **`PUBLISHING.md`**: sección "Spec 1000 — siempre activo" (por qué `minReplicas ≥ 1`, cómo verificar con `az containerapp show --query properties.template.scale`, cómo activar modo sombra en QA, cómo habilitar prod).
7. Nunca pongas secretos ni valores reales de credenciales.

## Verificación

- `run-gate.ps1 -Task A8 -Mode Check -IncludeE2E` (nivel `timers`: schema + API + detección + los timers `KpiIngestion` y `ApplicationHealthMonitor` corren solos en Docker; los chequeos del portal los hace A7 y RV).
- Validá la sintaxis YAML de los workflows (p. ej. `python -c "import yaml,sys;yaml.safe_load(open(sys.argv[1]))" <archivo>` o el linter que haya; si no hay herramienta, revisá indentación a mano y decilo en la entrega).

## Criterios de aceptación

1. QA Functions con `--min-replicas 1` en script y workflow (check estático `minReplicas`).
2. Settings de Spec 1000 presentes en QA, compose y `.env.example`, con defaults seguros (`Enabled=false`, `Shadow`).
3. `deploy-prod.yml` no puede ejecutarse sin variable de habilitación y aprobación de ambiente.
4. E2E `timers` PASS (timers vivos sin portal).
5. Sin secretos.
