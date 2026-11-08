# Banco Andino (demo) — company pack `bank`

Fictional retail bank of the Andean region (profile `banco`). **Everything in this pack is synthetic**: no real
customer, account, branch or partner data. Partner names (Red Cóndor, CompensAndes, VigíaFraude, Mensajería Andes,
RecaudaNet) are invented.

| File | What it is |
|---|---|
| `archetype.json` | Apps, branches, traffic curve, steps, partners, 8 scenarios and 5 historical incidents (DemoGenerator). |
| `profile.json` | CompanyProfile `banco`: 4 services (canales), steps, `BranchId` identity, 6 partners, privacy. |
| `sources.json` | 4 telemetry sources `bank-*` → appIds `demo-bank-*`. |
| `kpis.json` | 24 KPIs (5-min buckets, 1-min evaluation): Latency P95 / Exceptions per service (domain + entity), PartnerFailures per service, 4 business `kql:` KPIs. |
| `corpus/<service>/*.md` | 12 synthetic RCAs in the corporate format, one or two per scenario. |

## The bank in one minute

| Service (`domain`) | App / appId | Host | Steps (funnel) |
|---|---|---|---|
| `banca-web` — Banca por Internet | `web-banking` / `demo-bank-web-banking` | `www.bancoandino.demo` | login → saldos → movimientos → transferencia → pago de servicios → comprobante |
| `banca-movil` — App móvil | `mobile-api` / `demo-bank-mobile-api` | `api.bancoandino.demo` | login → saldo → transferencia; tarjetas: consulta, bloqueo |
| `pagos` — Pagos | `payments-svc` / `demo-bank-payments-svc` | `pagos.bancoandino.internal` | ACH interbancaria, pago de servicios, autorización de tarjeta |
| `core` — Core bancario | `core-gateway` / `demo-bank-core-gateway` | `core-gw.bancoandino.internal` | transferencia, saldos, débito (CICS/MQ to the mainframe) |

- **Entity:** branch (`BranchId`, `B-001`…`B-120`, Zipf 1.1: B-001 ≈ 23 % of the traffic, B-003 ≈ 7 %). The channel is
  the service itself (web vs mobile).
- **Partners:** `core-banking` (mainframe via core-gateway), `card-network` (Red Cóndor), `ach-clearing`
  (CompensAndes), `antifraud` (VigíaFraude), `otp-sms` (Mensajería Andes), `biller-hub` (RecaudaNet).
- **Traffic:** 160 000 requests/weekday at `laptop` scale, weekend × 0.75. The hourly curve is in UTC and models
  Andean local time (UTC-5): quiet nights (00:00-08:00 local carry ~9 % of the day), lunch peak 12-13 h and
  evening peak 18-19 h local.
- **Shape:** p95 in the hundreds of ms (balances ~0.5 s, transfers ~1.3 s, ACH ~2.3 s); error rate 0.3-0.6 % per step
  plus partner failures (≈ 0.6-0.8 % per service); business events `LoginSucceeded`, `TransferCompleted`, `BillPaid`,
  `CardBlocked`, `CardAuthorized`, `InterbankTransferSubmitted` (emitted for every attempt, success or not).

## Scenarios

> **Reset window: run `/api/reset` (`demo-reset.ps1`) between 09:00 and 20:30 local time (UTC-5, i.e. 14:00-01:30
> UTC), and start live scenarios right after it.** Every promise below is measured over the resets inside that window.

"Detected" below means: fired, while the incident runs or in the 15 minutes after it ends, in **at least 95 %** of the
replays of a reset inside the window, in each of three independent sweeps (historical and live incidents; see
*Notes*). For these signals the first anomaly comes 10 minutes after the start in the median replay (5-min buckets,
2 consecutive anomalous buckets), at most ~30 minutes for the service, partner and business KPIs and up to ~55
minutes for the branch (entity) KPIs. Nothing else is promised: the last column says how often each extra signal
showed up inside the window ("~NN–MM %" = the lowest and highest rate of the three sweeps).

| Key | What breaks | Detected (≥ 95 % of the replays) | Sometimes also (lowest–highest rate of the three sweeps) |
|---|---|---|---|
| `core-gw-slow-deploy` | core-gateway 4.18: every `core-banking` call ×5, core transfers 25 % `TimeoutException` | Latency P95 domain of `core`, `banca-web`, `banca-movil`, `pagos`; Exceptions `core` (domain); entity Latency P95 of the large branches on `banca-web`, `banca-movil`, `core` | entity Latency `pagos` or entity Exceptions `core` of some branch (~0–10 % each) |
| `mobile-transfer-exceptions` | release 7.4: 60 % of `POST /api/mobile/v2/transfers` throw `NullReferenceException` | Exceptions `banca-movil` (domain) | entity Exceptions `banca-movil` of a large branch (~40–60 %) or of B-003 (~20–40 %); *Transferencias completadas* (~15–25 %) |
| `ach-outage` | `ach-clearing` rejects 85 % and is ×3 slower | PartnerFailures `pagos` (`ach-clearing`) → Triage; Exceptions `pagos` (domain); *Rechazo interbancario (%)* > 25 % | Latency P95 `pagos` domain (~90–100 %); entity Exceptions `pagos` of the large branches (~75–85 %) and of B-003 (~15–25 %) |
| `branch-degraded` | only branch **B-003**: latency ×6 and 25 % `TimeoutException` on every channel | Latency P95 **entity B-003** on `banca-web` and `banca-movil`; Exceptions **entity B-003** on `banca-web`; no other branch fires (0 of 210 incidents, all resets of the three sweeps) | entity B-003: Exceptions `banca-movil` (~95–100 %), Exceptions `core` (~75–85 %), Latency `core` (~60–85 %), Exceptions `pagos` (~45–55 %), Latency `pagos` (~25–35 %). Domain grain: **Exceptions `core` (~20–35 %)**, which reaches Triage as a `core` service signal with no branch; Latency or Exceptions domain of another service (~0–10 % each) |
| `card-network-degraded` | `card-network` fails 50 %, ×4 slower | PartnerFailures `banca-movil` (`card-network`) → Triage; Exceptions `banca-movil` (domain) | entity Exceptions `banca-movil` of a large branch (~20–35 %) or of B-003 (~10–20 %) |
| `otp-delays` | `otp-sms` ×8 slower and 70 % failures | PartnerFailures `banca-web` and `banca-movil` (`otp-sms`) → Triage; Latency P95 and Exceptions (domain) of `banca-web` and `banca-movil`; *Logins exitosos* (Decrease) | entity Latency and Exceptions `banca-movil` of the large branches (~95–100 %); entity Exceptions (~75–85 %) and Latency (~70–85 %) `banca-web` of the large branches; entity B-003: Exceptions `banca-movil` (~75–95 %), Latency `banca-movil` (~55–70 %), Exceptions `banca-web` (~45–70 %), Latency `banca-web` (~15–35 %) |
| `transfers-drop` | **business only**: transfer requests −80 % mobile / −70 % web, no error, no latency | Only the business KPI *Transferencias completadas* (Decrease); nothing else fires, after a reset at any hour (105 of 105) | — |
| `payday-peak` | **legitimate**: traffic ×1.5 everywhere, nothing broken | **No KPI fires**, on any KPI or entity, during the peak or in the 2 hours after it — after a reset at any hour (210 of 210 peaks) | — |

Historical incidents seeded by `/api/reset` (relative to the reset time, so they land at the reset's hour of the
day): `payday-peak` 72 h ago (4 h), `core-gw-slow-deploy` 49 h ago (45 min), `branch-degraded` 47 h ago (50 min),
`ach-outage` 25 h ago (40 min), `otp-delays` 23 h ago (25 min). After a reset inside the window (09:00-20:30 local)
they start between 08:00 and 21:30 local and show the detections above. After a reset outside it they happen in the
quiet hours too, where the volume is often below `minObservationsPerBucket` (see *Limits*).

## Business KPIs (`kql:` over `customEvents`)

Each query is zero-filled (a `range` of buckets left-joined to the events): a bucket with no target event is
evaluated as 0 instead of disappearing. `TotalCount` is the other business events of the same sources (the
activity), so `minObservationsPerBucket` skips quiet buckets, and `Value` is the target count.

| KPI | Query key | Method |
|---|---|---|
| Transferencias completadas (`banca-movil`) | `kpi:bank-transfers-completed` | `TransferCompleted` web + mobile; BaselinePercent same slot of the previous 7 days, Decrease 50 %, ≥ 60 activity events |
| Rechazo interbancario (%) (`pagos`) | `kpi:bank-interbank-rejection-rate` | % of `InterbankTransferSubmitted` whose ACH request failed; StaticThreshold 25 %, ≥ 5 submissions (baseline ≈ 1 %) |
| Pagos de servicios (`pagos`) | `kpi:bank-bills-paid` | `BillPaid` web + payments-svc; BaselinePercent same slot, Decrease 50 %, ≥ 100 activity events |
| Logins exitosos (`banca-web`) | `kpi:bank-logins-succeeded` | `LoginSucceeded` web + mobile; BaselinePercent same slot, Decrease 50 %, ≥ 30 activity events |

The same-slot baseline is why the payday peak does not alert when it ends: a robust z over the last 2 hours would
take the end of a 3-4 h peak for a drop.

## 15-minute demo script

Before the demo: stack up with `Portal/demo/scripts/demo-up.ps1`, data loaded with `demo-reset.ps1` (8 days,
`laptop`) **between 09:00 and 20:30 local (UTC-5)**, and `demo-check.ps1` green. Run the live part right after the
reset: at night the bank has too little traffic for the latency, exception and business KPIs. Open the demo portal
at `http://localhost:5090` (DEMO banner visible) and select **Banco Andino (demo)** in the company selector.

1. **(0:00-2:00) The bank.** Settings → Company profile `banco`: canales (services), operaciones (flows), pasos,
   sucursal (`BranchId`) and the six partners. Say: four apps, one fictional bank, all data synthetic.
2. **(2:00-4:00) History speaks.** Daemons / KPI anomalies of the last 3 days: show the seeded incidents —
   core-gateway slowdown (49 h ago: latency on every channel), branch B-003 (47 h ago), ACH outage (25 h ago) and
   OTP delays (23 h ago). Point at the payday peak 72 h ago: traffic +50 % for 4 hours and **no anomaly** — the KPIs
   watch latency, rates and same-hour-of-day business volumes, not raw volume.
3. **(4:00-5:00) Trigger the live incident.**
   `Portal/demo/scripts/demo-scenario.ps1 -Company bank -Scenario ach-outage -Minutes 20`
   (or `POST http://localhost:8087/api/scenarios/bank/ach-outage/start` with `{"durationMinutes":20}`).
4. **(5:00-9:00) Detection.** Within 10-15 minutes: PartnerFailures of `pagos` for partner `ach-clearing`,
   Exceptions of `pagos` and the business KPI *Rechazo interbancario (%)* above 25 %. Open the Triage signal:
   stage `partner:ach-clearing`, step *Transferencia interbancaria (ACH)*, exceptions
   `System.Net.Http.HttpRequestException` on `POST /payments/v1/ach/transfers`.
5. **(9:00-12:00) Inspector + Memory Bank.** Ask the Inspector what is happening. It should say: the ACH clearing
   house (CompensAndes) is rejecting most interbank credit transfers; internal transfers, balances and cards are
   healthy; all branches are affected; and it should cite the synthetic RCAs *ACH clearing house outage* (2026-07-31)
   and *ACH mutual-TLS certificate expired* (2025-12-15) with their corrective actions (store-and-forward queue,
   certificate monitoring).
6. **(12:00-14:00) One branch only.** Stop the ACH scenario (`demo-scenario.ps1 -Company bank -Stop`) and show the
   historical `branch-degraded` incident on the KPI pages: entity-grain Latency P95 (`banca-web`, `banca-movil`) and
   Exceptions (`banca-web`) isolate **B-003**; no other branch fires. Entity anomalies stay on the KPI pages (only
   partner anomalies of entity grain reach Triage). In about a fifth to a third of the replays (~20–35 %) the
   branch also pushes the `core` service's Exceptions (domain) over its threshold: that one reaches Triage as a `core`
   signal without a branch. The Inspector should name the branch and cite the B-003 RCAs (regional link, teller sync
   job).
7. **(14:00-15:00) Silent business failure (optional).** Start `transfers-drop`: no exception, no latency change —
   only *Transferencias completadas* (Decrease) fires. Close: technical health and business health in one place.

Alternative live scenarios: `core-gw-slow-deploy` (latency on every channel, the Inspector should point at the
core-banking partner and the 4.18 deployment RCA), `mobile-transfer-exceptions` (one step, one new exception type),
`card-network-degraded`, `otp-delays`. Never present `payday-peak` as an incident: it is the control case.

## Limits

- At `tiny` scale (E2E) volumes are 1 % of `laptop`: most 5-minute buckets have fewer observations than the KPIs
  require (`minObservationsPerBucket` 50 for domain latency), so detection is only meaningful at `laptop`.
- Outside the reset window (night and early morning, 20:30-09:00 local) the volume is often below
  `minObservationsPerBucket` for latency, exception and business KPIs. A scenario started then (or a historical
  incident seeded then by such a reset) is detected in only ~20–80 % of those replays depending on the KPI and the
  sweep (30 resets of the three sweeps, see *Notes*); the `otp-sms` partner KPIs (~80–100 %) and, for `otp-delays`,
  Exceptions `banca-movil` (~90–100 %) keep firing. No KPI raises a false alarm at night either (0 measured).
- The generator has no calendar: month-end and payday are represented by the `payday-peak` scenario, not by dates.
- Entity-grain anomalies (branch) are shown on the KPI pages; only partner anomalies of entity grain reach Triage.
  Domain-grain anomalies reach Triage as a service signal (no branch): that is how `core-gw-slow-deploy`,
  `otp-delays`, `ach-outage`, `mobile-transfer-exceptions` and `card-network-degraded` show up there, and also
  `branch-degraded` in ~20–35 % of the replays (Exceptions `core` domain, see *Scenarios*).

## Notes — detection replay

The tuning above was verified by replaying the `laptop` backfill (seed 1000, 8 days, generator reference model) plus
each live scenario through the **platform's own** `KpiEvaluator` (JourneyMonitor.Core), bucket by bucket with the
history window of `GetRequiredHistory` and the evaluator state carried from the backfill into the live part, the same
per-bucket latency sample as `KpiSampling.SamplesPerBucket` (100 000 over 288 buckets = 348 requests) and the
zero-filled business queries.

**Three sweeps** (UTC-5, local times), each reset replayed the same way: the backfill with the 5 historical incidents,
then each of the 8 scenarios started live 5 minutes after the reset and evaluated until 2 h after its end. A detection
counts when the KPI fires from the incident's start to 15 minutes after its end.

1. **Sweep 1, 36 resets** (the one `DemoGenerator.Tests` replays): one every 7 hours for a week from Sun 08-Nov 00:30
   (24 resets: every hour of the day, every day of the week) plus 12 inside the window (Sun 09:00, Mon 20:15,
   Tue 09:40, Wed 20:30, Thu 10:15, Fri 20:00, Sat 09:00, Sat 15:20, Sun 17:45, Tue 19:10, Thu 14:35, Fri 11:50).
   24 inside the window.
2. **Sweep 2, 41 resets**: 4 a day on every weekday between 09:00 and 21:00 (Sun 09:05 13:25 18:10 20:55, Mon 10:20
   14:50 17:05 19:40, Tue 09:35 12:15 16:25 21:00, Wed 11:05 15:10 18:35 20:15, Thu 09:00 13:05 17:55 20:40, Fri 10:45
   14:20 19:25 20:59, Sat 11:35 16:05 18:50 20:25) plus 13 at night or early morning (22:15, 23:40, 01:05, 02:50,
   04:20, 05:45, 07:10, 08:35, 21:30, 00:10, 08:55, 03:25, 06:40 on Sun, Mon, Tue… in turn). 24 inside the window.
3. **Sweep 3, 28 resets**: 09:13 + k × 25.7 min (k = 0…27, rounded to the minute, the last one at 20:47) on the
   weekday 3k mod 7 counted from Sunday. 27 inside the window.

| Scenario | Inside the window: 75 resets (historical + live = 150 incidents; live only = 75) | Outside: 30 resets (info) |
|---|---|---|
| `core-gw-slow-deploy` | 150/150: Latency domain `core`, `banca-web`, `banca-movil`, `pagos`; Exceptions `core`; entity latency `banca-web`, `banca-movil`. 146/150 (97 %; 46/48, 46/48, 54/54 per sweep): entity latency `core` | 29-40/60 (48-67 %) |
| `mobile-transfer-exceptions` | 75/75: Exceptions `banca-movil` | 19/30 (63 %) |
| `ach-outage` | 150/150: PartnerFailures `pagos` [ach-clearing], Exceptions `pagos`, Rechazo interbancario | 38-39/60 (63-65 %) |
| `branch-degraded` | entity B-003: Latency `banca-web` 150/150, Latency `banca-movil` 150/150, Exceptions `banca-web` 149/150 (99 %; 47/48, 48/48, 54/54 per sweep); another branch: 0 of 210 incidents (all resets) | 20-25/60 (33-42 %) |
| `card-network-degraded` | 75/75: PartnerFailures `banca-movil` [card-network], Exceptions `banca-movil` | 19-23/30 (63-77 %) |
| `otp-delays` | 150/150: PartnerFailures `banca-web`/`banca-movil` [otp-sms], Latency + Exceptions domain `banca-web`/`banca-movil`, Logins exitosos | partner 55-60/60 (92-100 %); others 33-56/60 (55-93 %) |
| `transfers-drop` | 75/75: Transferencias completadas; no other KPI fires (105 of 105 resets, any hour) | 16/30 (53 %) |
| `payday-peak` | 0 anomalies on every KPI and entity from its start to 2 h after its end: 210 peaks (historical 4 h and live 3 h), all 105 resets | (included) |
| outside incidents | 0 false anomalies: 105 resets × 7 days of backfill and every live tail | (included) |

Every *Detected* promise fires in at least 95 % of the in-window incidents of each sweep on its own (lowest: 46/48 =
96 %). The "Sometimes also" ranges in *Scenarios* are the lowest and highest in-window rate of the three sweeps,
rounded outwards to 5 points and widened with the 4th sweep (65 resets from the iteration-4 tester); a few extra signals vary by up to ~25–30 points between sweeps, which is why they are given as ranges and not promised.

`DemoGenerator.Tests` keeps this honest: `BankPack_ReplayDetectsScenariosAndIgnoresPeak` replays sweep 1 with a
faithful copy of the evaluator (same per-bucket sample; it agrees with the console series by series, every rate of
sweep 1 above) and fails if a promise of the *Detected* column fires in less than 95 % of the incidents inside the
window, if a peak fires anything at any hour, if `branch-degraded` fires another branch, if `transfers-drop` fires
anything but *Transferencias completadas*, or if any KPI raises an anomaly outside the incidents (and the 2 hours
after each). `BankPack_FilesAreCleanUtf8` keeps the pack's files UTF-8 without BOM or mojibake.