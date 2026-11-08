# Root Cause Analysis - Holiday sale peak (alert review, no incident)

**Date:** 2025-10-28
**Company:** Arrivia (demo) - synthetic document written for the demo stack; not a real incident
**Scenario (demo):** `holiday-sale-peak`
**Verticals:** all - **Apps:** sor-frontend, club-frontend, cruise-core, cruise-web-api, air-ui, air-api
**Severity:** none (post-event review) - **Duration of the peak:** 95 minutes

## Bottom Line Up Front
A holiday sale doubled traffic on every vertical for about an hour and a half. Latency P95 and error rates stayed within their normal ranges and bookings rose. A legacy volume alert paged the on-call; the review concluded that the peak was legitimate and that request volume alone must not be an incident signal.

## Impact
- None on members. Bookings were about twice the usual rate for the hour.
- One unnecessary page to the on-call engineer.

## Timeline (UTC)
- 16:00 Sale opens; traffic doubles within five minutes.
- 16:06 Legacy request-volume alert pages the on-call.
- 16:15 On-call checks Latency P95 and Exceptions KPIs: all within baseline; no action.
- 17:35 Sale traffic returns to normal.

## What Happened
The traffic multiplier applied uniformly to every stage, so the funnel shape did not change: search, details, booking and confirmation all doubled. Error counts doubled in absolute terms but the error rate stayed flat, and latency did not move because capacity was sufficient.

## Root Cause
Not an incident. The page came from a volume threshold alert that did not consider error rate or latency.

## 5 Whys
1. Why was the on-call paged? Because request volume crossed a static threshold.
2. Why was the threshold crossed? Because a planned sale doubled traffic.
3. Why did the alert not know about the sale? Because static volume thresholds ignore context.
4. Why did other KPIs stay quiet? Because latency and error rate were within baseline.
5. Why does the static alert still exist? Because it pre-dated the KPI baselines and was never retired.

## Corrective Actions
- Retire the static volume alert.
- Marketing calendar shared with on-call ahead of planned sales.
- Keep Increase-direction KPIs on latency and exceptions, not on raw volume.

## Lessons Learned
A legitimate peak raises every stage together and keeps error rates flat. If the Inspector sees that shape, the right answer is "no incident".
