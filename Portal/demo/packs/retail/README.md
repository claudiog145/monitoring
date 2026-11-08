# Mercado Libre (demo) - pack `retail`

Synthetic LatAm e-commerce marketplace for the demo stack (contract v5 §6). **Every number, service, partner and
document in this folder is invented.** "Mercado Libre (demo)" is only a label; partners are generic fictional
companies (payment gateway, card acquirer, fraud screening, carrier A, carrier B).

| | |
|---|---|
| companyKey / profileKey | `retail` / `mercadolibre` |
| Apps (appId → database) | `demo-retail-storefront` → `retail-storefront`, `demo-retail-search-api` → `retail-search-api`, `demo-retail-checkout-api` → `retail-checkout-api`, `demo-retail-payments` → `retail-payments`, `demo-retail-shipping` → `retail-shipping` |
| Source aliases | `retail-storefront`, `retail-search-api`, `retail-checkout-api`, `retail-payments`, `retail-shipping` |
| Entity | site, `customDimensions.SiteId`: `MLB`, `MLA`, `MLM`, `MCO`, `MLC` (Zipf 1.2: MLB ~49 %, MLA ~21 %, MLM ~13 %, MCO ~9 %, MLC ~7 %) |
| Traffic | 220 000 requests/day at `laptop`, evening peak 22-01 UTC (19-22 local), weekend × 0.85 |
| Funnel | `GET /search` → `GET /item` → `POST /cart/items` → `GET /cart` → `POST /api/checkout/quote` → `POST /api/checkout/orders` → `POST /api/payments` → `POST /api/shipping/quote` / `POST /api/shipping/labels` |
| Partners | `payment-gateway`, `card-acquirer`, `fraud-screening`, `carrier-a`, `carrier-b` (external, fictional); `search-cluster`, `catalog-db`, `orders-db` (internal) |
| Business events | `CartItemAdded`, `OrderPlaced` (`AmountUsd`, `Items`), `PaymentApproved` (`AmountUsd`), `ShipmentLabelCreated` |

## Scenarios

"Alerts" lists only what the KPI replay measures (`RetailPackTests.RetailPack_ReplayDetectsScenariosAndIgnoresPeak`:
8 days of the reference model at `laptop` before each reset, classified with the profile's first-match-wins cascade
and evaluated with the platform's detection rules). The presenter does not choose when the stack was reset, so the
test sweeps 24 reset times, 7 hours apart from Sat 2026-11-07 21:50 UTC (every hour of the day, over 7 days), with
the historical incidents and the live scenarios of the demo script below. **Bold** KPIs fired for at least 95 % of
the incidents of that sweep and the test asserts exactly them; the percentages are the hit rate over that same sweep,
counted per incident (historical and live: 48 incidents for `search-latency-mlm` and `payment-gateway-outage`, 24 for
the others), with the platform's `KpiEvaluator` and `KpiSampling` and zero-filled business buckets.

| Key | What happens | Alerts (replay) |
|---|---|---|
| `search-latency-mlm` | MLM only: search-cluster 7× slower, storefront search 3.5× slower, 4 % search timeouts. | **Latency P95 of `search` at domain grain** (100 %); also, not asserted, Latency P95 of `search` at site grain for site MLM only, which names the site (98 % of the sweep). The site-grain alert is the one that can miss, mostly early in the morning local time, when MLM has only a few searches per bucket. |
| `payment-gateway-outage` | 65 % of `POST /v1/charges` to the payment gateway fail, 5× slower; `POST /api/payments` returns 502. | **Exceptions of `payments` (domain)** (100 %), **"Payments approved"** (100 %); also Exceptions of `payments` for site MLB (100 %, not asserted), usually for MLA (~84 %); Latency P95 of `payments` (domain) about half of the time (~50 %). |
| `checkout-deploy-exceptions` | After release 2026.11.3: 35 % of `POST /api/checkout/orders` throw `InvalidOperationException`, quote 2.5× slower. | **Exceptions of `checkout` (domain)** (100 %); usually also Exceptions of `checkout` for site MLB (~88 %); sometimes "Orders placed" (~21 %). |
| `carrier-b-timeouts` | Carrier B 8× slower and 75 % failures; shipping quote timeouts. Carrier A healthy. | **Exceptions of `shipping` (domain)** (100 %), **"Shipping labels"** (100 %); also Exceptions of `shipping` for sites MLB and MLA (100 %, not asserted); Latency P95 of `shipping` about half of the time at domain grain (~50 %), usually for site MLB (~79 %). |
| `orders-drop` | Hidden buy box: add-to-cart −60 %, orders/payments/labels −70 %, **no errors, normal latency**. | **"Add to cart"** (100 %), **"Orders placed"** (100 %), **"GMV (USD)"** (96 %), **"Payments approved"** (100 %), **"Shipping labels"** (100 %). No application KPI. |
| `hot-sale` | Legitimate campaign: traffic ×3 on every site and step. | **Nothing** (asserted: no KPI triggers during the peak nor in the 2 h after it, live and historical peak, at every reset of the sweep). |

Historical incidents (pre-seeded by `POST /api/reset`): `search-latency-mlm` 54 h ago (60 min), `hot-sale` 44 h
ago (180 min, must not have alerted), `payment-gateway-outage` 29 h ago (40 min), `carrier-b-timeouts` 13 h ago
(50 min). The replay also asserts at most 2 false triggers per KPI per day outside the incidents (observed: at most
2 per KPI in 8 days, i.e. 0.25 per KPI per day).
## KPIs (`kpis.json`)

Bucket 5 min, evaluated every minute, profile `mercadolibre`. Each service (`storefront`, `search`, `checkout`,
`payments`, `shipping`) is told apart by its own host in `hostFilters` (`www.`, `search-api.`, `checkout-api.`,
`payments.`, `shipping.mercadolibre.demo`), so the storefront's `/search` page never captures `GET /api/search`.
Per service:

- **Latency P95, domain grain:** BaselinePercent +80 % over the same slot of the last 7 days, `sampleSize` 100 000
  (348 samples per bucket, i.e. every request of almost every bucket), at least 60 requests, 2 consecutive buckets.
  `payments` and `shipping` use +120 % and 3 consecutive: their night slots hold few requests, so their reference P95
  is low; with +80 % / 2 consecutive, normal noise read +97 % less than two hours after a Hot Sale in the sweep.
  `search` uses +60 % and at least 30 requests, so a one-site slowdown (MLM, ~13 % of searches) still moves the
  domain P95 at night.
- **Latency P95, site grain:** RobustZScore over the previous 24 buckets, 3 consecutive: z > 8 and at least 8
  requests; `search` uses z > 5 and at least 4 requests, so the MLM site is still evaluated early in the morning.
  With z > 6, quiet sites (MLC, MLA) triggered within two hours of a Hot Sale in the sweep.
- **Exceptions, domain grain:** StaticThreshold on the exception percentage (`Value`) > 5 %, at least 3 exceptions in
  the bucket (`minObservationsPerBucket` counts exceptions), 2 consecutive. The baseline is 0.1-0.7 %, so most
  buckets hold 0-2 exceptions and are skipped; a z-score would see a flat window (MAD 0).
- **Exceptions, site grain:** the same with > 10 %.

Business (`kql:` over `customEvents`, Decrease, BaselinePercent −40 % over 7 days, 3 consecutive buckets; 4 for
"Shipping labels", whose 3-20 labels per bucket are noisy, and for "GMV (USD)", whose few night orders make the sum
swing). Each query zero-fills its buckets (`range` over the window + `join kind=leftouter`, `coalesce(..., 0)`), so a
bucket without events reads 0 instead of being skipped:
`kpi:retail-orders-placed` (OrderPlaced count), `kpi:retail-gmv-usd` (sum of `AmountUsd`),
`kpi:retail-payments-approved`, `kpi:retail-cart-adds`, `kpi:retail-shipping-labels`. Seed each `kqlText` with
`PUT /api/settings/kql/<queryKey>` before posting the KPI (see `Portal/demo/DemoGenerator/README.md`).
## Corpus (`corpus/`)

13 synthetic RCAs (Payments, Search, Checkout, Shipping), one or more per scenario, with the same apps, steps,
partners and exception types as the archetype. Import with
`POST /api/knowledge/import?subPath=retail/corpus&profileKey=mercadolibre`.

## 15-minute demo script

Detection times below are from the replay: an alert is raised when the last bucket of its consecutive run closes,
so count 10 minutes for Exceptions KPIs and ~15 minutes for site-grain latency and business KPIs, plus the
ingestion delay. Start the live scenarios early or show their historical twin first.

1. **(1 min) Context.** Portal with the red DEMO banner; select profile *Mercado Libre (demo)*. Explain the funnel and
   that sites are the entity (MLB and MLA carry ~70 % of traffic). Start
   `scripts/demo-scenario.ps1 -Company retail -Scenario payment-gateway-outage` now (default 30 min).
2. **(3 min) History.** Open the KPI view on the last 3 days: the MLM search latency 54 h ago (Latency P95 of
   `search` at domain grain; usually the site-grain KPI also shows **MLM** only - if it does not, run it live in
   step 4), the payment gateway outage 29 h ago (Exceptions of `payments` and "Payments approved") and the carrier
   B timeouts 13 h ago (Exceptions of `shipping` and "Shipping labels"). Point out that
   the Hot Sale 44 h ago tripled traffic and produced **no alert**.
3. **(4 min) Live partner outage.** About 10 minutes after step 1 the Exceptions KPI of `payments` breaches, and
   "Payments approved" a few minutes later. Open the Inspector: it should say that `POST /api/payments` fails with
   `HttpRequestException` against `gateway.payments-partner.demo` (partner **payment-gateway**) on **all sites**,
   that card-acquirer calls are healthy, and cite *rca-01-payment-gateway-outage* (circuit breaker, secondary
   region) from the Memory Bank. Stop it (`-Stop`) and start `orders-drop`.
4. **(3 min) One-site degradation.** Walk through the historical MLM incident in the Inspector: it should name site
   MLM, the search-cluster partner and the TimeoutException in `SearchService.Query`, and recall
   *rca-04-search-latency-mlm* (shard rebalance, pre-warm). Running `search-latency-mlm` live raises the domain-grain
   alert after ~10 minutes and, usually, the site-grain alert for MLM after ~15 minutes.
5. **(2 min) Business drop without errors.** ~15 minutes after starting `orders-drop`: application KPIs stay green;
   "Add to cart", "Orders placed", "GMV (USD)", "Payments approved" and "Shipping labels" breach. The Inspector should say there are no
   errors nor latency changes, that the funnel lost buyers before the cart, and cite *rca-09-orders-drop-buy-box*
   (feature flag).
6. **(2 min) Legitimate peak.** Stop it and start `hot-sale`. Traffic and orders triple, the exception percentage
   and latency stay flat: no alert. If asked, the Inspector should classify it as expected campaign traffic
   (*rca-13-hot-sale-false-alarm*).

Optional: `checkout-deploy-exceptions` (Exceptions of `checkout` after ~10 minutes; deploy regression, rollback -
*rca-07*) and `carrier-b-timeouts` (Exceptions of `shipping` and "Shipping labels"; carrier B vs healthy carrier A -
*rca-11*).