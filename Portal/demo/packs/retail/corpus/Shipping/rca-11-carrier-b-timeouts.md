# Root Cause Analysis - Shipping carrier B timeouts break shipping quotes and labels

**Date:** 2026-09-02
**Company:** Mercado Libre (demo) - synthetic document, every name and number is invented
**Demo scenario:** `carrier-b-timeouts`
**Services:** shipping (`POST /api/shipping/quote`, `POST /api/shipping/labels`)
**Partners:** carrier-b (`ws.carrier-b.demo`, `POST /quote`, `POST /shipments`); carrier-a healthy
**Severity:** SEV-2 - duration 52 minutes

## Bottom Line Up Front
Carrier B's rating and label API degraded during their own peak: calls were 8x slower and three quarters of them failed. `POST /api/shipping/quote` failed with `System.TimeoutException` ("Carrier rate request timed out after 5000 ms") and HttpRequestException against `ws.carrier-b.demo`; label creation also failed, delaying dispatch. Carrier A (`api.carrier-a.demo`) stayed healthy, which pointed to the partner immediately.

## What Happened
- 17:40 UTC - Exceptions KPI of `shipping` breaches on shipping-quote and shipping-label.
- 17:42 UTC - Latency P95 of `shipping` triples.
- 17:50 UTC - Dependency breakdown: carrier-b failures 75 %, carrier-a normal.
- 18:05 UTC - Quotes switched to carrier A only for affected postal codes.
- 18:32 UTC - Carrier B recovers; normal routing restored.

## 5 Whys
1. Why did shipping quotes fail? Because the quote waits for every carrier's rate.
2. Why wait for every carrier? Because the quote shows the cheapest option among all carriers.
3. Why did one slow carrier block the quote? Because there was no per-carrier deadline: the 5000 ms timeout applied to the whole quote.
4. Why no per-carrier deadline? Because quotes were designed when there was a single carrier.
5. Why was the design not revisited when carrier B was added? Because the integration was treated as configuration, not as a new dependency.

## Corrective Actions
- Per-carrier deadline of 800 ms; quote with the carriers that answered.
- Label creation retries on the alternate carrier when the primary fails twice.
- Partner-level dependency KPI for carrier-a and carrier-b.
