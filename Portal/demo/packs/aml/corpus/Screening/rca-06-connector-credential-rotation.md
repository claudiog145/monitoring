# Root Cause Analysis - Screened transactions drop after a customer connector credential rotation

**Date:** 2026-07-30  
**Company:** Kinectify (demo) - synthetic document, no real customer or incident  
**Scenario:** screening-volume-drop  
**Product:** Transaction Screening (screening)  
**Stages:** POST /api/v1/transactions; POST /api/v1/risk-score  
**Partner:** none  
**Duration:** 45 minutes  
**Customers affected:** several customers using the managed connector

## Bottom Line Up Front
A scheduled credential rotation of the managed ingestion connector left most connector instances with an expired secret. They stopped sending transactions without raising errors on our side: TransactionReceived and TransactionScreened fell to about 30 % of normal while latency and exceptions stayed flat. Only the Business KPI 'Transactions screened' detected it.

## Timeline
- 02:00 UTC - Credential rotation job runs.
- 02:10 UTC - Business KPI 'Transactions screened' (Decrease) fires; no Application KPI fires.
- 02:25 UTC - Ingest traffic on stage 01 Ingest confirmed at ~30 % of the same hour last week.
- 02:40 UTC - Connector instances restarted with the new secret.
- 02:55 UTC - Volume back to baseline; backlog drained by the connectors.

## What Happened
The connector caches the secret at start-up and treats 401 from its own source system as 'no new data'. Nothing failed on the Kinectify side, so only the volume of screened transactions showed the problem.

## Impact
Transactions were screened up to 50 minutes late. Regulatory monitoring windows were met.

## 5 Whys
1. Why did screened volume drop? Because connectors stopped sending transactions.
2. Why? Because they kept an expired secret after rotation.
3. Why was it not reloaded? Because the connector reads the secret only at start-up.
4. Why was the failure silent? Because 401 is treated as an empty batch.
5. Why was there no alert earlier? Because only technical KPIs existed for ingestion before this incident.

## Root Cause
Connector did not reload rotated credentials and masked authentication failures as empty batches.

## Corrective Actions
- Reload secrets on rotation events.
- Treat source 401 as an error and emit an exception.
- Keep the Business KPI on TransactionScreened as the primary volume guard.
