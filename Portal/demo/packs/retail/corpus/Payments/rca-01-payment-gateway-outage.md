# Root Cause Analysis - Payment gateway outage blocks checkout payments on every site

**Date:** 2026-03-14
**Company:** Mercado Libre (demo) - synthetic document, every name and number is invented
**Demo scenario:** `payment-gateway-outage`
**Services:** payments (`POST /api/payments`), checkout-api
**Partner:** payment-gateway (`gateway.payments-partner.demo`, `POST /v1/charges`)
**Severity:** SEV-1 - duration 38 minutes

## Bottom Line Up Front
The external payment gateway rejected or timed out about two thirds of `POST /v1/charges` calls for 38 minutes. `POST /api/payments` returned 502 with `System.Net.Http.HttpRequestException` on every site (MLB, MLA, MLM, MCO, MLC), and approved payments fell about 65 %. Our services were healthy; the fix was on the partner side. We added a circuit breaker and a secondary routing rule.

## What Happened
- 14:00 UTC - Gateway calls start failing; `POST /api/payments` returns 502 with HttpRequestException "Dependency call to gateway.payments-partner.demo failed".
- 14:10 UTC - Exceptions KPI of `payments` (domain grain) breaches: two consecutive 5-minute buckets above 5 % of requests (about 35 %).
- 14:15 UTC - Business KPI "Payments approved" breaches: three consecutive buckets more than 40 % below the 7-day baseline. Latency P95 of `payments` rises as well (gateway calls 5x slower before failing).
- 14:15 UTC - Payment gateway status page confirms a database failover on their side.
- 14:40 UTC - Gateway recovers; error rate back to baseline (0.3 %).

Every site was affected in proportion to its traffic, which ruled out a site-specific problem. Card acquirer (`auth.acquirer-partner.demo`) calls stayed healthy.

## 5 Whys
1. Why did `POST /api/payments` fail? Because 65 % of `POST /v1/charges` calls to the payment gateway failed.
2. Why did the gateway calls fail? Because the gateway was failing over its primary database.
3. Why did the failover hurt us so much? Because every retry went to the same gateway region and held a connection for up to 10 s.
4. Why did retries hold connections? Because the charge client had no circuit breaker and a 10 s timeout.
5. Why was there no circuit breaker? Because the gateway had been stable for a year and the item was deprioritised.

## Corrective Actions
- Circuit breaker on the payment-gateway client (open after 30 % failures in 1 minute).
- Route new charges to the secondary gateway region when the breaker opens.
- Keep the "Payments approved" business KPI at 5-minute buckets; together with the Exceptions KPI it confirmed the outage within 15 minutes.
