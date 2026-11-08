# Root Cause Analysis - Cruise Core Rooms & Rates slowdown (SQL pool saturation during a rate import)

**Date:** 2026-06-03
**Company:** Arrivia (demo) - synthetic document written for the demo stack; not a real incident
**Scenario (demo):** `cruise-pool-exhaustion`
**Vertical:** Cruises - **App:** cruise-core - **Stages:** 04 Rooms & Rates (`GET /cruisecategory.do`), 05 Total Price (`POST /cruisecabin.do`)
**Partners involved:** cruise-core-db
**Severity:** Sev 3 - **Duration:** 38 minutes

## Bottom Line Up Front
A nightly category-rate import was rescheduled into business hours and ran on the same connection pool as the member-facing site. The import held long transactions on `category_rates`; 04 Rooms & Rates and 05 Total Price queued for connections and surfaced pool-timeout exceptions. Moving the import back to its maintenance window and giving it its own pool resolved the issue.

## Impact
- Rooms & Rates latency P95 rose from about 3.5 s to more than 20 s; Total Price P95 above 60 s.
- 6 % of Rooms & Rates requests failed with `System.InvalidOperationException` (pool timeout) from CategoryRates.Load.
- No data loss. Cruise bookings dipped but recovered within the hour.

## Timeline (UTC)
- 14:10 Rate import starts (schedule change applied the previous day).
- 14:22 Exceptions KPI for Cruises (domain grain) fires on CategoryRates.Load.
- 14:29 Latency P95 for Cruises breaches its 7-day baseline.
- 14:41 DBA identifies the import session holding locks and connections.
- 14:48 Import paused; latency recovers by 14:55.

## What Happened
The import and the site use the same Cruise Core login and therefore the same pool. The import opened up to 12 parallel workers, each holding a connection inside a long transaction, leaving only a handful of connections for member traffic.

## Root Cause
Batch and interactive workloads shared one connection pool and one login, and the scheduling change was made without checking that dependency.

## 5 Whys
1. Why did Rooms & Rates slow down? Because SELECT category_rates waited for connections and locks.
2. Why were connections scarce? Because the rate import held 12 of them in long transactions.
3. Why did the import run at 14:10? Because its schedule was changed to finish before an early-morning deploy.
4. Why did the schedule change affect members? Because the import used the same pool and login as the site.
5. Why was that not known? Because the workload separation was never documented for Cruise Core.

## Corrective Actions
- Dedicated login and pool for batch jobs on Cruise Core.
- Batch schedule changes require a check against the member traffic curve.
- Alert on pool wait time in addition to request latency.

## Lessons Learned
The same symptom as a mis-sized pool (pool-timeout exceptions on Total Price and Rooms & Rates) can come from a competing workload. Check what else holds connections before resizing the pool.
