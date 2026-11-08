# Root Cause Analysis - Partner bookings failing (distribution partner reservation errors)

**Date:** 2026-01-27
**Company:** Arrivia (demo) - synthetic document written for the demo stack; not a real incident
**Scenario (demo):** `partner-bookings-failing`
**Vertical:** Shared Booking Services - **App:** sor-frontend - **Stages:** 07 Booking (`POST /checkout/completepurchase`), 09 Cancellation (`POST /reservations/cancel`)
**Partners involved:** booking-partner (hotel and car distribution partner)
**Severity:** Sev 1 - **Duration:** 47 minutes

## Bottom Line Up Front
The hotel and car distribution partner deployed a change to its reservation API that rejected requests without a new optional header. 85 % of our reservation calls failed; members reached 07 Booking, were charged an authorization hold and then saw `Travel.Booking.PartnerBookingException`. Completed bookings dropped sharply while search stayed healthy. The partner rolled back after our escalation.

## Impact
- Hotel and car bookings dropped by about 80 % for 47 minutes, all clubs.
- Authorization holds were released automatically within 24 hours.
- Cancellations also failed and were replayed after recovery.

## Timeline (UTC)
- 18:40 Partner deploys its reservation API change.
- 18:44 Dependency failures to reservations.partner-bookings-demo.example exceed 80 %.
- 18:47 Exceptions KPI for Shared Booking Services fires.
- 18:50 Hotel and car bookings business KPI fires (Decrease).
- 19:05 Escalation to the partner's on-call.
- 19:27 Partner rolls back; bookings recover.

## What Happened
Our checkout authorizes the card first and then reserves with the partner. When the partner fails, checkout voids the authorization and shows an error. The partner considered the header change backward-compatible, but its validation treated the missing header as an error.

## Root Cause
External: a breaking change in the partner's reservation API. Contributing: no contract test against the partner's pre-production environment and no notice of the change.

## 5 Whys
1. Why did bookings fail? Because the partner rejected reservation calls.
2. Why were they rejected? Because the partner's new validation required a header we do not send.
3. Why did we not know? Because the change was not announced to integrators.
4. Why did we not catch it in their pre-production? Because we have no scheduled contract test there.
5. Why did it take 20 minutes to escalate? Because the partner escalation path was not in the runbook.

## Corrective Actions
- Daily contract test against the partner's pre-production reservation API.
- Partner escalation contacts added to the Shared Booking runbook.
- Ask the partner for change notifications.

## Lessons Learned
Business KPIs (bookings per 5 minutes) and partner dependency failures together identify a partner outage in minutes; neither alone tells the full story.
