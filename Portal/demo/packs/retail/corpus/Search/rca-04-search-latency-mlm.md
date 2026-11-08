# Root Cause Analysis - Search latency in Mexico (MLM) during shard rebalancing

**Date:** 2026-08-19
**Company:** Mercado Libre (demo) - synthetic document, every name and number is invented
**Demo scenario:** `search-latency-mlm`
**Services:** search-api (`GET /api/search`), storefront (`GET /search`)
**Partner:** search-cluster (`search-cluster.retail.internal`, `POST /items/_search`)
**Site:** MLM only
**Severity:** SEV-2 - duration 64 minutes

## Bottom Line Up Front
A shard of the MLM item index was moved to a new node while the node was still warming its caches. `POST /items/_search` for MLM became 7x slower, `GET /api/search` P95 went from 0.4 s to 2.8 s and 4 % of MLM queries failed with `System.TimeoutException` ("Search cluster did not answer within 800 ms"). The storefront search page on MLM got 3.5x slower. MLB, MLA, MCO and MLC were unaffected.

## What Happened
- 15:09 UTC - A few searches on MLM time out (about 4 %, TimeoutException in `SearchService.Query`), too few to breach the Exceptions KPI.
- 15:10 UTC - Latency P95 of `search` (domain grain) breaches, but it cannot say which site is slow.
- 15:15 UTC - The entity-grain Latency P95 of `search` breaches for MLM only (three consecutive buckets) and names the site.
- 15:20 UTC - Search squad finds the rebalance job started at 15:00 UTC.
- 16:09 UTC - Caches warm; latency back to baseline.

## 5 Whys
1. Why was MLM search slow? Because the MLM shard was served by a cold node.
2. Why was the node cold? Because the rebalance moved the shard without pre-warming.
3. Why no pre-warming? Because the rebalance job skipped the warm-up step for indices under 50 GB.
4. Why that threshold? Because it was tuned when the MLM index was much smaller.
5. Why was it not revisited? Because index growth is not tracked per site.

## Corrective Actions
- Always pre-warm before switching a shard, regardless of size.
- Schedule rebalances outside the MLM evening peak (00-03 UTC).
- Keep per-site (entity grain) latency KPIs: the domain KPI alone cannot name the site of a one-site incident.
