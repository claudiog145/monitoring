# Root Cause Analysis - Silent drop in orders and GMV caused by a hidden buy box

**Date:** 2026-05-06
**Company:** Mercado Libre (demo) - synthetic document, every name and number is invented
**Demo scenario:** `orders-drop`
**Services:** storefront (`POST /cart/items`), checkout-api (`POST /api/checkout/orders`), payments (`POST /api/payments`), shipping (`POST /api/shipping/labels`)
**Severity:** SEV-1 (business impact) - duration 46 minutes

## Bottom Line Up Front
A storefront feature flag meant for an A/B test of the item page was enabled for 100 % of traffic. For about 60 % of the catalog the buy box was not rendered, so buyers could not add items to the cart. Add-to-cart fell 60 %, OrderPlaced and GMV fell 70 %, payments and shipping labels followed. There were **no errors and no latency change**: only the business KPIs ("Add to cart", "Orders placed", "GMV (USD)", "Payments approved") detected it.

## What Happened
- 19:10 UTC - Feature flag `item-page-v3` changed from 5 % to 100 % by mistake.
- 19:25 UTC - "Add to cart" business KPI breaches (-58 % vs 7-day baseline, three consecutive 5-minute buckets). Application KPIs are all green.
- 19:25 UTC - "Orders placed" and "GMV (USD)" breach (-70 %).
- 19:45 UTC - Product squad finds the flag change in the audit log and reverts it.
- 19:56 UTC - Business KPIs recover.

## 5 Whys
1. Why did orders drop? Because buyers could not add items to the cart.
2. Why could they not? Because the buy box was not rendered for items without a v3 layout.
3. Why did it reach all buyers? Because the flag rollout percentage was edited to 100 instead of 10.
4. Why was that possible? Because the flag console has no confirmation for jumps above 50 %.
5. Why did it take 15 minutes to notice? Because only technical KPIs paged; business KPIs were dashboard-only.

## Corrective Actions
- Business KPIs (orders, GMV, add to cart) page the on-call, not only the dashboard.
- Flag console confirmation for rollout jumps above 50 %.
- Distinguish from a Hot Sale: a drop with healthy latency and errors is a product/funnel problem, not infrastructure.
