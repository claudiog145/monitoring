# Root Cause Analysis - Air search outage (fare provider unavailable)

**Date:** 2026-04-22
**Company:** Arrivia (demo) - synthetic document written for the demo stack; not a real incident
**Scenario (demo):** `air-search-outage`
**Vertical:** Air - **Apps:** air-api, air-ui - **Stages:** 02 Air Search (`POST /api/air/search`), 03 Air Results (`GET /air/results`), 05 Air Price, 07 Air Booking
**Partners involved:** air-gds (air fare provider)
**Severity:** Sev 1 - **Duration:** 41 minutes

## Bottom Line Up Front
The air fare provider had a regional outage of its shopping endpoint. 90 % of `POST /shopping/offers` calls from the Air API failed, so Air search returned errors, the Air UI results page showed "no flights available" or `System.Net.Http.HttpRequestException` (503 from the Air API), and FlightBooked events nearly stopped. The provider failed over to its secondary region; no change on our side was required, but our failover took longer than it should have.

## Impact
- Air search was unavailable for about 40 minutes for all clubs.
- Air bookings dropped to near zero; Hotels, Cars and Cruises were unaffected.
- Members who retried were shown stale cached fares in a few cases.

## Timeline (UTC)
- 16:05 Dependency failures to shop.airfare-gds-demo.example start.
- 16:09 Exceptions KPI for the Air vertical fires (HttpRequestException on AirResults.Load).
- 16:12 Flight bookings business KPI fires (Decrease).
- 16:20 Provider status page confirms a regional incident.
- 16:46 Provider fails over; search recovers.

## What Happened
The Air API calls a single fare provider endpoint with a 20 s timeout and no circuit breaker. During the outage requests waited for the timeout before failing, which tripled latency and tied up threads in the Air API.

## Root Cause
External: the fare provider's primary shopping region failed. Contributing: no circuit breaker and no secondary endpoint configured on our side.

## 5 Whys
1. Why did Air search fail? Because calls to the fare provider failed or timed out.
2. Why did they fail? Because the provider's primary region was down.
3. Why did members wait so long for an error? Because each call waited for the full 20 s timeout.
4. Why was there no faster failure? Because the Air API has no circuit breaker on the provider.
5. Why was there no fallback? Because the provider's secondary endpoint was never configured.

## Corrective Actions
- Circuit breaker on the air-gds dependency with a fast "search temporarily unavailable" page.
- Configure the provider's secondary endpoint.
- Keep the Flight bookings KPI; it detected the business impact within minutes.

## Lessons Learned
When dependency failures to one partner explain almost every failed request across search, price and book, the incident is the partner. Our job is to fail fast and communicate.
