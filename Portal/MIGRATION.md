# Migration Map: JavaScript (`reliable-portal`) -> .NET (`reliable-portal-dotnet`)

This document maps every behaviorally significant piece of the original Node.js app to
its .NET replacement, and records known differences/gaps discovered during the port.
See `reliable-portal/MIGRATION_ANALYSIS.md` for the full discovery notes this map is
based on.

## Scope of the migration

Only **Customer Journeys** (`GET /api/journeys`, `GET /api/clubs`) has server-side
behavior in the original app. Every other tab (Overview, Platform Health, Analytics,
Simian Army, Exceptions) reads static local JSON directly in the browser - there is
nothing to port for those tabs beyond serving the same static files, which
`JourneyMonitor.Web/wwwroot` does verbatim.

## Component map

| Original (JS) | .NET replacement | Notes |
|---|---|---|
| `index.html`, `styles.css`, `app.js` | `JourneyMonitor.Web/wwwroot/*` (verbatim copy) | Byte-for-byte identical files. Guarantees UI/behavioral parity without re-implementing client-side rendering. |
| `map-v6.js` (Hotel/Cars/Cruise row -> journey) | `JourneyMonitor.Web/wwwroot/map-v6.js` (verbatim, still runs client-side) **and** `JourneyMonitor.Core.Mapping.HotelJourneyMapper` (ported, tested, **not yet wired**) | See ARCHITECTURE.md "Why raw rows, not mapped journeys". The C# mapper is a tested foundation for a future server-side-mapping phase. |
| `map-air.js` (Air row -> journey) | `JourneyMonitor.Web/wwwroot/map-air.js` (verbatim) **and** `JourneyMonitor.Core.Mapping.AirJourneyMapper` (ported, tested, **not yet wired**) | Same as above. |
| `domain-health.js` | `JourneyMonitor.Web/wwwroot/domain-health.js` (verbatim, client-side) **and** `JourneyMonitor.Core.Analytics.DomainHealthCalculator` (ported, tested) | Ported logic not yet exposed via an endpoint - the browser still computes this from its in-memory mapped journeys, exactly as before. |
| `domain-comparison.js` | `JourneyMonitor.Web/wwwroot/domain-comparison.js` (verbatim) **and** `JourneyMonitor.Core.Analytics.DomainComparisonCalculator` (ported, tested) | Same as above. |
| `server.js` static file serving | `JourneyMonitor.Web` (ASP.NET Core static files middleware) | |
| `server.js` `/api/journeys` handler (`handleJourneysApi`) | `JourneyMonitor.Core.UseCases.GetJourneysService` + `JourneyMonitor.Functions.GetJourneysFunction` | Same live -> snapshot -> demo fallback chain, same query-string contract. |
| `server.js` `/api/clubs` handler | `JourneyMonitor.Core.UseCases.GetClubsService` + `JourneyMonitor.Functions.GetClubsFunction` | **Deliberate divergence (2026-09-28):** club list is read from `brand.Information` (platformDB) via `IBrandCatalog`/`SqlBrandCatalog` instead of the App Insights discovery KQL; demo list remains the no-database fallback. |
| `query-appinsights.js` (`az account get-access-token` + REST POST + `tableToRows`/`maybeParseJson`) | `JourneyMonitor.Infrastructure.AppInsights.AppInsightsTelemetrySource` | Same REST endpoint (`api.applicationinsights.io/v1/apps/{appId}/query`) and response-shape parsing; `az` CLI token replaced with `Azure.Identity.DefaultAzureCredential` (works with `az login` locally, Managed Identity in Azure). |
| `club-filter.js` (`clubLabel`, `controlState`, row-cap injection, KQL text mutation) | `JourneyMonitor.Core.Filtering.ClubFilter` + `ClubDiscoveryKql` | Same regex-based KQL text injection strategy (`let win=`, `let tmoHost=`, `let tmoClub=`, `top N by ... desc` rewriting). |
| `insights-config.js` (`resolveInsightsSource`) | `JourneyMonitor.Core.Configuration.InsightsSourceResolver` | Same GUID-based tenant/app-id resolution rules, same legacy-Hotel-app-id special case, same Air/Cruise/Cars error messages. |
| in-process 90s `Map` cache in `server.js` | `JourneyMonitor.Infrastructure.Caching.InMemoryTelemetryQueryCache` via `ITelemetryQueryCache` | Same cache-key shape (domain/win/club/environment/rowCap) and TTL. Durable snapshots are `IJourneyStore`, not `dbo.CachedQueries`. |
| `loadSnapshot`/`loadDemo` in `server.js` (demo/snapshot fixture loading + timestamp shifting) | `JourneyMonitor.Infrastructure.Demo.DemoJourneyFixtureProvider` | Same shifting-to-"now" logic so demo data always looks recent. |
| `kql/*.txt` | `JourneyMonitor.Infrastructure/Kql/*.txt` (embedded resources) via `KqlQueryTextProvider` | Copied verbatim, unmodified. |
| `data/demo-journeys.json` | `JourneyMonitor.Infrastructure/Demo/Data/demo-journeys.json` | Copied verbatim (anonymized fixture, safe to commit). |
| `api/src/functions/journeys.js` (draft, undeployed JS Azure Function) | `JourneyMonitor.Functions/Functions/GetJourneysFunction.cs` | The .NET Function is the actual, wired, hand-crafted implementation - the JS draft was never deployed and had no additional behavior beyond `server.js`. |
| `local.config.example.json` | `JourneyMonitor.Functions/local.settings.example.json` + `appsettings.json` `Insights` section | Same per-domain/environment app-id/tenant-id shape, bound to `InsightsOptions` instead of a hand-parsed JSON file. |
| `test/*.test.js` (club-filter, insights-config, domain-health, domain-comparison, map-air, map-v6) | `tests/JourneyMonitor.Tests/**/*Tests.cs` | Ported as xUnit characterization tests with the same fixture values and assertions - see "Characterization test coverage" below. |
| No auth in the original app (local dev tool) | Microsoft Entra ID SSO (`Microsoft.Identity.Web`) in `JourneyMonitor.Web` | New requirement from the migration spec, not a parity concern - the original app had no auth because it only ever ran on a developer's machine. |

## Characterization test coverage

Ported so far (all passing, `dotnet test`):

- `Analytics/DomainHealthCalculatorTests.cs` <- `test/domain-health.test.js`
- `Filtering/ClubFilterTests.cs` <- `test/club-filter.test.js`
- `Configuration/InsightsSourceResolverTests.cs` <- `test/insights-config.test.js`
- `Mapping/HotelJourneyMapperTests.cs` <- `test/map-v6.test.js` (Cruise/Cars id selection,
  field mapping, staging/production destination labeling, the full "Failed at step N"
  1-11 status-label truth table, Purchase Failed / Dropped-at-step / Hotels-domain edge
  cases)
- `UseCases/GetJourneysServiceDemoModeTests.cs` - a new end-to-end test (no direct JS
  equivalent file, but exercises the same `loadDemo()`-driven fallback branch of
  `handleJourneysApi`) proving the full `GetJourneysService` pipeline against the real
  anonymized `demo-journeys.json` fixture, including timestamp shifting and the
  Air-has-no-demo-fixture error path.

Not yet ported (tracked as follow-up, not required for the current increment to be
useful):

- `Mapping/AirJourneyMapperTests.cs` <- `test/map-air.test.js`
- `Analytics/DomainComparisonCalculatorTests.cs` <- `test/domain-comparison.test.js`

To port one: open the corresponding `test/*.test.js` file, transcribe each fixture and
assertion into an xUnit `[Fact]`/`[Theory]` against the matching C# class (see
`Mapping/HotelJourneyMapperTests.cs` for the pattern of building a `TelemetryJourneyRow`
from an inline JSON literal via `RowFromJson`).

## Known behavioral notes / oddities preserved intentionally

- `DomainHealthCalculator.JourneyCallCount`/`JourneyFailedCalls` fall back to summing
  `Stages`/`Calls` only when `TotalCalls`/`FailedCalls` are zero. The original JS
  (`Number.isFinite(Number(journey.totalCalls))`) treats an explicit `0` as finite and
  uses it as-is; the .NET fallback is looser than that edge case, but every observed
  fixture populates `TotalCalls` from the KQL row so this has not been seen to produce
  a different result in practice. Flagged here per the spec's "document, don't silently
  fix" rule rather than fixed, since changing it risks diverging from the JS behavior
  in an untested corner case.
- Hotel/Cars/Cruise `JourneyCall` entries never set `Error = true` in the mapped model;
  only Air calls do. This matches `map-v6.js` (`error` is never set on the object built
  for those domains) vs `map-air.js` (`error: !passed`) exactly - not a bug, a
  deliberate parity preservation confirmed by reading both mapping files side by side.
- Row-cap/`top N by ... desc` KQL rewriting is done via targeted regex replacement (not
  a KQL parser), exactly as `club-filter.js` does it. This is intentionally preserved
  even though a real KQL parser would be more robust, because behavior must match
  exactly, including any quirks of the regex approach.

## Known gaps / deferred work

- **Web -> Functions authentication**: `JourneyMonitorApiClient` does not yet attach a
  Managed Identity token. Fine for local dev (`func start` is unauthenticated by
  default); required before a real Azure deployment. See ARCHITECTURE.md "Security".
- **Air demo fixture**: the original app has no anonymized Air fixture either
  (`data/demo-journeys.json` only covers Hotels/club 15639) - `GetJourneysServiceDemoModeTests`
  documents this as an expected 502 "Air has no anonymized demo fixture yet" response,
  matching `server.js` exactly. Not a regression; carried over as-is.
- **Bicep/infra for the .NET resources** (Function App, Web App, Azure SQL, managed
  identity, Entra App Registration) has not been authored - the original `infra/*.bicep`
  covered only the JS draft Function and was itself never applied. This is lower
  priority than proving functional parity and is left for a follow-up, since no real
  Azure subscription/tenant is available in this environment to validate against.
- **`AirJourneyMapperTests`/`DomainComparisonCalculatorTests`**: not yet written (see
  above) - the underlying C# code is already ported, just not yet characterization-
  tested.
