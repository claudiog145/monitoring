# Root Cause Analysis - Hot Sale traffic peak paged on-call although nothing was broken

**Date:** 2025-06-10
**Company:** Mercado Libre (demo) - synthetic document, every name and number is invented
**Demo scenario:** `hot-sale` (legitimate peak - must NOT alert)
**Services:** storefront, search-api, checkout-api, payments, shipping
**Severity:** none (false alarm) - review of the alerting design

## Bottom Line Up Front
During the Hot Sale campaign traffic tripled on every site and step. Error **rates** and latency stayed normal and orders went up, but the old exception alerts counted exceptions in absolute numbers, so they fired on every service and woke up five squads. There was no incident. We changed the Exceptions KPIs to use the exception **percentage** of requests (aggregation Value) and made business KPIs alert only on decreases.

## What Happened
- 00:00 UTC - Hot Sale starts; requests x3 on MLB, MLA, MLM, MCO and MLC.
- 00:05 UTC - Five "exceptions count" alerts fire (storefront, search, checkout, payments, shipping).
- 00:20 UTC - Incident commander compares exception percentage: 0.2 % before and during the peak. Orders and GMV x3. Declared not an incident.

## 5 Whys
1. Why did on-call get paged? Because exception counts tripled.
2. Why did counts triple? Because traffic tripled; the error rate did not change.
3. Why did the alert use counts? Because it was copied from a low-traffic back-office service.
4. Why was it not tuned for campaigns? Because campaign traffic was never replayed against the alert rules.
5. Why not? Because alert rules had no owner review before campaigns.

## Corrective Actions
- Exceptions KPIs use aggregation Value (exceptions as % of requests) at domain and site grain.
- Business KPIs ("Orders placed", "GMV (USD)", "Payments approved", "Add to cart", "Shipping labels") alert on Decrease only.
- Before each campaign, replay the expected peak (`hot-sale` scenario) and confirm that no KPI alerts.
- Inspector guidance: a peak with normal error percentage and latency, and with orders up, is expected campaign traffic.
