# Root Cause Analysis - Model inference exceptions on velocity features

**Date:** 2025-08-19  
**Company:** Kinectify (demo) - synthetic document, no real customer or incident  
**Scenario:** scoring-model-deploy  
**Product:** Transaction Screening (screening)  
**Stages:** POST /api/v1/risk-score  
**Partner:** scoring-model (internal)  
**Duration:** 28 minutes  
**Customers affected:** customers with new accounts (no 24 h history)

## Bottom Line Up Front
A model update did not handle accounts without 24 h of history. `Kinectify.Scoring.ModelInferenceException: Risk model returned NaN for feature 'velocity_24h'` rose on stage 02 Risk scoring and latency increased because failed calls were retried. Fixed by a default-value patch and a redeploy.

## Timeline
- 16:20 UTC - Model deploy.
- 16:27 UTC - Exceptions KPI of product screening fires; top exception ModelInferenceException.
- 16:41 UTC - Hotfix with imputed default for missing velocity.
- 16:48 UTC - Exceptions back to baseline.

## What Happened
Accounts opened the same day have no velocity history; the new model divided by a zero window. The service retried inference twice, which also increased latency.

## Impact
About 1 900 transactions fell back to rules-only scoring for 28 minutes; all were re-scored afterwards.

## 5 Whys
1. Why did inference fail? Because the model produced NaN for velocity_24h.
2. Why NaN? Because the window length was zero for new accounts.
3. Why was that not tested? Because the offline test set excluded accounts younger than a day.
4. Why excluded? Because the training sample was built from settled accounts only.
5. Why did nobody notice? Because offline evaluation had no edge-case suite.

## Root Cause
Missing guard for empty feature windows in a model release, not covered by offline tests.

## Corrective Actions
- Add an edge-case suite (new accounts, zero history) to model validation.
- Stop retrying deterministic inference errors.
- Alert on ModelInferenceException rate per model version.
