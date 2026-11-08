# Root Cause Analysis - Month-end regulatory re-screening peak paged on-call (false alarm)

**Date:** 2026-05-01  
**Company:** Kinectify (demo) - synthetic document, no real customer or incident  
**Scenario:** month-end-regulatory-peak  
**Product:** Transaction Screening (screening)  
**Stages:** all stages  
**Partner:** none  
**Duration:** 90 minutes  
**Customers affected:** all customers (expected batch)

## Bottom Line Up Front
At month end customers re-screen their full customer base for regulatory reporting. Traffic more than doubled for 90 minutes; latency and error rates stayed within baseline. An old static request-count threshold paged on-call even though nothing was broken. Classified as a false alarm; the static threshold was retired.

## Timeline
- 00:00 UTC - Month-end batches start across customers.
- 00:05 UTC - Legacy static threshold on request count pages on-call.
- 00:20 UTC - On-call confirms P95 latency, exceptions and dependency health are normal.
- 01:30 UTC - Batches finish; traffic returns to normal.

## What Happened
The re-screening is a planned, recurring, legitimate peak (about 2.2x). The only signal that fired was an absolute request-count threshold; the latency KPIs (baseline of the same slot over the last 7 days, product-level and per customer), the exception thresholds and the Business volume KPIs did not fire, neither during the peak nor when traffic fell back to normal at 01:30, which is the expected behaviour. The product-level P95 is computed from a sample of a few hundred requests per 5-minute bucket, enough for a doubled volume not to move it.

## Impact
None for customers. One unnecessary page.

## 5 Whys
1. Why was on-call paged? Because request count crossed a static threshold.
2. Why? Because month-end re-screening doubles traffic.
3. Why did the threshold not know that? Because static thresholds ignore seasonality.
4. Why did it still exist? Because it predated the KPI baselines.
5. Why was it not removed earlier? Because nobody owned the legacy alert list.

## Root Cause
A legacy static traffic threshold that does not model month-end seasonality.

## Corrective Actions
- Retire the static request-count alert.
- Document the month-end peak as expected behaviour; latency, exception and business-volume KPIs must stay quiet during it and after it.
- Treat a traffic increase alone (with healthy latency and errors) as informational.
