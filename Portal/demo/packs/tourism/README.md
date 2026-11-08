# Arrivia (demo) - company pack `tourism`

Synthetic demo company for the Demo Telemetry Server (contract v5 §6). Everything here is invented: club ids
(`9001`-`9060`), hosts (`clubNNNN.travel-demo.example`), partners, amounts and the RCAs in `corpus/`. Nothing is
copied from production telemetry or from real RCAs.

| File | What it is | Seeded with |
|---|---|---|
| `archetype.json` | 6 apps, 60 clubs (Zipf), 36 steps, 9 scenarios, 6 historical incidents | DemoGenerator |
| `profile.json` | The Arrivia profile (`profileKey` `arrivia`): same verticals, stages, matchers and identity sources as the real one, plus the `air` vertical, the `club` alias on Hotels/Cars/Shared and 10 fictional partners | `POST /api/settings/company-profiles` |
| `sources.json` | The real Arrivia aliases `sor-frontend`, `club`, `cruise-core`, `cruise-api`, `air-ui`, `air-api` pointing at the `demo-tourism-*` apps | `POST /api/settings/telemetry-sources` |
| `kpis.json` | 26 KPIs, bucket 5 min / every minute (details below) | `PUT /api/settings/kql/{key}` + `POST /api/settings/kpis` |
| `corpus/<Domain>/*.md` | 12 synthetic RCAs, one or more per scenario | `POST /api/knowledge/import?subPath=tourism/corpus&profileKey=arrivia` |

## Apps and verticals

| Alias (= real Arrivia alias) | appId / database | Vertical(s) | Main stages |
|---|---|---|---|
| `sor-frontend` | `demo-tourism-sor-frontend` | Hotels, Cars, Shared Booking Services | hotel search / results / details / PreBook, car search / results / PreBook, login, checkout, booking, confirmation, cancellation |
| `club` | `demo-tourism-club` | Hotels, Shared Booking Services | club login, logincheck, hotel details |
| `cruise-core` | `demo-tourism-cruise-core` | Cruises | 02 Listing -> 11 Confirmation, including **05 Total Price** (`POST /cruisecabin.do`) |
| `cruise-api` | `demo-tourism-cruise-api` | Cruises | search matrix, customer id, guests, options, insurance |
| `air-ui` | `demo-tourism-air-ui` | Air | search page, results, fare rules |
| `air-api` | `demo-tourism-air-api` | Air | shopping search, price, book |

The club is `customDimensions.VacationClubId` on every row and also the host (`club9002.travel-demo.example`).
Business events: `HotelPreBooked`, `BookingCompleted`, `CruiseBooked`, `FlightBooked` (with `Amount`).

## KPIs

All KPIs use 5-minute buckets evaluated every minute, a 30-minute cooldown and `profileKey` `arrivia`. Thresholds
were sized with the detection replay (see *Verification*), not guessed.

| KPI | Grain | Rule |
|---|---|---|
| Latency P95, per vertical | domain | 7-day same-slot baseline, +60 %, 3 consecutive buckets (Shared: +100 %, 4 consecutive buckets), >= 30 requests; `sampleSize` 100000 |
| Latency P95, per vertical | club | 7-day same-slot baseline, +300 %, 3 consecutive buckets, >= 20 requests of that club |
| Exceptions, per vertical | domain | Static threshold on exceptions as % of requests (`Value`): > 4 % (Air > 5 %, Cars > 10 %), 2 consecutive buckets |
| Exceptions, Hotels stage `05-prebook` | domain | Static threshold: > 30 % of PreBook requests fail, 2 consecutive buckets |
| Exceptions, per vertical | club | Static threshold on the club's exception count (`Count`): > 3 per bucket, 2 consecutive buckets |
| Business: Hotel and car bookings, Booking value, Cruise bookings, Flight bookings | domain | `kql:` on `customEvents`, Decrease, 7-day same-slot baseline, -50 %, 3 consecutive buckets |
| Business: Hotel pre-books | domain | Same rule: 7-day same-slot baseline, -50 %, 3 consecutive buckets |

A robust z-score on exception counts is never evaluable here (0-2 exceptions per bucket give a MAD of 0), and
on per-club P95 latency it produced ~60 false anomalies a day; both were replaced by the rules above.

The domain P95 is computed by the platform from stored samples only: `KpiSampling.SamplesPerBucket` spreads
`sampleSize` over the lookback window (1 day = 288 buckets) and the latency template keeps that many random rows
per bucket. `sampleSize` 100000 gives 348 samples per bucket (all rows of a laptop-scale bucket); the default 4000
gives 14, whose P95 is too noisy for a +60 % rule.

Club-grain latency needs 20 requests of one club in a bucket, so it is evaluated mostly for the largest clubs at
busy hours (Cars per club: only a few dozen buckets in 8 days at laptop scale); it is kept for parity with the
real profile, and no scenario below depends on it.

## Scenarios

| Key | What happens | Alerts promised (fire at >= 95 % of every replay sweep, see *Verification*) | Usually, not promised (rate over the 96 resets of the four sweeps; per-sweep range) |
|---|---|---|---|
| `cruise-pool-exhaustion` | Cruise Core SQL pool fixed at 20: 05 Total Price > 100 s and 60 % pool-timeout `InvalidOperationException`; Detail, Rooms & Rates and Cabin Selection slower or failing; payment/confirmation traffic drops | Cruises Latency P95 + Cruises Exceptions (~10-15 min) | Cruise bookings (~15 min; 92 %, 90-96 %: a 20-minute live run sometimes ends before 3 low buckets) |
| `air-search-outage` | Fare provider (`air-gds`) fails 90 % of calls; Air results page errors | Air Exceptions (~10 min) + Air Latency P95 (~15 min), Flight bookings (~15-30 min, also at night), club-grain Air Exceptions for one or more of the biggest clubs (every club is affected) | - |
| `hotel-prebook-exceptions` | After a deploy, Hotel 05 PreBook throws `UnauthorizedAccessException` (90 %) | Hotels PreBook-stage Exceptions + Hotels Exceptions (~10 min), Hotel pre-books (~15-35 min) | - |
| `club-degraded` | Only club `9002` (second-largest, ~11 % of traffic): 5x latency, 40 % timeouts on every app | Club-grain Exceptions naming **9002** on at least two verticals and no other club in that window (~10-35 min; 98 %) | Which verticals name 9002 varies: Cruises 97 % (96-98 %), Hotels 93 % (90-96 %), Shared 91 % (88-94 %), Air 81 % (79-81 %). Domain Exceptions KPIs also cross their thresholds (Cruises 99 % (98-100 %), Shared 98 % (96-100 %), Air 85 % (83-88 %)) without naming the club |
| `partner-bookings-failing` | Distribution partner (`booking-partner`) fails 85 % of reservations | Shared Exceptions (~10 min), Hotel and car bookings + Booking value (~10-20 min) | - |
| `payment-declines` | Payment gateway fails 50 % of authorizations in every vertical | Shared Exceptions (~10-15 min) | - |
| `silent-bookings-drop` | Booking steps lose 85 % of traffic, nothing fails | The four booking Business KPIs (~10-35 min) and **no** Application KPI | - |
| `cluster-node-loss` | Cruise API up to 15x slower with `FabricTransientException` (30 %) | Cruises Exceptions (~10 min) + Cruises Latency P95 (~15-25 min) | - |
| `holiday-sale-peak` | Traffic x2 everywhere, rates and latency flat | **Nothing** (0 triggers on every KPI and club from the start of the peak to 2 h after it, in 119 of 120 replayed runs; known rare case: one *Flight bookings* drop 95 min after a live peak ended, reset 2026-11-13T01:50Z) | - |

Historical incidents seeded by every reset (hours before the reset): `club-degraded` 9 h, `air-search-outage` 20 h,
`hotel-prebook-exceptions` 31 h, `partner-bookings-failing` 44 h, `cruise-pool-exhaustion` 54 h, `holiday-sale-peak` 66 h.

## 15-minute demo script

1. **(1 min) Context.** Open the demo portal (`http://localhost:5090`): the red DEMO banner is on top; pick
   **Arrivia (demo)** in the company selector. Say that every number is synthetic but the profile is Arrivia's:
   the same verticals, stages and club identity the real platform uses, plus Air.
2. **(2 min) History.** Open the Daemons / KPI view: the last three days already contain incidents. Show the
   `cruise-pool-exhaustion` incident (~54 h ago): Cruises Latency P95 jumped two orders of magnitude on
   05 Total Price, Cruises Exceptions and Cruise bookings fired with it. Open the Triage signal and the Inspector
   answer: it should cite the RCA *Cruise Core Total Price over 100 seconds (connection pool exhausted)* from the
   Memory Bank. **Precondition (DT5):** a daemon that starts with no state evaluates only the latest bucket, so
   historical anomalies and signals exist only if the DT5 reset replays (or seeds) the history through the
   daemons; check it with `demo-check.ps1` before the demo, otherwise skip to step 4.
3. **(1 min) The peak that did not alert.** Point at `holiday-sale-peak` (~66 h ago): traffic doubled and no KPI
   fired (same DT5 precondition). The Inspector, if asked, should call it a legitimate peak (RCA *Holiday sale peak*).
4. **(4 min) Live incident.** Run
   `Portal/demo/scripts/demo-scenario.ps1 -Company tourism -Scenario cruise-pool-exhaustion -Minutes 20`.
   About 10-15 minutes after the start (5-minute buckets, evaluated every minute) the Cruises Exceptions KPI
   (2 buckets above 4 %) and the Cruises Latency P95 KPI (3 buckets above +60 % of the 7-day same-slot baseline)
   fire; Cruise bookings usually follows at ~15-25 min (3 buckets below half of its baseline; ~92-98 % of the replays
      sweeps, so do not promise it). Expected Inspector verdict:
   Cruise Core connection pool exhaustion on 05 Total Price, all clubs, shared SQL pool, recommended action
   "restore pool settings / release the connection before the GDS call".
5. **(3 min) One club.** Stop it (`-Stop`) and start `club-degraded`. After ~10-35 min the club-grain Exceptions
   KPIs name **club 9002** (`club9002.travel-demo.example`) on at least two verticals (which ones varies). The domain Exceptions KPIs fire
   too (9002 is ~11 % of traffic) but cannot say who is affected. Expected Inspector: one
   club affected, check per-club configuration (RCA *Single club site degraded*).
6. **(3 min) Business without errors.** Start `silent-bookings-drop -Minutes 40`; it needs ~10-35 min to fire,
   so take questions or show the Memory Bank meanwhile: no exceptions, normal latency, but the Business KPIs
   *Hotel and car bookings*, *Booking value*, *Cruise bookings* and *Flight bookings* go down and no Application
   KPI fires. Expected Inspector: funnel conversion problem, not a technical failure (RCA *Bookings dropped with
   no errors*).
7. **(1 min) Wrap-up.** `demo-scenario.ps1 -Stop`. Recap: same platform, same profile shape as production,
   detection in minutes, precedent-based diagnosis.

Other scenarios for Q&A: `air-search-outage` (partner outage), `hotel-prebook-exceptions` (exception storm after
a deploy), `partner-bookings-failing`, `payment-declines`, `cluster-node-loss`.

## Verification (detection replay)

**Promise rule.** A detection is listed as promised only if it fires at >= 95 % of every replay sweep, each
replayed through the platform's own `KpiEvaluator` (throwaway console, live-daemon semantics: same-slot history,
consecutive count, cooldown, minimum observations; domain P95 from `SamplesPerBucket` random samples; business
buckets zero-filled like the KQL). Four sweeps, 96 distinct reset times in all:

- 26 resets every 7 h 05 min from Saturday 2026-11-07 02:25Z (24 resets, one in each hour of the day, over 6.8
  days) plus Thursday 2026-11-12 01:15Z and Friday 2026-11-13 23:40Z, whose historical air outage falls at
  05:15Z / 03:40Z (night trough);
- three independent sweeps of 24 resets every 7 h from Saturday 2026-11-07 00:50Z, 04:20Z and 02:35Z (every hour
  of the day, 7 days each; two resets are shared by two sweeps).

At each reset the six historical incidents are evaluated, and every scenario is also started live 15 min after the
reset (evaluated with a fresh state from 6 h before its start; holiday peak for 90 min): 192 runs for the
historical scenarios, 96 for `payment-declines` (30 min), `silent-bookings-drop` (40 min) and `cluster-node-loss`
(30 min). Anything below 95 % in any sweep is in the *Usually* column with its rate over the 96 resets and its
per-sweep range, and only if every sweep stays within 5 points of that rate. Side effects whose rate moves more
than that between sweeps are not listed as *Usually* (nor stated in the RCAs): `payment-declines` on Air / Cruises Exceptions, Hotel and car
bookings and Booking value (12-96 % depending on the sweep), `partner-bookings-failing` on Shared Latency P95
(79-88 %) and `cluster-node-loss` on club-grain Cruises Exceptions (46-58 %).

| Scenario -> promised alert | Fired (four sweeps; weakest sweep) |
|---|---|
| cruise-pool-exhaustion -> Cruises Latency P95, Cruises Exceptions | 190/192 (99 %; 98 %), 192/192 |
| air-search-outage -> Air Latency P95, Air Exceptions, Air Exceptions per club, Flight bookings | 192/192 each |
| hotel-prebook-exceptions -> Hotels PreBook-stage Exceptions, Hotels Exceptions, Hotel pre-books | 192/192 each |
| club-degraded -> club-grain Exceptions name 9002 on >= 2 verticals, no other club | 188/192 (98 %; 96 %) |
| partner-bookings-failing -> Shared Exceptions, Hotel and car bookings, Booking value | 192/192 each |
| payment-declines -> Shared Exceptions | 96/96 |
| silent-bookings-drop -> the four booking Business KPIs; no Application KPI | 96/96 each; 0 Application triggers |
| cluster-node-loss -> Cruises Latency P95, Cruises Exceptions | 96/96, 96/96 |
| holiday-sale-peak -> nothing | 0 triggers at all 96 resets (historical and live, every KPI and club, until 2 h after the peak); a 5th independent sweep (24 resets from 2026-11-07T05:50Z) found 1 rare *Flight bookings* trigger 95 min after a live peak ended. Accepted and documented by the user decision of 2026-11-08. |

Shared Latency P95 needs 4 consecutive buckets: with 3, a noisy night bucket run 1 h 20 min after the historical
peak (reset 2026-11-12 22:35Z, +124 % over a +100 % threshold) fired it.

False triggers (outside every incident window): at most 0.38 per day on any KPI in every sweep.

`TourismPackTests.TourismPack_ReplayDetectsScenariosAndIgnoresPeak` replays the first sweep's 26 resets plus
2026-11-12 22:35Z in under a minute with the generator's in-memory reference model (seed 1000, scale `laptop`): the six historical incidents
before each reset and, like the sweeps, `payment-declines` (30 min), `silent-bookings-drop` (40 min) and
`cluster-node-loss` (30 min) each started live 15 min after the reset and evaluated with a fresh state from 6 h
before its start to 60 min after its end. It aggregates the telemetry per KPI the way the platform's templates do
(domain-grain latency sampling of `KpiSampling.SamplesPerBucket` + `sample {{perBucket}}`, zero-filled business
buckets), evaluates every bucket with a re-implementation of the platform's detection methods and asserts: every
promised alert above (and only those) fires at >= 95 % of the resets, `holiday-sale-peak` triggers nothing on any
KPI or club until 2 h after it ends, `silent-bookings-drop` triggers no Application KPI, and no KPI has more than
2 false triggers per evaluated day, at every reset (0 peak triggers over the 27 resets, asserted separately). In the
test every promise holds at 27/27 resets except club-degraded (26/27).

History precondition: on its first run a daemon with no state evaluates only the latest ready bucket
(`PillarHealthMonitorService.GetBucketsToEvaluateAsync`), so the historical incidents become anomalies and Triage
signals only if the DT5 reset replays or seeds that history through the daemons (demo script steps 2-3).

Business KPIs return one row per bucket, 0 when no event arrived (`range` of bins left-joined with the
`customEvents` summary): without it a total stop at night (the air outage leaves 0 `FlightBooked` per bucket)
produced no rows and was never evaluated.

Triggers per KPI and scenario in the test for the Thursday 2026-11-12 01:15Z reset (club-grain cells list the clubs;
payment, silent and cluster are the live runs; false = outside every historical incident window):

| KPI | pool | partner | prebook | air | club | peak | payment (live) | silent (live) | cluster (live) | false/day |
|---|---|---|---|---|---|---|---|---|---|---|
| Latency cruises | 2 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 1 | 0 |
| Exceptions cruises | 2 | 0 | 0 | 0 | 2 | 0 | 1 | 0 | 1 | 0 |
| Latency cruises / club | 1 [9002] | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 1 [9001] | 0 |
| Exceptions cruises / club | 4 [9001,9002,9006] | 0 | 0 | 0 | 2 [9002] | 0 | 0 | 0 | 0 | 0 |
| Latency cars | 0 | 0 | 0 | 0 | 1 | 0 | 0 | 0 | 0 | 0 |
| Exceptions cars, Latency / Exceptions cars per club | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
| Latency hotels | 0 | 0 | 0 | 0 | 1 | 0 | 0 | 0 | 0 | 0 |
| Exceptions hotels | 0 | 0 | 2 | 0 | 2 | 0 | 0 | 0 | 0 | 0 |
| Exceptions hotels `05-prebook` | 0 | 0 | 2 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
| Latency hotels / club | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
| Exceptions hotels / club | 0 | 0 | 1 [9001] | 0 | 2 [9002] | 0 | 0 | 0 | 0 | 0 |
| Latency shared | 0 | 1 | 0 | 0 | 0 | 0 | 1 | 0 | 0 | 0 |
| Exceptions shared | 0 | 1 | 0 | 0 | 1 | 0 | 1 | 0 | 0 | 0 |
| Latency shared / club | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
| Exceptions shared / club | 0 | 0 | 0 | 0 | 2 [9002] | 0 | 1 [9002] | 0 | 0 | 0 |
| Latency air | 0 | 0 | 0 | 1 | 0 | 0 | 0 | 0 | 0 | 0 |
| Exceptions air | 0 | 0 | 0 | 1 | 1 | 0 | 1 | 0 | 0 | 0 |
| Latency air / club | 0 | 0 | 0 | 0 | 1 [9002] | 0 | 0 | 0 | 0 | 0 |
| Exceptions air / club | 0 | 0 | 0 | 2 [9001,9002] | 2 [9002] | 0 | 0 | 0 | 0 | 0 |
| Hotel and car bookings | 0 | 1 | 0 | 0 | 0 | 0 | 0 | 1 | 0 | 0 |
| Booking value | 0 | 1 | 0 | 0 | 0 | 0 | 0 | 1 | 0 | 0 |
| Hotel pre-books | 0 | 0 | 1 | 0 | 0 | 0 | 0 | 0 | 0 | 0.20 |
| Cruise bookings | 2 | 0 | 0 | 0 | 0 | 0 | 0 | 2 | 0 | 0.60 |
| Flight bookings | 0 | 0 | 0 | 1 | 0 | 0 | 0 | 1 | 0 | 0 |

Known limits: the club-grain P95 uses PERCENTILE.INC while Kusto's
`percentile()` is nearest-rank, which does not change the conclusions.
## Known limits (accepted 2026-11-08)
- Rare peak side effect: in 1 of 120 replayed runs *Flight bookings* fired 95 min after a live holiday peak ended.
- Usually-rates measured on a 5th sweep: Cruise bookings with pool exhaustion 97.9 % (published ~92 %), club 9002 named on Shared 85.4 % (published ~91 %).
