# Root Cause Analysis - Sanctions list provider outage blocks transaction screening

**Date:** 2026-09-14  
**Company:** Kinectify (demo) - synthetic document, no real customer or incident  
**Scenario:** sanctions-provider-outage  
**Product:** Transaction Screening (screening)  
**Stages:** POST /api/v1/risk-score; POST /api/onboarding/sanctions-check  
**Partner:** sanctions-provider (Sanctions & PEP list provider, api.sanctionlists.demo)  
**Duration:** 47 minutes  
**Customers affected:** all customers

## Bottom Line Up Front
The sanctions & PEP list provider refused about 70 % of `POST /v2/screen` calls for 47 minutes after a failed failover on their side. `POST /api/v1/risk-score` failed with HTTP 502 and `System.TimeoutException`, TransactionScreened dropped by two thirds and onboarding sanctions checks stalled. Service recovered when the provider completed its failover; no transaction was released unscreened because the pipeline fails closed.

## Timeline
- 14:02 UTC - Latency P95 of stage 02 Risk scoring crosses the 7-day baseline (+410 %).
- 14:06 UTC - Exceptions KPI for product screening fires; dominant exception System.Net.Http.HttpRequestException (502) from api.sanctionlists.demo.
- 14:09 UTC - Business KPI 'Transactions screened' fires (Decrease).
- 14:15 UTC - On-call confirms the provider status page reports a degraded region.
- 14:31 UTC - Retry budget lowered to protect the thread pool; queued transactions held, not released.
- 14:49 UTC - Provider failover completes; error rate back to baseline within two buckets.

## What Happened
Every risk-score request calls the sanctions provider synchronously before the model is invoked. When the provider started rejecting calls, each request waited up to the 5000 ms client timeout and then failed. Because the dependency is shared by onboarding, `POST /api/onboarding/sanctions-check` failed at the same time. The dependency failures were attributed to partner `sanctions-provider` by the profile's partner matcher, which made the triage obvious.

## Impact
About 21 000 transactions were screened late (held and re-screened after recovery). 140 onboarding applications waited for a sanctions check. No regulatory deadline was missed.

## 5 Whys
1. Why did risk scoring fail? Because the call to the sanctions & PEP list provider failed or timed out.
2. Why did the call fail? Because the provider's primary region was degraded and its failover did not complete for 45 minutes.
3. Why did that hurt every customer? Because there is a single provider endpoint and no cached list fallback.
4. Why is there no cached fallback? Because the list snapshot job was descoped when the provider SLA was renegotiated.
5. Why was the descoping not caught? Because there was no partner-level availability KPI to show the exposure.

## Root Cause
Single synchronous dependency on the sanctions list provider with no local list snapshot to fall back to.

## Corrective Actions
- Reinstate the hourly sanctions list snapshot and screen against it when the provider is unavailable (owner: Screening team).
- Add a circuit breaker on `POST /v2/screen` so requests fail fast instead of waiting 5000 ms.
- Track partner availability for `sanctions-provider` on the dependency board.
