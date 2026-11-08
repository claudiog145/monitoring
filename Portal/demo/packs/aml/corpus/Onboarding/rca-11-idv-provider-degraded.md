# Root Cause Analysis - Identity verification provider degraded during onboarding

**Date:** 2026-04-08  
**Company:** Kinectify (demo) - synthetic document, no real customer or incident  
**Scenario:** idv-provider-degraded  
**Product:** Client Onboarding (onboarding)  
**Stages:** POST /api/onboarding/verify-identity; POST /api/onboarding/approve  
**Partner:** idv-provider (Identity verification provider, api.verifyid.demo)  
**Duration:** 35 minutes  
**Customers affected:** customers onboarding new clients (mostly fintech and crypto)

## Bottom Line Up Front
The identity verification provider answered 5x slower and rejected about 60 % of `POST /v3/verifications` calls. Stage 02 ID verification failed with `System.TimeoutException` and HTTP 502; applicants stayed stuck before stage 04 Approve and ClientApproved fell sharply. Recovered when the provider rolled back a release.

## Timeline
- 15:12 UTC - Provider deploys a new liveness model.
- 15:17 UTC - Latency and Exceptions KPIs of product onboarding fire.
- 15:22 UTC - Business KPI 'Clients approved' (Decrease) fires: POST /api/onboarding/approve calls fell to a fraction of last week's.
- 15:25 UTC - Partner idv-provider identified from dependency failures.
- 15:47 UTC - Provider rolls back; verification succeeds again.

## What Happened
Failed verifications left applications in 'pending ID' and blocked approvals. Retries by end users added load.

## Impact
About 600 applications delayed; none rejected incorrectly.

## 5 Whys
1. Why did ID verification fail? Because the provider rejected or timed out calls.
2. Why? Because of a faulty provider release.
3. Why did end users retry? Because the UI showed a generic error.
4. Why generic? Because provider errors are not mapped to a 'try later' message.
5. Why no fallback? Because no secondary verification provider is contracted.

## Root Cause
Provider-side release regression with no fallback provider and poor error mapping.

## Corrective Actions
- Show a 'verification delayed, we will notify you' state instead of an error.
- Evaluate a secondary identity verification provider.
- Track idv-provider availability on the dependency board.
