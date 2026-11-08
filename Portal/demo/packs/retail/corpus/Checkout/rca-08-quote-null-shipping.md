# Root Cause Analysis - Null shipping option breaks checkout quotes after configuration change

**Date:** 2025-07-15
**Company:** Mercado Libre (demo) - synthetic document, every name and number is invented
**Demo scenario:** `checkout-deploy-exceptions` (exception storm after a change)
**Services:** checkout-api (`POST /api/checkout/quote`)
**Severity:** SEV-2 - duration 22 minutes

## Bottom Line Up Front
A configuration change removed a deprecated shipping service level. Carts that still referenced it got a null shipping option and `QuoteBuilder.AddShipping` threw `System.NullReferenceException` ("Shipping option was null for cart line"). About 15 % of `POST /api/checkout/quote` failed until the configuration was restored.

## What Happened
- 16:40 UTC - Shipping configuration published.
- 16:42 UTC - Exceptions KPI of `checkout` breaches on step checkout-quote.
- 16:55 UTC - Change identified from the configuration audit log.
- 17:02 UTC - Configuration restored; failures stop.

## 5 Whys
1. Why did quotes fail? Because a cart line referenced a shipping level that no longer existed.
2. Why did that throw? Because `AddShipping` assumed the option was never null.
3. Why was the level removed while carts used it? Because carts keep their shipping choice for 7 days.
4. Why was that not considered? Because the configuration change had no impact check against open carts.
5. Why no impact check? Because configuration changes do not go through the release process.

## Corrective Actions
- Treat shipping configuration like code (review + canary).
- Fallback to the cheapest available level when the stored one is missing.
