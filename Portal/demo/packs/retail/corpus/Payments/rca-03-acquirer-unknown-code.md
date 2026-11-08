# Root Cause Analysis - Card acquirer response codes break payment authorization

**Date:** 2026-06-03
**Company:** Mercado Libre (demo) - synthetic document, every name and number is invented
**Demo scenario:** `payment-gateway-outage` (same symptoms, different partner)
**Services:** payments (`POST /api/payments`)
**Partner:** card-acquirer (`auth.acquirer-partner.demo`, `POST /authorize`)
**Severity:** SEV-2 - duration 31 minutes

## Bottom Line Up Front
The card acquirer introduced a new response code for soft declines. Our authorization mapper did not know it and threw `Retail.Payments.AcquirerResponseException` ("Card acquirer returned an unexpected response code"), failing 12 % of `POST /api/payments` on MLB and MLA, where the acquirer handles most card volume.

## What Happened
- 18:20 UTC - Exceptions KPI of `payments` (entity grain) breaches for MLB, then MLA.
- 18:24 UTC - Payment gateway calls are healthy, which rules out the gateway (compare with the payment-gateway outage RCA of 2026-03-14).
- 18:40 UTC - Mapper hot-fixed to treat unknown codes as soft declines.
- 18:51 UTC - Error rate back to 0.2 %.

## 5 Whys
1. Why did payments fail? Because `PaymentService.Authorize` threw on an unknown response code.
2. Why was the code unknown? Because the acquirer added it without notice.
3. Why did an unknown code throw? Because the mapper used a strict switch with no default.
4. Why was there no default? Because the contract tests only covered documented codes.
5. Why only documented codes? Because there was no fuzzing of partner responses.

## Corrective Actions
- Unknown acquirer codes map to "soft decline, retry with another method".
- Contract tests with unknown codes for every payment partner.
- Entity-grain Exceptions KPI kept: it showed MLB and MLA first, matching the acquirer's footprint.
