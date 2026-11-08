---
name: spec1000-mt1-auth-token-refresh
description: "Tarea MT1 (rama company-profile): cadena de credenciales de App Insights sin cache eterno (API key → token guardado vigente → DefaultAzureCredential) y refresco automático de tokens guardados con un daemon nuevo token-refresh."
agent: spec1000-implementer
argument-hint: "TASK=MT1 WORKTREE=<ruta> ITERATION=<n>"
---
# MT1 · Cadena de credenciales de App Insights + refresco automático de tokens

**Rama de integración:** `feature/company-profile-decoupling` (merge de spec-1000 en `f2291f3`). **Prohibido `git push` y `gh pr create`.**

**Contrato:** `.github/spec1000/contracts-company.md` §2 es la especificación. v1/v2 siguen congelados. Excepción: puedes **agregar** (nunca cambiar ni borrar) la clave de daemon `token-refresh`, su entrada en `DaemonKeys.All` / `MonkeyKey` y el kind `token-refreshed` en `Core/Domain/DaemonActivity.cs`, y el `MonkeyKey` donde esté declarado.

## Problema real

`AppInsightsTelemetrySource.GetCredentialAsync` cachea un `BearerTokenCredential` por tenant en `_credentialByTenant` para siempre. Un token pegado en el portal dura ~1 h. Después de esa hora, todas las consultas, incluida la ingesta de KPIs que corre cada minuto, fallan con 401 hasta reiniciar el host, y no vuelven a `DefaultAzureCredential`. Además, el usuario quiere que los tokens guardados se **refresquen solos**.

## Archivos que te pertenecen

Ver `tasks.json` → `MT1`. Si necesitas un archivo que no está en la lista, para y repórtalo en tu entrega en vez de tocarlo.

## Diseño (obligatorio)

1. **`AppInsightsTelemetrySource`** (contrato §2.1):
   - Orden: API key → token guardado con `ExpiresAt > now + 2 min` → `DefaultAzureCredential` por tenant.
   - El cache del token guardado guarda `(token, expiresAt)` y caduca en `expiresAt − 2 min`.
   - Si la consulta con token guardado da 401 o 403: se descarta del cache y se reintenta **una vez** con DefaultAzureCredential.
   - Inyecta una fábrica `Func<string?, TokenCredential>` para el paso 3 (constructor adicional o parámetro opcional), así los tests no llaman a Azure. El constructor que usa DI sigue funcionando.
   - Logs del camino usado, sin el token.
   - Mueve `ResolveTenantKey` a `Core/Monitoring/TenantResolver.cs` (método público o estático equivalente con las mismas reglas: Hotels → `arrivia`, Air → `ice`, Cruises → `cruises`, si no el UUID). Borra de `Portal/tests/JourneyMonitor.Tests/Company/coupling-allowlist.txt` las **dos** líneas `AppInsightsTelemetrySource.cs|...|OWNED BY MT1...`. No toques la línea de TenantResolver: es de MT3.
2. **Tabla de tokens** (§2.2):
   - `ALTER` idempotentes en `20241030_CreateAppInsightsTokensTable.sql`.
   - El modelo `AppInsightsToken` y `SqlAppInsightsTokenRepository` leen y escriben las columnas nuevas.
   - El secreto del cliente se cifra con el mismo DataProtection que el token.
3. **Adquisición:**
   - Interfaz Core `IAppInsightsTokenAcquirer` (`Task<(string Token, DateTimeOffset ExpiresOn)> AcquireAsync(string refreshMode, string aadTenantId, string? clientId, string? clientSecret, CancellationToken ct)`).
   - Implementación en Infrastructure: `AzureCliCredential` / `ClientSecretCredential`, scope `https://api.applicationinsights.io/.default`.
4. **`AppInsightsTokenRefreshService`** (Core, §2.3):
   - `RefreshDueAsync(DateTimeOffset now, ct)` y `RefreshNowAsync(string tenantKey, ct)`.
   - Escribe la actividad del daemon `token-refresh` con los textos **exactos** del contrato.
   - Nunca registra el token ni el secreto: ni en logs, ni en actividad, ni en errores. Si una excepción contiene el secreto, se redacta.
5. **Timer** `TokenRefreshFunction` con `%spec1000_token_refresh_cron%` (default `0 */5 * * * *`):
   - Agrega el setting en **todos** los lugares listados en §2.3. Revisa con `git grep spec1000_monitor_cron`.
   - Opciones `TokenRefresh:Enabled` y `TokenRefresh:RefreshAheadMinutes`.
   - Con `Enabled=false`, la corrida registra `run-skipped` y no hace nada.
6. **API y portal** (§2.4):
   - Endpoints en `AppInsightsTokenController`, proxy en Web `Program.cs` + `JourneyMonitorApiClient`.
   - UI en `wwwroot/AppInsightsTokens.html`: modo de refresco, campos, botón **Refresh now** y estado.
   - La tarjeta del daemon aparece en la pestaña Daemons, porque el cliente lista lo que devuelve `/api/daemons`. Verifica que `DaemonFunctions` incluya `token-refresh`.

## Tests requeridos

Tests nuevos en `Portal/tests/JourneyMonitor.Tests/Infrastructure/AppInsightsTelemetrySourceAuthTests.cs` y `Portal/tests/JourneyMonitor.Tests/Monitoring/TokenRefresh/AppInsightsTokenRefreshServiceTests.cs`, con estos nombres exactos:

| Test | Aserción |
|---|---|
| `TelemetrySource_ApiKeyWins` | con API key no se lee el token guardado ni se pide credencial |
| `TelemetrySource_ValidStoredToken_IsUsed` | Bearer = token guardado; fábrica de default no invocada |
| `TelemetrySource_ExpiredStoredToken_FallsBackToDefaultCredential` | token con `ExpiresAt` pasado → usa la fábrica |
| `TelemetrySource_StoredTokenNotCachedPastExpiry` | reloj avanza más allá de `ExpiresAt − 2 min` → vuelve a leer el store y, si no hay token vigente, usa default |
| `TelemetrySource_StoredToken401_RetriesOnceWithDefault` | primer request 401 con token guardado → segundo request con default; solo un reintento |
| `TokenRefresh_DueToken_IsRefreshedAndSaved` | modo `azure-cli`, vence en 10 min → token y ExpiresAt nuevos guardados, `RefreshCount` + 1, actividad `token-refreshed` |
| `TokenRefresh_NotDue_IsSkipped` | vence en 50 min → sin adquisición |
| `TokenRefresh_ManualMode_IsNeverRefreshed` | modo `manual` vencido → sin adquisición |
| `TokenRefresh_AcquireFailure_KeepsOldTokenAndRecordsError` | adquisición lanza → token viejo intacto, `LastRefreshError` con mensaje, actividad Error |
| `TokenRefresh_RefreshNow_ManualMode_IsRejected` | `RefreshNowAsync` en manual → resultado rechazado (409 en API) |
| `TokenRefresh_NeverLogsSecrets` | secreto y token no aparecen en ninguna línea de actividad ni log capturado |
| `TokenRefresh_ServicePrincipal_RequiresClientIdAndSecret` | validación de guardado |

Actualiza `Spec1000TimerFunctionsTests` para el cron nuevo. `AppInsightsTelemetrySourceApiKeyTests` debe seguir verde.

## Criterios de aceptación

1. Build sin warnings nuevos y suite completa verde. El gate `-Task MT1 -Mode Check -IncludeE2E` da PASS (E2E `daemons-web`).
2. Con el stack local:
   - `GET /api/daemons` incluye `token-refresh`;
   - la consola de Daemons muestra su `run-completed` cada 5 min;
   - `POST /api/appinsights/tokens/{key}/refresh` sobre un token `manual` devuelve 409.
3. Ninguna respuesta de API contiene `EncryptedToken`, un token o un secreto.
4. La ingesta de KPIs sigue funcionando sin token guardado (camino DefaultAzureCredential).
