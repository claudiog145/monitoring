# Root Cause Analysis - Search cluster garbage-collection pauses slow MLB search

**Date:** 2025-11-28
**Company:** Mercado Libre (demo) - synthetic document, every name and number is invented
**Demo scenario:** `search-latency-mlm` (same pattern on another site)
**Services:** search-api (`GET /api/search`), storefront (`GET /search`)
**Partner:** search-cluster (`search-cluster.retail.internal`)
**Site:** MLB only
**Severity:** SEV-3 - duration 41 minutes

## Bottom Line Up Front
A new ranking feature loaded a large synonym dictionary for Portuguese into the heap of the MLB search nodes. Long garbage-collection pauses made `POST /items/_search` 5x slower on MLB. Storefront search results (`GET /search`) saw `System.Threading.Tasks.TaskCanceledException` ("Search results request was canceled after 3000 ms").

## What Happened
- 21:30 UTC - Entity-grain Latency P95 of `search` alerts for MLB.
- 21:34 UTC - Storefront Exceptions for MLB rise (TaskCanceledException in `SearchPage.RenderResults`).
- 21:50 UTC - Feature flag for the Portuguese synonyms turned off.
- 22:11 UTC - Latency recovers.

## 5 Whys
1. Why was MLB search slow? Because search nodes paused for GC several times per minute.
2. Why the pauses? Because heap usage jumped by 40 % with the new synonym dictionary.
3. Why did the dictionary go to the heap? Because it was loaded per query context, not shared.
4. Why was that not caught? Because the load test used the Spanish dictionary, which is 5x smaller.
5. Why only Spanish? Because the load test data set is built from MLA.

## Corrective Actions
- Share the synonym dictionary across query contexts.
- Load tests per site language (pt-BR, es-AR, es-MX, es-CO, es-CL).
