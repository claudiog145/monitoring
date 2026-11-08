---
name: spec1000-mt4-repo-cleanup
description: "Tarea MT4 (rama company-profile): sacar de git los artefactos de build Portal/publish/ y mover los .md de sesión de la raíz a docs/company-profile/."
agent: spec1000-implementer
argument-hint: "TASK=MT4 WORKTREE=<ruta> ITERATION=<n>"
---
# MT4 · Limpieza del repositorio

**Rama de integración:** `feature/company-profile-decoupling`. **Prohibido `git push` y `gh pr create`.** No toques código C#/JS/SQL.

## Qué hacer

1. **Artefactos de build:**
   - `git rm -r --cached Portal/publish`. Los archivos quedan en disco; no los borres del disco.
   - Agrega `Portal/publish/` a `.gitignore` en la sección de build outputs, o al final con un comentario.
   - Verifica que ningún script, Dockerfile ni workflow **lea** archivos versionados de `Portal/publish/` (`git grep -n "publish/" -- Portal/*.ps1 Portal/scripts Portal/docker* .github/workflows Portal/Dockerfile*`). Los scripts que **escriben** ahí siguen funcionando. Si alguno depende de que esté versionado, para y repórtalo.
2. **Documentos de sesión en la raíz:**
   - Mueve con `git mv` a `docs/company-profile/` estos archivos: `00_START_HERE.md`, `APPINSIGHTS_*.md`, `ARRIVIA_TOKEN_TEST_RESULTS.md`, `AUTH_OPTIONS_SUMMARY.md`, `BRANCH_DOCUMENTATION_INDEX.md`, `EXECUTIVE_SUMMARY.md`, `GENERATING_TEST_DATA.md`, `IMPLEMENTATION_STATUS.md`, `MULTI_COMPANY_ANALYSIS.md`, `MULTI_TENANT_AUTH_GUIDE.md`, `QUICK_REFERENCE.md`, `RUN_LOCAL.md`, `SCOUT_READINESS_CHECKLIST.md`, `SESSION_FINAL_SUMMARY_2026-10-30.md`, `TESTING_MULTI_COMPANY.md`.
   - Mueve también `Portal/QUICK_START_TOKENS.md`, `Portal/SCOUTS_IMPLEMENTATION_STATUS.md` y `Portal/SCOUT_IMPLEMENTATION_COMPLETE.md`.
   - Antes de mover, revisa si `docs/` está en `.gitignore` (en `main` lo está). Si lo está, agrega la excepción `!docs/company-profile/` y `!docs/company-profile/**` para que los archivos sigan versionados. El objetivo es que no se pierda nada del historial.
   - Corrige los links relativos rotos dentro de los documentos movidos (`BRANCH_DOCUMENTATION_INDEX.md` enlaza a `Portal/...`).
   - Crea `docs/company-profile/README.md`: un índice corto en español con un párrafo que dice que estos documentos son notas de sesión de la rama y no fuente de verdad, más la lista de archivos.
3. **No muevas:** `README.md`, `AGENTS.md`, `CLAUDE.md`, `openwiki/**`, `.github/**`, ni nada que ya esté en `docs/`.

## Tests requeridos

Ninguno nuevo. La suite completa sigue verde y sin cambios en el conteo.

## Criterios de aceptación

1. `git ls-files Portal/publish` no devuelve nada. Los archivos siguen en disco.
2. `git ls-files '*.md'` en la raíz solo muestra los archivos que ya estaban antes de la rama: compáralo con `git ls-tree --name-only 01169e72 -- '*.md'`.
3. `git ls-files docs/company-profile` lista todos los movidos más el README.
4. Gate `-Task MT4 -Mode Check` da PASS.
