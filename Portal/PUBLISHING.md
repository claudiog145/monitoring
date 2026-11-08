# Publishing QA from PowerShell

Manual publish for the two QA Container Apps: the portal **Web App** and the **Functions** app. Both scripts build the image in Azure Container Registry, then point the Container App at a new unique tag so Azure creates a new revision.

Run them from the repository root, from the latest local pull of `origin/main` on GitHub.

| What | Script | Container App | Image |
|---|---|---|---|
| Functions | `Portal/scripts/publish-func-qa.ps1` | `platform-reliability-ca-func-qa` | `journeymonitor-functions` |
| Web App | `Portal/scripts/publish-web-qa.ps1` | `platform-reliability-ca-wa-qa` | `journeymonitor-web` |

Both live in resource group `platform-reliability-rg-qa`, subscription `89780439-fb9b-4f7b-bf37-115569216afe`, registry `iceacrnonprod`.

## Prerequisites

- Latest code from the **`main`** branch
- [Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli) (`az`)
- Signed in to the **ICE** tenant
- **Contributor** on registry `iceacrnonprod` and on the Container App you are updating

From `ai-reliable-platform`, pull `main` from GitHub:

```powershell
git checkout main
git pull origin main
```

Then sign in:

```powershell
az login --tenant 60396ff5-e90f-4bc5-aced-a4f462f2636e
az account show --query "{name:name, id:id, user:user.name}" -o table
```

The script sets the subscription. Confirm the account above is the ICE tenant before you publish.

## Before publishing

> **Make sure everything is working locally on Docker first.** Do not publish until the local stack is up and the change you are shipping behaves correctly there.

From the `Portal` folder:

```powershell
docker compose up --build
```

Then open http://localhost:5080. Details, including the sign-in inside the Functions container, are in [README.md](README.md#docker-compose-live-app-insights).

## Order

1. Publish **Functions** whenever Functions, Core, or Infrastructure changed.
2. Publish the **Web App** only when the portal itself changed (Razor host, `wwwroot`, or Web config).

A Functions-only change does not need a Web publish. A Web-only change does not need a Functions publish.

## Functions

Open PowerShell in the **`ai-reliable-platform`** repository root — the folder that contains `Portal\`. The publish commands use `.\Portal\scripts\...`, so they only work from that folder.

```powershell
cd C:\path\to\ai-reliable-platform
powershell -NoProfile -ExecutionPolicy Bypass -File .\Portal\scripts\publish-func-qa.ps1
```

> **Image updates do not change existing environment variables or secrets.** The script only adds missing Spec 1000 settings and pins `--min-replicas 1` — see [Spec 1000 — siempre activo](#spec-1000--siempre-activo).

## Web App

Open PowerShell in the **`ai-reliable-platform`** repository root — the folder that contains `Portal\`. The publish commands use `.\Portal\scripts\...`, so they only work from that folder.

```powershell
cd C:\path\to\ai-reliable-platform
powershell -NoProfile -ExecutionPolicy Bypass -File .\Portal\scripts\publish-web-qa.ps1
```

> **Image updates do not change existing environment variables or secrets.**

## Database

> **The database is not changed from here.** These scripts only publish the container image. They do not run SQL or alter schema or data.

## Verify

In the Azure portal, open the Container App you published (`platform-reliability-ca-func-qa` or `platform-reliability-ca-wa-qa`). On **Revisions and replicas**, the latest replica should show **today's date** and a **green Ready** state.

## Spec 1000 — siempre activo

### Why `minReplicas ≥ 1`

The Spec 1000 timers (`KpiIngestion` on `spec1000_ingestion_cron`, `ApplicationHealthMonitor` / `BusinessHealthMonitor` / `InfrastructureHealthMonitor` on `spec1000_monitor_cron`) run every minute. A Container App that scales to zero has no running host, so no timer fires and no KPI is ingested or evaluated until an HTTP request wakes it up. The Functions app is therefore pinned to **at least one replica**:

- `Portal/scripts/publish-func-qa.ps1` runs `az containerapp update ... --min-replicas 1 --max-replicas <current, 1 when unset>` on every publish.
- `.github/workflows/deploy-qa.yml` does the same in the `functions-settings` job, which runs **before** the shared workflow swaps the image (`functions` has `needs: functions-settings`). The shared workflow's `az containerapp update --image` keeps env vars and scale, so the new revision starts with its settings and stays at one replica or more. This job is **optional**; see [Pipeline identity](#pipeline-identity-spec1000_azure_) below.
- `Portal/scripts/publish-func-qa-test.ps1` (staging app) does not change scaling.

Both scripts and the workflow also add the Spec 1000 settings **only when they are missing**, before the new image starts, because without the three cron values the host fails indexing and every function goes down. A value already changed in Azure is never overwritten by a publish:

| Setting | Default |
|---|---|
| `spec1000_ingestion_cron` | `0 */1 * * * *` |
| `spec1000_monitor_cron` | `30 */1 * * * *` |
| `spec1000_token_refresh_cron` | `0 */5 * * * *` (`token-refresh` daemon: renews stored App Insights tokens set to `azure-cli` or `service-principal` before they expire) |
| `HealthMonitoring__Enabled` | `false` (timers fire but do no work) |
| `Triage__Mode` | `Shadow` (`Off` \| `Shadow` \| `Live`) |

Locally, `docker-compose.yml` sets the same defaults; override them in `Portal/.env` (see `.env.example`).

### Pipeline identity (`SPEC1000_AZURE_*`)

The shared workflow only logs in with the ACR push identity (`vars.ARRIVIA_NONPROD_ACR_*`), which cannot change Container App settings. The `functions-settings` job therefore uses its own identity from these variables (`# TODO confirmar` in both workflows):

| Variable | Scope | Value |
|---|---|---|
| `SPEC1000_AZURE_CLIENT_ID` | **Repository** variable (a job-level `if:` cannot read environment variables) | Client id of an identity with **Contributor** on the Functions Container App, with a federated credential for this repo: environment `qa` for QA, branch `main` for production |
| `SPEC1000_AZURE_SUBSCRIPTION_ID` | Repository variable | Subscription of the Functions Container App |
| `ARRIVIA_AZURE_TENANT_ID` | Already exists (org variable used by the shared workflow) | Tenant |

Behaviour:

- `SPEC1000_AZURE_CLIENT_ID` **not set** → `functions-settings` is skipped and `functions` deploys as before (`if: !failure() && !cancelled()`). The pipeline does **not** add the Spec 1000 settings or pin min replicas.
- Set, and the job **fails** → `functions` does not run, so no image starts without its settings.
- Set, and the job succeeds → the settings are added if missing, min replicas pinned, then the image is swapped.

> **Until the `SPEC1000_AZURE_*` variables exist, apply the Spec 1000 settings once by hand, BEFORE the first pipeline deploy of an image that contains the Spec 1000 timers.** Run `Portal/scripts/publish-func-qa.ps1` — it sets the missing settings and `--min-replicas 1` in the same update as the image. Otherwise the new revision starts without `spec1000_*_cron` and the Functions host fails indexing. After that, image updates (script or pipeline) keep the env vars and the scale.

### Verify

```powershell
az containerapp show --name platform-reliability-ca-func-qa `
  --resource-group platform-reliability-rg-qa `
  --query properties.template.scale
```

`minReplicas` must be `1` or more. To check the settings:

```powershell
az containerapp show --name platform-reliability-ca-func-qa `
  --resource-group platform-reliability-rg-qa `
  --query "properties.template.containers[0].env[?starts_with(name, 'spec1000_') || starts_with(name, 'HealthMonitoring__') || starts_with(name, 'Triage__')]" `
  --output table
```

### Turn on shadow mode in QA

Shadow mode = ingestion and detection running, triage in `Shadow`. It is a one-time env var change; later publishes keep it:

```powershell
az containerapp update --name platform-reliability-ca-func-qa `
  --resource-group platform-reliability-rg-qa `
  --set-env-vars "HealthMonitoring__Enabled=true" "Triage__Mode=Shadow"
```

Turn it off again with `HealthMonitoring__Enabled=false`. Do not set `Triage__Mode=Live` until triage has been validated.

### Enable production

`.github/workflows/deploy-prod.yml` exists but is **off**. It only runs when:

- it is started by hand (`workflow_dispatch`; nothing runs on merge),
- from `main` (every job checks `github.ref == 'refs/heads/main'`),
- with the repository variable `SPEC1000_PROD_ENABLED` set to exactly `true` (every job checks it),
- and a reviewer approves the `production` environment.

Order of a run: `approve` (production approval) → `functions-settings` (optional, only with `SPEC1000_AZURE_CLIENT_ID`; Azure login, then missing Spec 1000 settings and `--min-replicas 1` on the Functions app) → `functions` (shared workflow, image swap); `web` only needs `approve`. `functions` requires `approve` to have **succeeded** and runs when `functions-settings` succeeded or was skipped, never when it failed. `concurrency: deploy-prod` keeps a single production deploy running and never cancels one half-way (`deploy-qa.yml` uses `deploy-qa` the same way).

**Approvals.** Only `approve` declares `environment: production` in this file, so this file asks for **one** approval per run. `functions-settings` reads repository variables only and does not declare an environment, so it never asks again. Reusable-workflow jobs cannot declare `environment:`. If the shared workflow maps `environments: - production` onto a job environment, that job asks for its own approval: one for Web and one for Functions. Check this in the first run.

#### Confirm before enabling

Each item below is marked `# TODO confirmar` in the workflows:

1. `deploy-prod.yml` → `FUNC_CONTAINER_APP` (assumed `platform-reliability-ca-func-prd`).
2. `deploy-prod.yml` → `WEB_CONTAINER_APP` (assumed `platform-reliability-ca-wa-prd`).
3. `deploy-prod.yml` → `RESOURCE_GROUP` (assumed `platform-reliability-rg-prd`).
4. `deploy-prod.yml` → `SHARED_WORKFLOW_ENVIRONMENT`, the name the shared workflow deploys to (assumed `production`).
5. The `SPEC1000_AZURE_*` identity (see [Pipeline identity](#pipeline-identity-spec1000_azure_)). The variables are repository-wide, so QA and production share one identity. It needs Contributor on **both** Functions Container Apps and a federated credential for `main` in addition to the `qa` environment. If production must use a separate identity, give `deploy-prod.yml` its own variable names first. While the variables are unset, run the settings step by hand before the first production deploy (same `az containerapp update ... --min-replicas 1 --set-env-vars ...` as the script, against the production app).
6. The production Functions Container App already exists. `functions-settings` reads its env vars and scale before the image swap and fails if the app is missing.

#### Steps

1. Confirm every item above and update the workflow values if needed.
2. In **Settings → Environments**, create `production` with **Required reviewers**. Optionally, set **Deployment branches** to `main`.
3. In **Settings → Secrets and variables → Actions → Variables**, add `SPEC1000_PROD_ENABLED` = `true`.
4. **Actions → Deploy Production → Run workflow** on `main`, then approve the deployment.

Production starts with the same safe defaults (`HealthMonitoring__Enabled=false`, `Triage__Mode=Shadow`) and `--min-replicas 1`. To disable the workflow again, set `SPEC1000_PROD_ENABLED` to any other value.
