# Root Cause Analysis - Bookings dropped with no errors (promotional banner covered the purchase button)

**Date:** 2026-07-08
**Company:** Arrivia (demo) - synthetic document written for the demo stack; not a real incident
**Scenario (demo):** `silent-bookings-drop`
**Verticals:** Shared Booking Services, Cruises, Air - **Apps:** sor-frontend, cruise-core, air-api - **Stages:** 07 Booking (`POST /checkout/completepurchase`), 08 Confirmation (`GET /checkout/purchasecomplete`), 11 Confirmation (`POST /cruiseconfirm.do`), 07 Air Booking (`POST /api/air/book`)
**Severity:** Sev 2 - **Duration:** 63 minutes

## Bottom Line Up Front
A summer promotion banner was published through the content system with a fixed position that, on common laptop screen sizes, covered the purchase button of the checkout and confirmation pages. No request failed and latency was normal, so no technical alert fired. Completed bookings fell by about 65 % across hotels, cars, cruises and air. The business KPIs detected the drop; the banner was unpublished.

## Impact
- Roughly two thirds of the bookings expected in the hour did not happen (synthetic estimate).
- Members who scrolled or used a phone could still book; desktop members could not.
- No errors, no data issues.

## Timeline (UTC)
- 17:00 Banner published.
- 17:10 Hotel and car bookings KPI fires (Decrease); technical KPIs stay green.
- 17:15 Cruise bookings and flight bookings KPIs fire.
- 17:40 Customer care reports members "cannot click Book".
- 18:03 Banner unpublished; bookings return to the expected curve.

## What Happened
Traffic to the booking steps fell while traffic to search and details stayed normal. Because the funnel stopped before the booking calls, there were fewer requests, not failed ones, and the exception and latency KPIs had nothing to detect.

## Root Cause
Content changes to transactional pages were not previewed at common viewport sizes and did not go through the same review as code.

## 5 Whys
1. Why did bookings drop? Because members did not submit the purchase step.
2. Why did they not submit? Because the purchase button was covered.
3. Why was it covered? Because the banner used a fixed position over the checkout area.
4. Why was it published? Because the preview only showed a wide desktop viewport.
5. Why was there no technical alert? Because nothing failed; only business volume changed.

## Corrective Actions
- Banners are blocked on checkout and confirmation pages.
- Content previews at three viewport sizes.
- Keep the business KPIs on bookings per vertical as first-class alerts.

## Lessons Learned
A drop in business events with flat errors and latency is real and urgent. Look at funnel conversion between stages, not only at failures.
