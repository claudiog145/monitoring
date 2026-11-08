---
name: spec1000-mt2-kpi-profilekey
description: "Tarea MT2 (rama company-profile): contratos v3 — ProfileKey en KpiDefinition, TriageSignal y TriageInvestigation; daemons procesan todos los perfiles; API y portal filtran por la compañía activa."
agent: spec1000-implementer
argument-hint: "TASK=MT2 WORKTREE=<ruta> ITERATION=<n>"
---
# MT2 · ProfileKey en KPIs y Triage (contratos v3)

**Rama de integración:** `feature/company-profile-decoupling`. **Prohibido `git push` y `gh pr create`.**

**Contrato:** `.github/spec1000/contracts-company.md` §1. Esta tarea **es** el cambio de contrato v2 → v3 (`contractsFrozen: false`). No cambies nada fuera de §1.

## Contexto

La rama agregó perfiles de compañía (`monitoring.CompanyProfile`, puntero `monitoring.ActiveCompanyProfile`, `IActiveCompanyProfileStore`, `ICompanyProfileStore`, `Company:DefaultProfileKey`). Los KPIs de Spec 1000 no conocían el perfil. El usuario decidió agregar `ProfileKey` ahora.

## Archivos que te pertenecen

Ver `tasks.json` → `MT2`.

## Diseño (obligatorio, ver §1)

1. **Dominio:**
   - `KpiDefinition.ProfileKey` es una propiedad `init` con default `KpiProfiles.Default` (const en `Core/Company/ArriviaKpiProfile.cs`). `Validate()` exige que no esté en blanco.
   - `TriageSignal` recibe el último parámetro opcional `string? ProfileKey = null`.
2. **SQL idempotente:**
   - KpiDefinition: columna, unique `(ProfileKey, Pillar, Kpi, Domain, Stage)`, procedimientos y audit.
   - TriageInvestigation: columna, índice e insert.
   - Seed con `arrivia`.
   - Prueba los scripts dos veces seguidas contra la base local (`127.0.0.1,1433`, `sa` / `Jm_LocalDev_1!`, `platformDB`), que ya tiene datos: no deben fallar ni duplicar filas.
3. **Repositorios** (`Sql*` e `InMemory*`): leen y escriben `ProfileKey`. Las lecturas reciben `string? profileKey = null` según §1.4 (`null` = todos los perfiles).
4. **Daemons:**
   - `KpiIngestionService` y `PillarHealthMonitorService` siguen procesando **todos** los perfiles.
   - Las señales del monitor llevan el `ProfileKey` de la definición.
   - El Triage Monkey, al crear un ForensicCase / InvestigationPackage (que en esta rama exigen `ProfileKey`), usa `signal.ProfileKey ?? KpiProfiles.Default`.
   - Una fuente sin App Insights para un perfil no incumbente escribe el Warning de §1.7.
5. **API (§1.5):**
   - Por defecto se usa el perfil activo: resuelve con `IActiveCompanyProfileStore` y, si no hay, con `Company:DefaultProfileKey` (`ProfileDrivenCompanyOptions`).
   - `?profileKey=*` devuelve todos.
   - El POST valida que el perfil exista.
6. **Portal** (`app.js` / `index.html` donde corresponda):
   - Platform Health, KPI settings y la lista de triage recargan al cambiar el selector de compañía del header.
   - Busca el evento o la función que usa ese selector en el código de la rama (`wireCompanySelector`, `loadCompanyProfiles`).
   - Daemons no cambia.
7. **HealthConsole** (CLI): `status`, `watch` y `kpis` aceptan `--profile <key>`. Sin la opción, muestran todos los perfiles con la columna `Profile`.

## Tests requeridos (nombres exactos, archivos nuevos en `Portal/tests/JourneyMonitor.Tests/Monitoring/Kpi/KpiProfileTests.cs` y `.../Functions/KpiProfileApiTests.cs`)

| Test | Aserción |
|---|---|
| `KpiDefinition_DefaultProfileKey_IsIncumbent` | definición sin init → `ProfileKey == KpiProfiles.Default` |
| `KpiDefinition_BlankProfileKey_FailsValidation` | |
| `KpiRepository_ListByProfile_FiltersAndNullReturnsAll` | in-memory: dos perfiles |
| `KpiRepository_SameKeyDifferentProfiles_AreDistinct` | mismo Pillar/Kpi/Domain/Stage en dos perfiles coexiste |
| `Monitor_SignalCarriesDefinitionProfileKey` | anomalía de KPI del perfil `acme` → señal con `ProfileKey = "acme"` |
| `Ingestion_ProcessesAllProfiles` | KPIs habilitados de dos perfiles → ambos ingeridos |
| `Ingestion_NoSourceForProfile_WarnsAndSkips` | texto exacto §1.7 |
| `Triage_ForensicCaseUsesSignalProfileKey` | |
| `TriageSignal_OldJsonWithoutProfileKey_Deserializes` | |
| `SettingsApi_DefaultsToActiveProfile` | GET sin query → solo el perfil activo |
| `SettingsApi_UnknownProfileKey_Returns400` | |
| `PillarsApi_FiltersByActiveProfile` | |
| `TriageApi_ManualRunDefaultsToActiveProfile` | |

Los tests de contrato existentes (`KpiContractTests`, `TriageContractTests`) se actualizan para v3: agrega las aserciones nuevas, no borres las viejas que siguen valiendo.

## Criterios de aceptación

1. Gate `-Task MT2 -Mode Check -IncludeE2E` da PASS (E2E `detection`).
2. En el stack local: `GET /api/settings/kpis` devuelve los 25 KPIs con `profileKey: "arrivia"`; con `?profileKey=ice` devuelve `[]` (o 400 si `ice` no existe como perfil); `GET /api/health/pillars?domain=hotels` sigue mostrando Latency y Exceptions de Hotels.
3. Scripts SQL idempotentes, verificado ejecutándolos dos veces.
4. KPI 1 Latency/hotels sigue en BaselinePercent `{"lookbackDays":7,"thresholdPercent":50}`. Si lo cambias para probar, restáuralo con `POST /api/settings/kpis` (header `X-Settings-Write-Secret: Jm_LocalDev_SettingsSecret_1!`), nunca directo en SQL.
