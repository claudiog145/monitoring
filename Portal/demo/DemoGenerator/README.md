# DemoGenerator

Synthetic App Insights telemetry for the demo stack (contract v5 §4, `.github/spec1000/contracts-demo-telemetry.md`).
It reads the company packs under `Demo:PacksPath`, writes one Kustainer database per archetype app, backfills
history, keeps feeding live minutes and switches scenarios on and off. Everything it writes is synthetic.

## API (port 8080)

| Method | Route | Effect |
|---|---|---|
| `GET` | `/health` | `{"status":"ok","kusto":"reachable|unreachable","feeder":"running|stopped","lastFedAt":…}` |
| `GET` | `/api/companies` | Loaded packs: `companyKey, profileKey, displayName, apps[], scenarios[{key,title}]` |
| `POST` | `/api/reset` | Body `{ "companies"?: [..], "days"?: 1..30, "scale"?: "tiny|laptop|showcase" }`. Recreates the six tables of every app database of those companies, registers the appIds, backfills `days` up to now with the `historicalIncidents`, and answers `{ companies:[{companyKey, rows:{requests,…}}], elapsedSeconds }`. `400` for an unknown company/scale, `502` when Kusto fails. Synchronous. |
| `POST` | `/api/scenarios/{companyKey}/{scenarioKey}/start` | Body `{ "durationMinutes"?: 1..1440 }` (default: the scenario's). `404` if unknown. |
| `POST` | `/api/scenarios/stop` | Body `{ "companyKey"?: … }`: ends one company's scenarios or all. |
| `GET` | `/api/scenarios/active` | `[{companyKey, scenarioKey, title, startedAt, endsAt}]` |
| `POST` | `/api/feeder/start` · `/api/feeder/stop` | Live feeder on/off. |

Configuration (`Demo:*`): `KustoUrl`, `PacksPath`, `Scale` (default reset scale), `Days` (8), `Seed` (1000),
`FeederIntervalSeconds` (60), `FeederEnabled` (true), `IngestParallelism` (4 concurrent commands), `BatchHours`
(6 hours of one app per command), `DatabasePersistence` (`persist` under `KustoDataPath`=/kustodata/dbs, or `volatile`).

## How rows are made

- **Inside Kusto.** Each command is `.set-or-append <table> <| range m from <minute> to <minute> | mv-expand k = range(0, n-1) …`:
  one row per request, per-minute counts from `requestsPerDay × scale × hourlyCurve[h] × weekendFactor × traffic effects`,
  stochastic rounding so `tiny` keeps the right average. Per-row randomness is `U(rowKey, channel)`, an integer hash
  (Park-Miller + xor-shift, no overflow) that C# (`DeterministicRandom`) and KQL compute identically.
- **Reference model.** `ReferenceSynthesizer` computes the same rows in memory with the same formulas and channels;
  the unit tests measure it. Against Kustainer the two produce identical rows (count, operation_Id, timestamp,
  duration, success, step, entity, resultCode, session_Id). Change both together.
- **Shape.** Latency is log-normal fitted to p50/p95 (`sigma = ln(p95/p50)/1.645`); entities follow a bounded Zipf
  (continuous inverse CDF); every request has a unique 32-hex `operation_Id` shared by its dependencies
  (`operation_ParentId` = request id), exception, trace, page view and business event. A failed dependency fails its
  request (`502`, `System.Net.Http.HttpRequestException`). Failed requests always get one exception and one error
  trace; 5 % of successful ones get an informational trace; 20 % of successful page steps (GET, not `/api/`, not
  `.json/.xml`) get a page view. The entity id is in `customDimensions[entities.dimension]` (and in the host when
  `hostTemplate` is set).
- **Seed.** Each company derives its seed from `Demo:Seed` and its `companyKey`; each app from that and its appId.
  A reset with the same seed, scale and clock minute writes the same rows.
- **Sessions.** `session_Id`/`user_Id` group a user's requests into a journey that walks a flow of the pack's
  `profile.json` in order (`SessionFlows`): every archetype step is classified like the platform does (all flow
  steps stable-sorted by `order`, first host-accepted matcher wins) into a flow group and a 0-based position in
  that flow. A request at minute `m` of step position `p`, ranked `r` among the minute's requests of the same
  flow step across every entity (before thinning), belongs to session `hash(companySeed, group, m - p, r)`: rank
  `r` of step `p` continues rank `r` of step `p-1` one minute earlier, across the company's apps. Later steps
  being rarer gives the funnel and the drop-off. The rank ignores the entity on purpose: ranked per entity, a
  later step almost never found a request of the previous step one minute earlier (2–15 % of the sessions had
  more than one step, at night less); ranked per flow step, 40–70 % of the sessions walk several steps at any
  hour. The price is that a session's requests can carry different entities; the platform takes the journey's
  entity from its first step. Steps no flow claims (or every step when
  `profile.json` is missing) are one-request sessions. Sessions are assigned **after** every other column and use
  no channel of their own, so the KPI series (counts, timestamps, durations, success, resultCode, entity,
  business events) are byte-identical to the generator without sessions (`SessionTests.Sessions_DoNotChangeKpiSeries`
  keeps the pre-session digests of the fixture pack).
- **Regenerating history with sessions.** Rows written before sessions existed keep the old one-session-per-user-and-day
  ids; the feeder writes new minutes with flow sessions. To rewrite the history, `POST /api/reset` the company
  (same seed and scale: each minute gets the rows the generator without sessions would write, plus flow sessions). Only do it on
  request: it drops and refills the company's databases.
- **Scales.** `tiny` = 1 %, `laptop` = the archetype as written, `showcase` = ×4.

### Scenario effects

| Kind | Fields | Effect |
|---|---|---|
| `latency-multiplier` | `factor`, `step`? or `partner`? | Request latency × factor (all steps, one step, or the steps calling `partner`; with `partner` the partner's dependency latency is multiplied too). |
| `error-rate` | `rate`, `step`?, `exceptionType`? | Extra failure probability; failures raise `exceptionType` (message taken from the step's exception of that type, if any). |
| `dependency-failure` | `partner`, `rate` | Extra failure probability of every call to the partner; the request fails too. |
| `entity-only` | `entities` | Restricts every other effect of the scenario to those entity ids. |
| `traffic-multiplier` | `factor`, `step`? | Unscoped: scales the request count (a peak). Scoped by `step` or `entity-only`: only `factor <= 1` (drops matching requests). |

Effects apply per minute while the incident is active (`[start, end)`, whole minutes). Historical incidents start
`hoursAgo` before the reset (rounded to the minute); live scenarios start at the current minute. Every run of a live
scenario is kept for a day after it ends (starting it again adds a run and ends a still-running one), so minutes
written late still get the effects that were in force during them.

### Feeding and recovery

The feeder writes `[next minute, now)` for each app of every reset company every `FeederIntervalSeconds`, with the
historical incidents and the live scenarios. Progress is tracked per (app database, table): when a command fails,
only that table retries from its last written minute, so tables that succeeded never get duplicate rows. A restarted
generator reads `_demo_registry._company` (reset time, scale, days) and the latest `requests.timestamp` of each app
database and resumes from the next minute.

### appId registry (rule of `Portal/demo/FakeAppInsights/README.md`)

Database `_demo_registry`, table `_app(appId:string, database:string)`, rewritten on every reset with
`.set-or-replace _app <| _app | where appId !in (<ids>) | union (datatable(...) [...])`; the same database keeps
`_company(companyKey, resetAt, scale, backfillDays)`.

### Measured (temporary Kustainer, 6 GB, this laptop)

| Run | Rows | Time |
|---|---|---|
| `laptop`, 4 companies × 8 days (4 × 180 000 requests/day, 2 apps each), parallelism 4, 6 h batches | 12.7 M (5.2 M requests, 4.1 M dependencies, 2.6 M customEvents, 0.5 M pageViews, 0.29 M traces, 29 k exceptions) | 195 s |
| the same reset again on the loaded engine | 12.7 M | 199 s |
| one feeder tick (4 companies, 1 minute) | 48 commands | ~2 s |

Kustainer grows to ~5 GB with that volume and stays just under the 6 GB `mem_limit` during a second reset (24 h
batches got it OOM-killed once, hence the 6 h default). `showcase` (×4) needs a larger `KUSTAINER_MEM` or fewer days.
Row-for-row parity between `ReferenceSynthesizer` and the KQL was checked against Kustainer for both fixture apps
with every effect kind active.

## Packs (`Portal/demo/packs/<companyKey>/`) and `PackValidator`

`PackValidator.Validate(packDir)` returns every problem as `"<file>: <message>"`; empty = valid. Pack tests
(`DemoGenerator.Tests/Packs/<Name>PackTests.cs`) should assert it is empty. It checks:

- **archetype.json** — §4.2 rules (`ArchetypeLoader`): keys and prefixes (`demo-<companyKey>-*` / `<companyKey>-*`),
  displayName ends with `(demo)`, 24-value hourly curve, p95 > p50, rates 0..1, ≤ 8 dependencies per step, unique
  step names, every app has a step, scenarios reference existing steps/partners/entities, known effect kinds,
  historical incidents reference scenarios.
- **profile.json** — the errors of the platform's `CompanyProfileValidator` (labels, glossary, services/flows/steps,
  known matcher kinds, compiling regexes, identity sources, partners — including the RE2 check that rejects lookaround, backreferences, atomic groups, `\Z`, `\G` and the `n` flag in partner regexes, copied verbatim from `FindUnsupportedRe2Construct` — privacy); `profileKey` and `displayName` equal
  the archetype's; `entityIdentitySources` has `custom-dimension` with the archetype `entities.dimension`; every
  archetype `partner` is declared **and** is the first partner whose matchers claim each of its dependencies;
  **every archetype step URL is matched** (platform semantics of `url-contains`, `path-contains` on the lower-cased
  path, `operation-equals`, `operation-contains`, `regex`) by a step of a service that lists the app's alias and
  whose `hostFilters` accept the host.
- **sources.json** — `[{alias, environment:"production", resourceId, appId, ownerProfileKey, enabled:true, notes}]`;
  one source per archetype app with `appId` = the app's and `resourceId` ending in `/components/<database>`; alias
  lowercase, unique, `<companyKey>-<role>` (tourism reuses the Arrivia aliases); every alias is used by a service
  and every service lists one of them. The `resourceId` must match the platform rule
  `/subscriptions/<hex guid>/resourceGroups/<rg>/providers/microsoft.insights/components/<name>` — note that the
  contract example `…-00000000demo` is **not** hexadecimal and is rejected by `PUT /api/settings/telemetry-sources`;
  use e.g. `00000000-0000-0000-0000-000000000000`. When seeding, send `ownerProfileKey` as `profileKey`.
- **kpis.json** — an array of `POST /api/settings/kpis` bodies (`kpiDefinitionId:0`, pillar, kpi, domain, stage,
  sourceKey, direction, aggregation, evaluationFrequency `00:01:00`, bucketSize `00:05:00`, lookbackWindow ≥ `1.00:00:00`,
  sampleSize, minObservationsPerBucket, detectionMethod, methodParamsJson, consecutiveCount, cooldownMinutes,
  `enabled:true`, `profileKey`, grain, maxEntitiesPerBucket). Platform ranges and method parameters are enforced,
  `domain` is a profile service key (≤ 32 chars), entity-grain history fits the 8-day retention, natural keys are
  unique, and sourceKey is `appinsights:latency|exceptions|dependency` or `kql:kpi:<name>`. A `kql:` KPI carries the
  catalog query in **`kqlText`** (+ optional `kqlDescription`): seed it first with `PUT /api/settings/kql/<queryKey>`
  (`kpi:<name>`), then POST the KPI without those two members. `kqlText` may use only `{{from}} {{to}} {{bucket}}
  {{perBucket}} {{stage}} {{grain}}` and `app('{{SOURCE:<alias of this profile>}}')`, no other cross-resource call.
  Coverage: every service has Latency P95 (`appinsights:latency`) and Exceptions (`appinsights:exceptions`) in
  grain `domain` **and** `entity`, plus ≥ 2 Business `kql:` KPIs whose `kqlText` reads `customEvents` and names an
  archetype business event.
- **corpus/** — ≥ 10 `.md` under `corpus/<Domain>/` containing "Root Cause Analysis".
- **README.md** — present and not empty.

Archetype extensions beyond the §4.2 example (all optional): `entities.ids` (explicit ids instead of `idFormat`),
`latency-multiplier.partner`, `traffic-multiplier.step`.

A minimal valid pack used only by the tests lives in `DemoGenerator.Tests/Fixtures/minipack`.
