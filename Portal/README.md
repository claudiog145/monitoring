# JourneyMonitor (.NET)

JourneyMonitor is the .NET 10 / Azure port of the original Node.js **arrivia Agentic
Operations Portal** "Customer Journeys" backend (`reliable-portal`). It reconstructs
customer journeys (Hotels/Cars/Cruises/Air) from Application Insights telemetry and
serves the same operations portal UI.

**This is a behavioral-parity migration, not a redesign.** See
[MIGRATION.md](MIGRATION.md) for the JS → .NET component map and known gaps, and
[ARCHITECTURE.md](ARCHITECTURE.md) for how the pieces fit together.

## What this does

- Serves the operations portal UI (same HTML/CSS/JS as the original app, byte-for-byte,
  under `src/JourneyMonitor.Web/wwwroot`).
- Exposes `GET /api/journeys/db`, `POST /api/journeys/refresh`, and `GET /api/clubs`
  through the Web proxy. Journey refreshes query Application Insights and store a
  snapshot, which the browser then reads via `/api/journeys/db`.
- Everything else in the portal (Overview, Platform Health, Analytics, Simian Army,
  Exceptions tabs) reads static local JSON today, exactly like the original app - there
  is no server-side logic to port for those tabs.

## Solution layout

```
JourneyMonitor.slnx
src/
  JourneyMonitor.Core            domain models, mappers, calculators, use cases (no Azure deps)
  JourneyMonitor.Infrastructure  Application Insights, SQL cache, demo fixtures, KQL text
  JourneyMonitor.Functions       Azure Functions (isolated worker) - GetJourneys, GetClubs
  JourneyMonitor.Web             Razor Pages host: static UI + Entra ID + typed client proxy
tests/
  JourneyMonitor.Tests           xUnit characterization tests ported from test/*.test.js
```

See [ARCHITECTURE.md](ARCHITECTURE.md) for the dependency diagram and request flow.

## Prerequisites

- .NET 10 SDK and Visual Studio **2026 (18.0+)** (VS 2022 17.x cannot target `net10.0`)
- Azure Functions Core Tools v4 (`npm install -g azure-functions-core-tools@4`) to run
  `JourneyMonitor.Functions` locally
- Optional for live queries: `az login` with **Monitoring Reader** on the relevant
  Application Insights resource (same requirement as the original app)
- Optional for the durable journey store: a local SQL Server / Azure SQL database
  with the hotel/car/cruise/air schemas (`JourneyStore:ConnectionString`)

## Running it locally (first clone)

Use **two Visual Studio windows**: Functions in one, Web in the other. Do not use Multiple startup projects in a single VS instance (orphaned `func.exe` / `JourneyMonitor.Web.exe` lock port 7071 or the output `.exe`).

Repo-root walkthrough (clone, copy settings, F5): see the root [`README.md`](../README.md).

1. Open **`JourneyMonitor.slnx`** (this `Portal/` folder) in Visual Studio.
2. `src/JourneyMonitor.Functions/local.settings.json` is committed with Hotels Insights ids. Empty Insights values return **HTTP 400** (the API does not auto-fall back to demo). Demo: `?mode=demo` on `/api/journeys`.

3. `az login` (Monitoring Reader on that App Insights resource).
4. **Instance A:** startup project `JourneyMonitor.Functions` → F5 → `http://localhost:7071`. Start Azurite if Functions complains about storage.
5. **Instance B:** same solution, startup project `JourneyMonitor.Web`, profile **https** → F5 → `https://localhost:7152`. Leave `AzureAd:TenantId` empty. Keep `JourneyMonitorApi:BaseUrl` = `http://localhost:7071`.

Hotels and Cars use the Hotels App ID. Air stays 400 until both `Insights__Air__TenantId` and `Insights__Air__AppInsightsAppId` are set. Cruises needs `Insights__Cruises__AppInsightsAppId`.

If MSB3027 or “Port 7071 is unavailable”: Shift+F5, then `Get-Process func,JourneyMonitor.Web -ErrorAction SilentlyContinue | Stop-Process -Force`, then F5 again.

You can also run `JourneyMonitor.Web` alone: every tab except Customer Journeys works with zero backend (static JSON).

## Docker Compose (live App Insights)

Host `az login` is **not** visible inside the Functions container. The compose stack builds a local-only Functions image that includes Azure CLI.

From this `Portal/` folder, if `jm-web` / `jm-functions` are already running from an earlier `docker run`, stop them first.

```bash
docker compose up --build
```

In a second terminal, sign in **inside** the Functions container (Monitoring Reader on the Insights resource, same as F5):

```bash
docker compose exec functions az login --use-device-code --tenant 60396ff5-e90f-4bc5-aced-a4f462f2636e
```

Air lives in a second tenant; add:

```bash
docker compose exec functions az login --use-device-code --tenant b0298d22-1db0-4550-8fa6-a116a5ab4208
```

Then open http://localhost:5080 (Customer Journeys) or http://localhost:8081/api/journeys?domain=hotels.

Alternatively copy `.env.example` to `.env` and set `AZURE_TENANT_ID` / `AZURE_CLIENT_ID` / `AZURE_CLIENT_SECRET` for a service principal. Do not commit `.env`.

Compose builds the Functions image with Docker target `with-azure-cli`. Do not use that target when publishing to ACR.

## Configuration

All configuration is bound via `IConfiguration`/Options (no scattered
`Environment.GetEnvironmentVariable` calls):

| Section | Where | Purpose |
|---|---|---|
| `Insights` | Functions | Application Insights app ids / tenant ids per domain+environment (mirrors `local.config.json` in the original app) |
| `DemoJourneyFixture` | Functions | Optional overrides for the snapshot/demo fixture file paths |
| `JourneyStore:ConnectionString` | Functions | Azure SQL / LocalDB connection for durable journey snapshots; omit to disable sync and stored-reads |
| `AzureAd` | Web | Microsoft Entra ID SSO (Microsoft.Identity.Web); leave `TenantId` empty to skip auth entirely for local dev |
| `JourneyMonitorApi:BaseUrl` | Web | Base URL of the Functions host |

**No connection strings or keys are stored in source control.** `local.settings.json` holds Application Insights **application ids** and tenant ids for local clone; do not put instrumentation keys or SQL passwords there.

## Authentication

Production deployments authenticate every page via Microsoft Entra ID
(`Microsoft.Identity.Web`, OpenID Connect). Web → Functions calls should use a
Managed Identity service-to-service token in Azure (see `ARCHITECTURE.md` §Security).
Locally, leaving `AzureAd:TenantId` empty in `appsettings.json` skips auth wiring
entirely so a fresh clone still runs without an App Registration.

## SQL (journey store)

Azure SQL holds durable journey snapshots in the per-vertical `hotel`/`car`/`cruise`/`air`
schemas (`IJourneyStore`). Live KQL results are cached in-process for 90 seconds via
`InMemoryTelemetryQueryCache` (same shape as the original Node `Map` cache). There is
no `dbo.CachedQueries` table.

## Tests

```bash
dotnet test tests/JourneyMonitor.Tests/JourneyMonitor.Tests.csproj
```

These are characterization tests ported directly from the original app's
`test/*.test.js` (same fixture values and assertions) to prove behavioral parity -
see MIGRATION.md for the full list and how to extend them.
