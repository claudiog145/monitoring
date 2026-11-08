# Spec 1000 Ã— Company Profile â€” contract addendum v3

Applies to `feature/company-profile-decoupling` after the spec-1000 merge (`f2291f3`). Owned by the orchestrator. v1 (`contracts.md`) and v2 (`contracts-daemons.md`) still hold except where this file amends them. Tag after MT2 merges: `spec1000-contracts-v3`.

**Branch rule: NO `git push`, NO `gh pr create` for this integration branch or any task branch. Everything stays local.**

---

## Â§1 ProfileKey on KPI definitions (MT2)

### 1.1 Domain (`Core/Domain/Kpi.cs`)

`KpiDefinition` keeps its positional parameters unchanged and gains one init-only property:

```csharp
/// <summary>Company profile (monitoring.CompanyProfile.ProfileKey) this KPI belongs to.</summary>
public string ProfileKey { get; init; } = KpiProfiles.Default;
```

`KpiProfiles.Default` is a `const string` that holds the incumbent profile key. It is defined in a file under `Core/Company/` whose name contains `Arrivia`, so it is exempt from `CouplingInventoryTests`, for example `Core/Company/ArriviaKpiProfile.cs`. `KpiDefinition.Validate()` adds the error `"ProfileKey is required."` when the value is blank.

`KpiAnomaly`, `KpiBucket`, `KpiDetectionState` and `IngestionWatermark` do **not** change. They are keyed by `KpiDefinitionId`, and the profile is derived from the definition.

### 1.2 Triage (`Core/Domain/Triage.cs`)

`TriageSignal` gains a last optional positional parameter `string? ProfileKey = null`. Old JSON without the field stays valid. The daemon path always fills it from the KPI definition. The manual path fills it from the request, or from the active profile if the request omits it.

`TriageInvestigation` does not change shape. Its profile is `Signal.ProfileKey`.

### 1.3 SQL (idempotent, safe on the shared local DB that already has rows)

`CreateMonitoringKpi.sql`:
- `KpiDefinition.ProfileKey nvarchar(128) NOT NULL CONSTRAINT DF_monitoring_KpiDefinition_ProfileKey DEFAULT N'arrivia'`. Add it with `IF COL_LENGTH(...) IS NULL ALTER TABLE ... ADD`, and keep it in the `CREATE TABLE` for fresh databases.
- Replace `UQ_monitoring_KpiDefinition_Key (Pillar, Kpi, Domain, Stage)` with `UQ_monitoring_KpiDefinition_Key (ProfileKey, Pillar, Kpi, Domain, Stage)`. Drop it and recreate it only when the existing constraint lacks `ProfileKey`.
- Every procedure that reads or writes `KpiDefinition` carries `ProfileKey`. Upsert takes `@ProfileKey nvarchar(128)`, with no default. The audit table gets a nullable `ProfileKey` column that records the value at change time.

`CreateMonitoringTriage.sql`:
- `TriageInvestigation.ProfileKey nvarchar(128) NOT NULL DEFAULT N'arrivia'`, added idempotently.
- The insert procedure takes `@ProfileKey`.
- Index `IX_monitoring_TriageInvestigation_Profile (ProfileKey, CreatedAt DESC)`.
- The `DedupKey` rule does not change: `anomaly:{id}` is already globally unique.

`SeedKpiDefinitions.sql`: every seeded row has `ProfileKey = N'arrivia'`. The `MERGE`/`IF NOT EXISTS` match includes `ProfileKey`.

### 1.4 Abstractions (`IKpi.cs`, `ITriage.cs`)

Read methods that list definitions, anomalies, pillar status or investigations gain a trailing optional `string? profileKey = null`, placed **after** the existing optional parameters and **before** `CancellationToken`. If the existing method already has `CancellationToken ct = default` as the last parameter, insert `profileKey` just before it, and update every caller to use named `ct:` arguments where needed. `null` means every profile. That is what the daemons use: **ingestion and the health monitors process the enabled KPIs of every profile, always**, whatever company is active in the portal.

### 1.5 HTTP API (Functions) â€” the default is the active profile

| Route | Change |
|---|---|
| `GET /api/settings/kpis` | `?profileKey=`. Missing â†’ active profile (`IActiveCompanyProfileStore.GetActiveAsync`, then `Company:DefaultProfileKey`). `?profileKey=*` â†’ all profiles. Every item includes `profileKey`. |
| `POST /api/settings/kpis` | Body `profileKey`. Missing â†’ active profile. Unknown profile (not in `ICompanyProfileStore`) â†’ 400 `"Unknown profileKey '{x}'."` |
| `GET /api/health/pillars` | `?domain=&profileKey=`. Missing â†’ active profile. The response includes `profileKey`. |
| `GET /api/health/anomalies` | `?profileKey=`. Missing â†’ active profile. Join on the definition. |
| `GET /api/triage/investigations` | `?profileKey=`. Missing â†’ active profile. Each item includes `profileKey`. |
| `POST /api/triage/investigations` | Body `profileKey`. Missing â†’ active profile. |
| `GET /api/daemons`, `/api/daemons/activity` | Unchanged. Daemons are platform-wide. |

The JSON field name is `profileKey`, camelCase as in `TriageJson.Options`.

### 1.6 Portal

Platform Health, KPI settings and the triage list follow the header company selector. When it changes, they reload with the new active profile. The Daemons tab ignores the selector.

### 1.7 Ingestion source for non-incumbent profiles

`AppInsightsKpiSource` keeps resolving App Insights by `Domain` through `InsightsOptions`. A KPI of a profile other than `KpiProfiles.Default` whose domain has no App Insights source configured is skipped. The run writes the Warning activity line `"{Kpi}/{Domain} [{profileKey}]: no telemetry source configured for this profile"`; it never throws.

---

## Â§2 App Insights auth chain and automatic token refresh (MT1)

### 2.1 Credential order (`AppInsightsTelemetrySource`)

1. **API key**: if `Insights:QueryApiKeys` has the appId, send `X-Api-Key` (behaviour from main, unchanged).
2. **Stored token**: map the tenant to its `tenantKey` (`TenantResolver`), then read the stored token. Use it only if `ExpiresAt > now + 2 min`. The in-memory cache entry for a stored token **expires at the token's ExpiresAt âˆ’ 2 min**, so nothing is cached forever. A stored token that gets 401/403 is evicted, and the query is retried **once** with step 3.
3. **DefaultAzureCredential** per tenant: cached, because it renews itself. Same Managed Identity exclusion logic as today.

The credential factory for step 3 is injectable, so tests never call Azure. Logs name the path used (`api-key` | `stored-token` | `default-credential`) and never the token.

Tenant-key resolution moves out of `AppInsightsTelemetrySource` into `Core/Monitoring/TenantResolver.cs`. MT1 deletes the two `AppInsightsTelemetrySource.cs` lines from `coupling-allowlist.txt`.

### 2.2 Token table (`20241030_CreateAppInsightsTokensTable.sql`, idempotent `ALTER`s)

| Column | Type | Meaning |
|---|---|---|
| `RefreshMode` | `nvarchar(20) NOT NULL DEFAULT 'manual'` | `manual` \| `azure-cli` \| `service-principal` |
| `AadTenantId` | `nvarchar(64) NULL` | Entra tenant GUID used to acquire the token |
| `ClientId` | `nvarchar(64) NULL` | service principal app id (service-principal only) |
| `EncryptedClientSecret` | `nvarchar(max) NULL` | DataProtection-encrypted, the same protector as the token; **never returned by any API** |
| `LastRefreshAt` | `datetime2 NULL` | |
| `LastRefreshError` | `nvarchar(500) NULL` | last failure message, no secrets |
| `RefreshCount` | `int NOT NULL DEFAULT 0` | |

### 2.3 Refresh daemon

- Daemon key `token-refresh`. It is added to `DaemonKeys` / `MonkeyKey` and to `DaemonKeys.All`, so it shows in the Daemons tab and on the Simian cards. New activity kind `token-refreshed`.
- Timer `TokenRefreshFunction` with `[TimerTrigger("%spec1000_token_refresh_cron%")]`, default `0 */5 * * * *`. The setting must be added **everywhere `spec1000_monitor_cron` exists**: docker-compose (functions and functions-qa), `local.settings.json`, `publish-func-qa.ps1`, `publish-func-qa-test.ps1`, `deploy-qa.yml`, `deploy-prod.yml`, `Spec1000ServiceCollectionExtensions` if it validates crons, and `Spec1000TimerFunctionsTests`. A missing setting takes the Functions host down.
- Options `TokenRefresh:Enabled` (default `true`) and `TokenRefresh:RefreshAheadMinutes` (default `15`).
- Each run:
  - It selects active tokens with `RefreshMode <> 'manual'` and `ExpiresAt <= now + RefreshAhead`.
  - It acquires a new token for scope `https://api.applicationinsights.io/.default`. `azure-cli` uses `AzureCliCredential(TenantId = AadTenantId)`. `service-principal` uses `ClientSecretCredential(AadTenantId, ClientId, decrypted secret)`.
  - On success it saves the encrypted token and its `ExpiresAt`, sets `LastRefreshAt`, increments `RefreshCount` and clears `LastRefreshError`. It also evicts the telemetry source's cached stored-token entry, or relies on the expiry-bound cache.
  - On failure it keeps the old token and sets `LastRefreshError`.
- Activity lines:
  - `run-started`
  - per token, `token-refreshed` Info `"{tenantKey}: refreshed via {mode}, expires {HH:mm} UTC"`
  - per failure, Error `"{tenantKey}: refresh via {mode} failed â€” {message}"`
  - `run-completed` `"{r} refreshed, {f} failed, {n} not due"`

  When nothing is configured for refresh, `run-completed` reads `"No tokens configured for automatic refresh"`.

### 2.4 API (Functions, same gating as the existing token endpoints) and the portal

- The token save and update body accepts `refreshMode`, `aadTenantId`, `clientId` and `clientSecret`. `clientSecret` is write-only: an empty value keeps the stored secret. Validation:
  - `service-principal` requires `aadTenantId`, `clientId` and a stored or new secret;
  - `azure-cli` requires `aadTenantId`.
- The token list returns `refreshMode`, `aadTenantId`, `clientId`, `hasClientSecret`, `lastRefreshAt`, `lastRefreshError`, `refreshCount` and `expiresAt`, and **never** a token or a secret.
- `POST /api/appinsights/tokens/{tenantKey}/refresh` refreshes now. It returns 200 with the new `expiresAt`, 409 `"Manual tokens cannot be refreshed automatically; paste a new token."` for `manual`, 404 for an unknown key and 502 with the error message when acquisition fails. The Web proxy forwards it.
- Token page (`wwwroot/AppInsightsTokens.html`):
  - each row shows its auto-refresh mode, expiry countdown, last refresh and last error, plus a **Refresh now** button;
  - the add/edit form has the mode selector and the tenant id / client id / client secret fields; the secret is never echoed back.
