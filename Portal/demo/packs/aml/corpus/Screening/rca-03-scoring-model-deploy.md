# Root Cause Analysis - Risk scoring latency regression after model deploy

**Date:** 2026-06-22  
**Company:** Kinectify (demo) - synthetic document, no real customer or incident  
**Scenario:** scoring-model-deploy  
**Product:** Transaction Screening (screening)  
**Stages:** POST /api/v1/risk-score  
**Partner:** scoring-model (risk-model.kinectify.internal, internal)  
**Duration:** 40 minutes  
**Customers affected:** all customers

## Bottom Line Up Front
Model release 2026.06.2 shipped a feature pipeline that recomputed 30-day velocity features on every call. Calls to the internal scoring model (`POST /score`) became ~6x slower and `POST /api/v1/risk-score` P95 jumped above 3 s for 40 minutes, with a small rise of `Kinectify.Scoring.ModelInferenceException`. Rolled back to the previous model.

## Timeline
- 11:00 UTC - Model 2026.06.2 promoted to 100 % of traffic.
- 11:06 UTC - Latency KPI (BaselinePercent) of product screening fires.
- 11:10 UTC - Inspector correlates the change with the deploy and with dependency `POST /score` to scoring-model.
- 11:38 UTC - Rollback to 2026.06.1 completed.
- 11:42 UTC - Latency back to baseline.

## What Happened
The new model needed features that were not in the online feature cache, so the scoring service computed them from raw history per request. Canary traffic was 5 % and too small to move the P95 visibly; the regression became obvious only at 100 %.

## Impact
No failed screenings beyond ~3 % ModelInferenceException retries; customers saw slower synchronous responses and two customers opened tickets.

## 5 Whys
1. Why was risk scoring slow? Because `POST /score` to the scoring model was ~6x slower.
2. Why was the model slower? Because new features were computed per request.
3. Why per request? Because they were missing from the online feature cache.
4. Why was that not detected in canary? Because the canary latency check used the mean, not P95.
5. Why the mean? Because the canary gate was copied from the batch-scoring pipeline.

## Root Cause
Model release depended on features missing from the online cache; canary gate did not measure P95.

## Corrective Actions
- Gate model promotion on P95 of `POST /score` with a 5-minute bucket.
- Fail the release pipeline if a model requests features absent from the online cache.
- Keep the previous model warm for one-click rollback.
