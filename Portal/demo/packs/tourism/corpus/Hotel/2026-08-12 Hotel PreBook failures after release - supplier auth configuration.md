# Root Cause Analysis - Hotel PreBook failures after a release (supplier authentication configuration)

**Date:** 2026-08-12
**Company:** Arrivia (demo) - synthetic document written for the demo stack; not a real incident
**Scenario (demo):** `hotel-prebook-exceptions`
**Vertical:** Hotels - **App:** sor-frontend - **Stage:** 05 PreBook (`POST /hotels/book.json`)
**Partners involved:** hotel-hub
**Severity:** Sev 2 - **Duration:** 44 minutes

## Bottom Line Up Front
A storefront release replaced the hotel supplier client configuration and set the wrong token audience for the rate recheck call. From the first request after the release, 90 % of hotel pre-books failed with `System.UnauthorizedAccessException` ("Supplier rejected the booking token"). Hotel search and details were healthy because they use a different token. Rolling back the configuration fixed it.

## Impact
- Nine out of ten Hotel 05 PreBook attempts failed for 44 minutes, all clubs.
- Hotel pre-books (HotelPreBooked) and completed hotel bookings dropped accordingly.
- Cars and Cruises unaffected.

## Timeline (UTC)
- 15:20 Storefront release deployed.
- 15:24 Exceptions KPI for Hotels fires: UnauthorizedAccessException on HotelPreBook.Authorize.
- 15:31 Hotel pre-books per 5 minutes visibly down on the business dashboard.
- 15:52 Release diff shows the audience change in the supplier client settings.
- 16:04 Configuration rolled back; failures stop.

## What Happened
The release consolidated three supplier client registrations into one settings block. The rate recheck call used by PreBook needs a token for a different audience than search; after consolidation it received the search token, which the supplier rejected. Almost every pre-book needs a recheck, so about 90 % of them failed.

## Root Cause
A configuration refactor changed an authentication setting used by only one stage, and no test exercised that stage against the supplier sandbox.

## 5 Whys
1. Why did PreBook fail? Because the supplier rejected the booking token.
2. Why was the token rejected? Because its audience was the search audience.
3. Why did PreBook get the search token? Because the release merged the client settings.
4. Why was it not detected before production? Because the smoke test only covered search and details.
5. Why only those? Because PreBook needs a bookable test rate, which the sandbox lacked.

## Corrective Actions
- Add a PreBook smoke test with a bookable sandbox rate to the release pipeline.
- Keep separate, named supplier client settings per audience.
- Post-deploy watch on the Hotels exception KPI for 30 minutes.

## Lessons Learned
An exception storm on exactly one stage right after a deploy, with healthy neighbours that call the same partner, points at stage-specific configuration in the release.
