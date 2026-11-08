# Root Cause Analysis - Air pricing and booking failures (fare provider throttling)

**Date:** 2026-09-17
**Company:** Arrivia (demo) - synthetic document written for the demo stack; not a real incident
**Scenario (demo):** `air-search-outage`
**Vertical:** Air - **App:** air-api - **Stages:** 05 Air Price (`POST /api/air/price`), 07 Air Booking (`POST /api/air/book`), 02 Air Search
**Partners involved:** air-gds
**Severity:** Sev 2 - **Duration:** 33 minutes

## Bottom Line Up Front
A marketing email linked directly to a flight search with dozens of pre-filled date combinations. Each click generated a burst of shopping calls and the fare provider throttled our account. Throttled calls failed for search, price and book alike; the Air UI results page reported `System.Net.Http.HttpRequestException` (503 from the Air API), `Travel.Air.FareChangedException` and `TicketingException` rose and FlightBooked fell by 70 %. Limiting the fan-out of date-flexible searches ended the throttling.

## Impact
- Air price and book failures for 33 minutes, all clubs.
- About 120 flight bookings estimated lost or delayed (synthetic figure).
- Other verticals unaffected.

## Timeline (UTC)
- 13:00 Campaign email sent.
- 13:06 Dependency failures to the air fare provider rise above 50 %.
- 13:10 Exceptions KPI for Air fires; Flight bookings KPI fires shortly after.
- 13:24 Provider support confirms account-level throttling.
- 13:39 Date-flexible fan-out capped from 30 to 5 combinations; failures stop.

## What Happened
The Air API expands a flexible-date search into one provider call per date pair. The campaign deep link requested a 15-day window both ways, so every member click cost hundreds of provider calls. Price and book shared the same throttled quota.

## Root Cause
Unbounded fan-out of provider calls per member action, combined with a shared provider quota for shopping, pricing and booking.

## 5 Whys
1. Why did pricing and booking fail? Because the provider throttled our account.
2. Why were we throttled? Because shopping calls exceeded the account quota.
3. Why so many shopping calls? Because flexible-date searches fanned out to hundreds of calls.
4. Why were flexible-date searches so frequent? Because a campaign deep link pre-filled a wide window.
5. Why did search traffic affect booking? Because all operations share one quota.

## Corrective Actions
- Cap flexible-date fan-out and cache provider answers for identical searches.
- Ask the provider for a separate quota for price and book.
- Marketing deep links to air search are reviewed by the Air team.

## Lessons Learned
The same air-gds failure pattern as a provider outage can be self-inflicted. Check the request volume to the partner before blaming the partner.
