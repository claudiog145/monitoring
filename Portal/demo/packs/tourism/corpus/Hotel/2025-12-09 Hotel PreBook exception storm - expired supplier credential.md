# Root Cause Analysis - Hotel PreBook exception storm (expired supplier credential)

**Date:** 2025-12-09
**Company:** Arrivia (demo) - synthetic document written for the demo stack; not a real incident
**Scenario (demo):** `hotel-prebook-exceptions`
**Vertical:** Hotels - **App:** sor-frontend - **Stage:** 05 PreBook (`POST /hotels/book.json`)
**Partners involved:** hotel-hub
**Severity:** Sev 2 - **Duration:** 29 minutes

## Bottom Line Up Front
The client secret used for the hotel hub booking scope expired at midnight UTC. The renewal reminder went to a distribution list that had been retired. PreBook calls started failing with `System.UnauthorizedAccessException` while search continued to work with a long-lived token. Rotating the secret restored service.

## Impact
- 40-50 % of hotel pre-books failed for 29 minutes during the low-traffic night window.
- Limited business impact because of the hour; no member data affected.

## Timeline (UTC)
- 00:00 Booking-scope secret expires.
- 00:07 Exceptions KPI for Hotels fires on HotelPreBook.Authorize.
- 00:15 On-call finds the expiry in the identity audit log.
- 00:29 New secret stored and the storefront restarted; failures stop.

## What Happened
Search and details use a cached token with a long lifetime; PreBook requests a fresh booking-scope token per recheck. Requests that hit the recheck path (rate older than two minutes) failed; the rest succeeded with the cached rate.

## Root Cause
Secret expiry relied on an email reminder to a retired distribution list; there was no automated expiry monitoring.

## 5 Whys
1. Why did PreBook fail? Because the booking-scope token request was rejected.
2. Why was it rejected? Because the client secret had expired.
3. Why was it not renewed? Because nobody received the reminder.
4. Why did nobody receive it? Because it went to a retired distribution list.
5. Why was the list not updated? Because secret ownership was not tracked in the service catalog.

## Corrective Actions
- Automated alert 30 days before any supplier secret expires.
- Owners of supplier credentials recorded in the service catalog.
- Prefer certificate or managed identity where the supplier supports it.

## Lessons Learned
The same UnauthorizedAccessException storm on PreBook can come from a deploy or from an expiry; check the release history and the credential calendar together.
