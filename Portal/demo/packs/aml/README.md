# Kinectify (demo) — company pack `aml`

Synthetic AML / compliance SaaS used by casinos, fintechs and crypto exchanges. Every host, partner, customer
tenant, account number and e-mail in this pack is invented (`*.demo`, `*.kinectify.internal`, `example.test`).
Contract: `.github/spec1000/contracts-demo-telemetry.md` §6. Validated by `PackValidator` (test
`DemoGenerator.Tests/Packs/AmlPackTests.cs`).

| | |
|---|---|
| companyKey / profileKey | `aml` / `kinectify` |
| Apps (appId → database, alias) | `demo-aml-screening-api` → `aml-screening-api`; `demo-aml-onboarding-kyc` → `aml-onboarding-kyc`; `demo-aml-case-mgmt` → `aml-case-mgmt`; `demo-aml-alerts-worker` → `aml-alerts-worker` (aliases = database names) |
| Products (services) | `screening`, `alerts`, `cases`, `onboarding` |
| Entity | customer tenant, `customDimensions.CustomerTenantId` = `T-001`…`T-060`, Zipf 1.15 |
| Partners | `sanctions-provider` (sanctions & PEP lists), `idv-provider` (identity verification), `chain-analytics` (blockchain analytics), `doc-ocr` (document OCR), internal `scoring-model`, `alert-queue`, `screening-db`, `case-db`, `onboarding-db` |
| Volume (`laptop`) | 100 000 requests/day, flat-ish curve (global customers), weekend × 0.85. Measured (temporary Kustainer, 6 GB): 8 days = 0.76 M requests, 1.38 M dependencies, 0.67 M customEvents, 43 k traces, 4.7 k exceptions (~2.9 M rows) |
| Business events | `TransactionReceived`, `TransactionScreened`, `AlertRaised`, `CaseOpened`, `CaseClosed`, `ApplicationSubmitted`, `IdentityVerified`, `ClientApproved` |
| Business KPIs (`kql:`) | `kql:kpi:aml-transactions-screened` (TransactionScreened), `kql:kpi:aml-alerts-raised` (AlertRaised), `kql:kpi:aml-clients-approved` (ClientApproved) — all `Decrease` |
| Privacy | `piiDimensions`: CustomerName, AccountNumber, DateOfBirth, EmailAddress, WalletAddress; `patterns`: 12–19 digit account numbers, e-mails, `0x` wallet addresses |

Workflows: **transaction** ingest → risk scoring → alert generation → case escalation → open / review / close
case; **onboarding** submit → ID verification → sanctions check → approve.

## Scenarios

| Key | Kind | What it does | Expected detection (real platform evaluator over 48 reset times, see Notes; n = runs replayed). "Always" fires in ≥ 95 % of the runs; every usually / sometimes / rarely rate is the range measured across three independent sweeps (A, B and C, see Notes) |
|---|---|---|---|
| `sanctions-provider-outage` | partner outage | `sanctions-provider` fails 70 %, ×5 latency | always (96/96): Latency P95 + Exceptions `screening`, entity Exceptions `screening`, *Transactions screened* drops. Usually (~90–96 %): Exceptions `onboarding` (the misses are night runs, when onboarding has only a few requests per bucket). Sometimes (~46–73 %): entity Latency P95 of the largest customers |
| `scoring-model-deploy` | latency after deploy | internal `scoring-model` ×6 latency, +3 % `ModelInferenceException` on risk scoring | always (96/96): Latency P95 `screening` (domain). Usually (~77–88 %): entity Latency P95 of the largest customers; sometimes (~33–48 %) their *Median latency* |
| `alert-queue-backlog` | exception storm | +75 % `ServiceBusException` on `PROCESS alert-generation`, `alert-queue` ×8 | always (96/96): Exceptions `alerts`, Latency `alerts`, *Alerts raised* drops. Sometimes (~42–61 %): entity Exceptions `alerts` |
| `tenant-degraded` | single entity | only tenant `T-003`: ingest, risk scoring and status lookups ×7 latency, +25 % timeouts on risk scoring | always (239/240 = 99.6 %, 20-, 30- and 60-min runs): entity *Median latency* `screening` for `T-003`, usually (~84–93 %) 7–14 min after the start (~94–98 % within 20 min, up to ~30 min); no other customer fires (0/240). Usually (~74–78 %) also the product-level Latency P95 `screening` (T-003 is ≈ 8 % of the traffic). Rarely (~16–18 %) entity Exceptions for `T-003` |
| `idv-provider-degraded` | partner degraded | `idv-provider` fails 60 %, ×5 latency; only 15 % of `POST /api/onboarding/approve` still arrive (applicants stuck in ID verification) | always (48/48): *Clients approved* drops. Usually (~92–96 %): Latency `onboarding`. Usually (~83–96 %): Exceptions `onboarding` (32/33 of the runs started between 06:45 and 22:35 UTC; the misses are mostly night and early-morning runs) |
| `screening-volume-drop` | business drop | ingest and risk scoring traffic × 0.3, no errors | always (48/48): only *Transactions screened* (Business) fires (another KPI fired once in 28 resets: Latency P95 `screening`, sweep C) |
| `month-end-regulatory-peak` | legitimate peak | all traffic × 2.2, nothing broken | **nothing alerts** on any KPI (0 triggers in 96 peaks, 48 × 90 min historical + 48 × 60 min live, at every hour of every weekday), during the peak or in the 2 h after it ends |

Historical incidents seeded by `POST /api/reset` (hours before the reset): `scoring-model-deploy` 68 h (40 min),
`sanctions-provider-outage` 50 h (45 min), `month-end-regulatory-peak` 36 h (90 min, the false-alarm check),
`tenant-degraded` 22 h (60 min), `alert-queue-backlog` 8 h (40 min).

Corpus (`corpus/<Domain>/`, 12 synthetic RCAs, dated 2025-06 … 2026-10): Screening (7), Alerting (2), Cases (1),
Onboarding (2). Each names its `Scenario`, stages, partner and exceptions, so the Memory Bank returns a matching
precedent for every scenario above.

## 15-minute demo script

Prerequisites: demo stack up and reset (`Portal/demo/scripts/demo-up.ps1`, `demo-reset.ps1`), portal at
`http://localhost:5090`, active company **Kinectify (demo)** (Settings → Company profiles → Make active).

1. **(0–2 min) The company.** Settings → Company profiles → `kinectify`: labels *Product / Workflow / Stage /
   Customer*, four products, partners (sanctions, identity verification, blockchain analytics, OCR) and the
   **Privacy** block. Point out: same engine as Arrivia, different vocabulary, no code change.
2. **(2–4 min) History.** Daemons / KPI anomalies for the last 3 days: the seeded incidents appear —
   model deploy (68 h, Latency *screening*), sanctions outage (50 h, Latency + Exceptions *screening*, *Transactions screened*,
   usually (~90–96 %) also Exceptions *onboarding*), tenant T-003 (22 h, entity *Median latency* for `T-003` only; the
   product-level P95 usually (~74–78 %) moves too), alert backlog
   (8 h, Exceptions + Latency *alerts*, *Alerts raised*). Show that the month-end peak (36 h, traffic × 2.2 for
   90 min) produced **no** anomaly on any KPI, not even when traffic fell back to normal: the platform tells a
   legitimate peak from an incident.
3. **(4–8 min) Live partner outage.**
   `Portal/demo/scripts/demo-scenario.ps1 -Company aml -Scenario sanctions-provider-outage -Minutes 20`.
   Within 2–3 buckets (5 min each, evaluated every minute) Latency and Exceptions fire on *screening* and
   *Transactions screened* drops; Exceptions on *onboarding* usually fires too (~90–96 % of the runs; the misses are at
   night, when onboarding has only a few requests per bucket). Open the Inspector: it should say that **stage 02 Risk scoring
   and stage 03 Sanctions check fail because partner `sanctions-provider` (api.sanctionlists.demo) returns
   502/timeouts**, affecting all customers, and cite *rca-01 Sanctions list provider outage* and *rca-12* from the
   Memory Bank (fallback to list snapshot, circuit breaker).
4. **(8–10 min) Privacy.** In the evidence sent to the model, exception messages such as
   `… (account 000098765432)` or `… jane.doe@example.test` appear **redacted** (privacy `patterns`), because
   `allowRawEvidence` is `false`. Compare with the raw message in the App Insights query.
5. **(10–13 min) One customer.** Stop the scenario (`demo-scenario.ps1 -Stop`) and start
   `-Scenario tenant-degraded -Minutes 20`.    Among the customers only `T-003` fires (entity-grain
      *Median latency* on *screening*, 2 buckets: usually (~84–93 %) 7–14 min after the start, ~94–98 % within 20 min, up to ~30 min); no other customer series moves. The
      product-level *screening* P95 usually (~74–78 %) fires as well (T-003 is ≈ 8 % of the traffic) but does not say who is
      affected; the per-customer series does. The Inspector should say *"In Transaction Screening, customer T-003 is
      slow at stage 02 Risk scoring (and ingest)"* and cite *rca-05* (bulk load + rule set, per-tenant rate limit).
6. **(13–15 min) Business signal.** Explain `screening-volume-drop` (or start it if time allows): no errors at all,
   only the Business KPI *Transactions screened* catches the drop (precedent *rca-06*, connector credential
   rotation). Close with the month-end peak: start `month-end-regulatory-peak` (60 min by default) and show that
   nothing alerts, neither during the peak nor when it ends.

Stop everything with `demo-scenario.ps1 -Stop` before handing over.

## Notes and limits

- The generator writes the entity id in `CustomerTenantId` only; it has no archetype field to emit the
  `piiDimensions` as custom dimensions. Redaction is therefore demonstrated with the fake account numbers,
  e-mails and wallet addresses embedded in exception messages (all obviously synthetic: `0000…`, `example.test`).
- **KPI tuning (verified with the real platform evaluator).** The numbers in the scenario table come from a replay
  that feeds the generator's in-memory reference model (8 days of `laptop` backfill + 30 h of live feed, every
  scenario started live after the reset, `tenant-degraded` for 20 and 30 min) into the platform's own
  `KpiEvaluator` (replay console), at 48 deterministic reset times covering every hour and all 7 weekdays.
  Sweep A (24, one per hour; the pack test's sweep): 2026-11-05 00:00, 11-08 01:13, 11-11 02:26, 11-07 03:39,
  11-10 04:52, 11-06 05:05, 11-09 06:18, 11-05 07:31, 11-08 08:44, 11-11 09:57, 11-07 10:10, 11-10 11:23,
  11-06 12:36, 11-09 13:49, 11-05 14:02, 11-08 15:15, 11-11 16:28, 11-07 17:41, 11-10 18:54, 11-06 19:07,
  11-09 20:20, 11-05 21:33, 11-08 22:46 and 11-11 23:59 UTC. Sweep B (24): hour *i*, day 2026-11-06 + (5*i* mod 7),
  minute (29*i* + 7) mod 60. The scenario table's "always" counts are A + B (on sweep A alone every "always" detection fires in every run);
  each qualified rate is the range observed across A, B and an independent sweep C (28 resets: 2026-11-04 05:00 UTC +
  7 *i* h, minute (41 + 11 *i*) mod 60), since a single sweep moves those rates by several points. Series are built the way the platform stores them:
  domain latency P95 from `KpiSampling.SamplesPerBucket` random rows per bucket (`sample {{perBucket}}` in
  `kpi-latency.kql.template.txt`, then `KpiBucketAssembler` + `KpiBucketStatistics.Build`, PERCENTILE.INC);
  entity latency from Kusto `percentile()` over every row of the tenant, top `maxEntitiesPerBucket` tenants.
  Result: 0 triggers in the 96 peaks (+2 h), at most 1 false anomaly a day per KPI outside the incidents.
  `AmlPack_ReplayDetectsScenariosAndIgnoresPeak` runs the same replay with a copy of the evaluator (the demo tests
  do not reference the platform; 0 mismatches against the real evaluator) at the 24 resets of sweep A and asserts
  exactly the "always" detections above in every run, `T-003` within 30 minutes, only *Transactions screened*
  during `screening-volume-drop`, no other customer during `tenant-degraded`, 0 triggers around the peak and ≤ 2 false anomalies a day.
  The choices behind it:
  - Business KPIs use **BaselinePercent** (same 5-minute slot of the previous 7 days, `Decrease` > 50 %): the
    return to normal after the peak is not a "drop", because the reference is last week's slot, not the peak.
    *Clients approved* (≈ 4–15 per bucket) needs 3 consecutive buckets.
  - Exceptions are sparse (≲ 1 per bucket outside *screening*), so a robust z-score would have MAD = 0 and never
    be evaluated. Exceptions KPIs use **StaticThreshold** on the count: `screening` > 8, `alerts` > 2, `onboarding`
    > 2, `cases` > 4, entity grain > 3, all with 2 consecutive buckets — far above the peak's doubled baseline.
  - Domain Latency KPIs use **BaselinePercent** on P95 with `sampleSize` 100 000 (348 samples per 5-minute bucket
    over the 1-day lookback: every request outside the busiest hours). With 4 000 the P95 would come from 14
    samples and fire ≈ 5 times a day and on the peak. `screening` +50 % (≈ 120–360 requests per bucket, 2 buckets),
    `alerts` / `cases` / `onboarding` +200 % (only 15–50 requests per bucket; `cases` and `alerts` need 3 buckets).
  - Entity **Latency** (P95, required by the pack contract) needs `minObservationsPerBucket` 30 on *screening*
    (+250 %, 3 buckets) and 20 elsewhere (+300 %): with fewer rows Kusto's P95 is the tenant's slowest request, and
    a small tenant whose volume doubles in the peak looks "slower". It therefore only watches the largest customers.
  - Entity **Median latency** (`screening`, P50, +300 %, 2 buckets, ≥ 5 requests, top 8 customers per bucket) is
    the per-customer detector: a median does not grow with volume, so the peak does not move it, while a slow
    tenant moves it ×7. `maxEntitiesPerBucket` 8 keeps the tiny tenants (1–2 requests per bucket) out of the
    evaluation; `T-003` (≈ 8 % of the screening traffic) is always among them.
- Business `kqlText` zero-fills (`range` + `leftouter` join), so a total stop is a bucket with 0, not a missing one.
- The thresholds are tuned for `laptop`. Exception thresholds are absolute counts, so at `showcase` (× 4) they
  would need to be raised about 4x. BaselinePercent needs at least 3 days of same-slot history,
  which the 8-day backfill provides. At `tiny` (1 %) most buckets are below `minObservationsPerBucket` and the
  counts are too small to detect anything; use it for smoke tests only.
- Volume per 5-minute bucket is modest outside *screening* (alerts/cases/onboarding ≈ 15–50 requests). The backend
  has no browser pages, so `pageViews` stays empty.
