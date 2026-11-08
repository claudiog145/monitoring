# Root Cause Analysis - Cruise Core Total Price over 100 seconds (connection pool exhausted)

**Date:** 2026-02-18
**Company:** Arrivia (demo) - synthetic document written for the demo stack; not a real incident
**Scenario (demo):** `cruise-pool-exhaustion`
**Vertical:** Cruises - **App:** cruise-core - **Stage:** 05 Total Price (`POST /cruisecabin.do`)
**Partners involved:** cruise-core-db (Cruise Core SQL database), cruise-line-gds
**Severity:** Sev 2 - **Duration:** 52 minutes

## Bottom Line Up Front
A configuration release fixed the Cruise Core SQL connection pool at exactly 20 connections (minimum = maximum) with a 5 second checkout timeout. When the evening search wave arrived, the pool was permanently full: 05 Total Price (`POST /cruisecabin.do`) took more than 100 seconds per request and more than half of the quotes failed with `System.InvalidOperationException` ("Timeout expired ... obtaining a connection from the pool"). Restoring the previous elastic pool settings ended the incident.

## Impact
- Members of every club could not see a total price for a cabin; most abandoned the cruise funnel at 05 Total Price.
- 04 Rooms & Rates (`GET /cruisecategory.do`) and 06 Cabin Selection (`POST /cruisecabinonhold.do`) slowed down five to six times because they share the same pool.
- CruiseBooked events fell to roughly a fifth of the normal evening rate for the duration.

## Timeline (UTC)
- 19:02 Configuration release of Cruise Core completes; pool settings change from elastic to fixed 20/20.
- 19:31 Latency P95 KPI for the Cruises vertical breaches its baseline; Total Price P95 above 100 s.
- 19:31 Exceptions KPI for Cruises fires at the same time: pool-timeout `InvalidOperationException` on TotalPrice.Quote.
- 19:36 Cruise bookings business KPI fires (Decrease).
- 19:55 On-call correlates the start with the release window and the pool counters.
- 20:16 Previous pool configuration redeployed; latency back to baseline within 7 minutes.

## What Happened
Total Price runs a pricing stored procedure (`EXEC price_quote`) and then asks the cruise line GDS for a quote. The procedure holds its connection while the GDS call is in flight, so each request keeps a connection for the whole quote. With a fixed pool of 20 and a 5 s checkout timeout, requests queued behind the pool, retried, and the retries made the queue longer.

## Root Cause
The pool size was copied from a test environment profile into the production configuration during a clean-up of connection strings. Nothing validated pool size against the expected concurrency of the pricing path.

## 5 Whys
1. Why did 05 Total Price take more than 100 seconds? Because every request waited for a free SQL connection.
2. Why were there no free connections? Because the pool was capped at 20 and each quote held a connection during the GDS call.
3. Why was the pool capped at 20? Because the release set MinPoolSize = MaxPoolSize = 20 from a test profile.
4. Why did the test value reach production? Because connection string settings were not part of the release review checklist.
5. Why was it not caught before the evening wave? Because load in the first 30 minutes after the release was too low to saturate the pool.

## Corrective Actions
- Release the connection before calling the cruise line GDS in TotalPrice.Quote.
- Add pool size and checkout timeout to the configuration review checklist for Cruise Core.
- Keep the 5-minute Latency P95 KPI on the Cruises vertical; add a runbook link to this RCA.

## Lessons Learned
A pool that is "large enough" in a test environment can be the bottleneck of the slowest stage in production. Latency that grows by two orders of magnitude on one stage while others only slow down points at a shared resource, not at the stage code.
