# Root Cause Analysis - Checkout exception storm after release 2026.10.2

**Date:** 2026-10-08
**Company:** Mercado Libre (demo) - synthetic document, every name and number is invented
**Demo scenario:** `checkout-deploy-exceptions`
**Services:** checkout-api (`POST /api/checkout/orders`, `POST /api/checkout/quote`)
**Severity:** SEV-1 - duration 27 minutes

## Bottom Line Up Front
Release 2026.10.2 of checkout-api changed how the promotion engine combines coupons and installments. For carts mixing a coupon with interest-free installments it returned an empty price breakdown, and `OrderService.Place` threw `System.InvalidOperationException` ("Promotion engine returned an empty price breakdown"). 35 % of `POST /api/checkout/orders` failed on every site and quotes got 2.5x slower. Rolling back fixed it.

## What Happened
- 13:00 UTC - checkout-api 2026.10.2 deployed (blue/green, 100 % in 5 minutes).
- 13:10 UTC - Exceptions KPI of `checkout` (domain grain) breaches after two consecutive 5-minute buckets above 5 % of requests: InvalidOperationException in `OrderService.Place` and `QuoteBuilder.ApplyPromotions`.
- 13:10 UTC - "Orders placed" and "GMV (USD)" business KPIs drop 30-35 %.
- 13:18 UTC - Release manager rolls back to 2026.10.1.
- 13:27 UTC - Exceptions and orders back to baseline.

## 5 Whys
1. Why did orders fail? Because the promotion engine returned an empty price breakdown.
2. Why was it empty? Because the new combination rule discarded the cart when coupon and installments overlapped.
3. Why was that not caught in tests? Because test carts never combined a coupon with installments.
4. Why not? Because test data is generated per feature, not from real cart shapes.
5. Why did it reach 100 % so fast? Because the canary stage was skipped to meet a campaign deadline.

## Corrective Actions
- Canary at 5 % for 15 minutes is mandatory for checkout-api, with automatic rollback on the Exceptions KPI.
- Synthetic cart generator covering coupon x installments x shipping combinations.
- Alert annotation: show the last deploy of the affected service in the Inspector.
