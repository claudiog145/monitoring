# Root Cause Analysis - Fraud screening slowness delays order placement

**Date:** 2025-12-11
**Company:** Mercado Libre (demo) - synthetic document, every name and number is invented
**Demo scenario:** `checkout-deploy-exceptions` (checkout degradation; here caused by a partner)
**Services:** checkout-api (`POST /api/checkout/orders`)
**Partner:** fraud-screening (`score.fraud-partner.demo`, `POST /v2/score`)
**Severity:** SEV-3 - duration 35 minutes

## Bottom Line Up Front
The fraud screening partner deployed a new model that answered `POST /v2/score` in 2-3 s instead of 110 ms. `POST /api/checkout/orders` P95 rose from 1.6 s to 4.8 s on every site; some buyers abandoned and "Orders placed" dipped 12 %, below the alert threshold. The partner rolled back their model.

## What Happened
- 20:15 UTC - Latency P95 of `checkout` (domain grain) breaches on place-order.
- 20:18 UTC - Dependency view shows fraud-screening as the slow call; orders-db is normal.
- 20:30 UTC - Partner confirms a model release.
- 20:50 UTC - Partner rollback; latency normal.

## 5 Whys
1. Why was order placement slow? Because every order waits for the fraud score.
2. Why was the score slow? Because the partner's new model was heavier.
3. Why did we wait? Because the fraud call is synchronous with a 5 s timeout.
4. Why synchronous? Because low-value orders were never split from high-value ones.
5. Why not split? Because the risk policy predates the current order volume.

## Corrective Actions
- Orders under USD 30 are scored asynchronously.
- Fraud-screening timeout reduced to 1.5 s with a conservative default decision.
