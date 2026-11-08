# Root Cause Analysis - Carrier A rejects shipping labels after dimension rule change

**Date:** 2026-02-17
**Company:** Mercado Libre (demo) - synthetic document, every name and number is invented
**Demo scenario:** `carrier-b-timeouts` (carrier degradation; here carrier A and a validation change)
**Services:** shipping (`POST /api/shipping/labels`)
**Partner:** carrier-a (`api.carrier-a.demo`, `POST /labels`)
**Severity:** SEV-3 - duration 3 hours (slow burn)

## Bottom Line Up Front
Carrier A tightened its maximum package dimensions without notice. Labels for bulky items failed with `Retail.Shipping.LabelRejectedException` ("Carrier rejected the label: package dimensions out of range"), about 6 % of `POST /api/shipping/labels`, mostly on MLA and MLC where carrier A ships most bulky items.

## What Happened
- 08:00 UTC - Carrier A applies the new rule.
- 10:10 UTC - Exceptions KPI of `shipping` (entity grain) breaches for MLA, then MLC; the domain grain stays below threshold.
- 10:40 UTC - Shipping squad confirms the new limit with the carrier.
- 11:05 UTC - Bulky items routed to carrier B.

## 5 Whys
1. Why did labels fail? Because carrier A rejected bulky packages.
2. Why were they rejected? Because the carrier changed its dimension limits.
3. Why did we not know? Because carrier limits are hard-coded and not synced from the carrier.
4. Why hard-coded? Because the carrier API had no limits endpoint when we integrated.
5. Why not revisited? Because there is no periodic review of carrier contracts.

## Corrective Actions
- Sync carrier limits daily from the carrier's new limits endpoint.
- Route by dimensions before calling the carrier.
