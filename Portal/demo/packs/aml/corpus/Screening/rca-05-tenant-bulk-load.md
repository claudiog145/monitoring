# Root Cause Analysis - Single customer tenant degraded by a bulk load against a misconfigured rule set

**Date:** 2026-03-10  
**Company:** Kinectify (demo) - synthetic document, no real customer or incident  
**Scenario:** tenant-degraded  
**Product:** Transaction Screening (screening)  
**Stages:** POST /api/v1/transactions, POST /api/v1/risk-score, GET /api/v1/screening-status  
**Partner:** none (customer-specific configuration)  
**Duration:** 60 minutes  
**Customers affected:** one customer tenant (T-003)

## Bottom Line Up Front
Customer tenant T-003 replayed a historical file of transactions while a newly enabled rule set evaluated every counterparty against the full PEP list. Only that tenant saw every screening call (ingest, risk scoring, status) about 7x slower, with its median latency above 1 s and ~25 % `System.TimeoutException` on risk scoring; all other customers were unaffected. Mitigated by rate-limiting the tenant and fixing the rule set.

## Timeline
- 08:05 UTC - T-003 starts a bulk replay.
- 08:15 UTC - Entity-grain *Median latency* KPI fires for customer T-003 only; no other customer series moves. The product-level P95 also rises, because T-003 carries about 8 % of the screening traffic, but it does not say who is affected.
- 08:20 UTC - Inspector shows the anomaly concentrated on CustomerTenantId T-003 at stage 02 Risk scoring, with ingest slow as well.
- 08:45 UTC - Per-tenant rate limit applied; rule set corrected with the customer.
- 09:05 UTC - Latency for T-003 back to baseline.

## What Happened
The new rule set disabled the fuzzy-match pre-filter, so every counterparty went through full PEP screening. Combined with the replay volume it saturated that tenant's partition of the scoring workers.

## Impact
T-003's transactions were delayed for an hour; no other customer was affected.

## 5 Whys
1. Why was T-003 slow? Because each of its transactions took several seconds to ingest and score.
2. Why? Because its rule set disabled the fuzzy-match pre-filter.
3. Why was that allowed? Because rule-set changes are not load-tested.
4. Why did it coincide with a replay? Because bulk replays are not announced.
5. Why does a replay affect live traffic? Because replays share the live scoring partition.

## Root Cause
Customer-specific rule set without pre-filter combined with an unannounced bulk replay on the live partition.

## Corrective Actions
- Route bulk replays to a separate low-priority partition.
- Validate rule sets for pre-filter presence before activation.
- Keep the per-customer median latency KPI enabled: a tenant's median does not depend on its volume, so it separates one slow customer from a busy one.
