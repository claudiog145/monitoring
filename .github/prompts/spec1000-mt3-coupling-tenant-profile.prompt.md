---
name: spec1000-mt3-coupling-tenant-profile
description: "Tarea MT3 (rama company-profile): el mapa tenant → tenantKey de App Insights sale de TenantResolver hacia el CompanyProfile y se elimina la última deuda de coupling propia de la rama."
agent: spec1000-implementer
argument-hint: "TASK=MT3 WORKTREE=<ruta> ITERATION=<n>"
---
# MT3 · TenantResolver lee del perfil de compañía (CouplingInventoryTests)

**Rama de integración:** `feature/company-profile-decoupling`. **Prohibido `git push` y `gh pr create`.** Contratos v3 congelados (tag `spec1000-contracts-v3`).

## Problema

`Core/Monitoring/TenantResolver.cs` tiene hardcodeados:
- el mapa `"arrivia"`, `"ice"` y `"cruises"`, con placeholders `YOUR-…-AAD-TENANT-ID`;
- desde MT1, también la resolución tenant UUID → tenantKey.

La regla `CouplingInventoryTests` solo pasa por la línea `TenantResolver.cs|arrivia|OWNED BY MT3…` de `coupling-allowlist.txt`, que esta tarea debe **borrar**.

## Archivos que te pertenecen

Ver `tasks.json` → `MT3`.

## Diseño

1. **Datos en el perfil:**
   - Agrega a `CompanyProfile` (en `Core/Company/CompanyModel.cs`) una lista **opcional** de tenants de telemetría: `TelemetryTenants`, con elementos `{ TenantKey, AadTenantId, Domains[] }`.
   - Una lista vacía o null equivale a "sin mapa".
   - `CompanyProfileValidator`: TenantKey no vacío y único, AadTenantId con formato GUID.
2. **Seed** (`CreateMonitoringCompanyProfile.sql`, que es idempotente): el perfil `arrivia` declara:
   - `arrivia` con el tenant `60396ff5-e90f-4bc5-aced-a4f462f2636e`, dominios `hotels` y `cars`;
   - `cruises` con el mismo tenant, dominio `cruises`;
   - `ice` con el tenant `b0298d22-1db0-4550-8fa6-a116a5ab4208`, dominio `air`.

   Esos tenants son los de `docker-compose.yml` (`Insights__*__TenantId`). Si el JSON del seed tiene un test de forma (`ArriviaProfileSeedTests`), actualízalo.
3. **TenantResolver:**
   - Resuelve contra el perfil activo, o contra todos los perfiles si el activo no tiene el tenant.
   - Orden: tenant UUID + dominio → `TenantKey`. Si el UUID solo coincide con varios, se toma el primero por orden del perfil. Si nada coincide, devuelve el UUID.
   - No queda ningún literal de compañía en el archivo.
   - Lee los perfiles con `ICompanyProfileStore` y los cachea 60 s, como hacen otros consumidores de perfiles en la rama (revisa `CompanyAdapterProvider`).
4. **Allowlist:** borra la línea `TenantResolver.cs|arrivia|OWNED BY MT3…`. Si el test reporta otras líneas obsoletas, bórralas también. **No agregues líneas nuevas.**

## Tests requeridos (`Portal/tests/JourneyMonitor.Tests/Monitoring/TenantResolverProfileTests.cs`)

| Test | Aserción |
|---|---|
| `TenantResolver_MapsTenantAndDomainFromProfile` | |
| `TenantResolver_UnknownTenant_ReturnsUuid` | |
| `TenantResolver_ProfileWithoutTenants_ReturnsUuid` | |
| `CompanyProfileValidator_RejectsDuplicateTenantKey` | |
| `CompanyProfileValidator_RejectsNonGuidAadTenantId` | |
| `ArriviaSeed_DeclaresTelemetryTenants` | el seed parseado tiene los 3 tenants |

`CouplingInventoryTests` pasa sin la línea MT3.

## Criterios de aceptación

1. El gate `-Task MT3 -Mode Check -IncludeE2E` da PASS (E2E `daemons`).
2. En vivo, con un token guardado para `arrivia`, una corrida manual de Scout lo resuelve igual que antes. El log de Functions dice `Resolved tenant ... to key 'arrivia'` o la línea equivalente que haya dejado MT1.
3. `git grep -n '"arrivia"' Portal/src/JourneyMonitor.Core/Monitoring/TenantResolver.cs` no devuelve nada.
