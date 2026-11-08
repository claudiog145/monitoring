# Root Cause Analysis - Onboarding sanctions checks fail during sanctions provider incident

**Date:** 2025-06-25  
**Company:** Kinectify (demo) - synthetic document, no real customer or incident  
**Scenario:** sanctions-provider-outage  
**Product:** Client Onboarding (onboarding)  
**Stages:** POST /api/onboarding/sanctions-check  
**Partner:** sanctions-provider (api.sanctionlists.demo)  
**Duration:** 45 minutes  
**Customers affected:** all customers onboarding clients

## Bottom Line Up Front
During a sanctions provider incident `POST /v2/screen-person` failed for most calls. Stage 03 Sanctions check failed with `System.TimeoutException: Sanctions list provider did not answer within 5000 ms`; approvals were blocked by design (fail closed). Transaction screening was affected at the same time.

## Timeline
- 10:05 UTC - Provider incident begins.
- 10:10 UTC - Exceptions KPI of product onboarding fires.
- 10:12 UTC - Transaction screening KPIs fire too: same partner.
- 10:50 UTC - Provider recovers; pending checks re-run automatically.

## What Happened
Onboarding and transaction screening share the sanctions provider. Both products alerted, and grouping the anomalies by partner `sanctions-provider` made it one incident instead of two.

## Impact
About 90 applications waited for sanctions screening; none were approved without a check.

## 5 Whys
1. Why did sanctions checks fail? Because the provider was down.
2. Why did onboarding wait? Because approval requires a completed check (fail closed).
3. Why no retry queue? Because checks were synchronous.
4. Why synchronous? Because the original UX expected an instant decision.
5. Why not revisited? Because onboarding volume was low until this year.

## Root Cause
Synchronous dependency on the sanctions provider for onboarding with no queued retry.

## Corrective Actions
- Queue sanctions checks and notify the applicant when done.
- Share the sanctions list snapshot fallback with onboarding.
- Group anomalies by partner in incident triage.
