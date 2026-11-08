# Root Cause Analysis - Slow sanctions provider responses inflate risk-score latency

**Date:** 2025-12-03  
**Company:** Kinectify (demo) - synthetic document, no real customer or incident  
**Scenario:** sanctions-provider-outage  
**Product:** Transaction Screening (screening)  
**Stages:** POST /api/v1/risk-score  
**Partner:** sanctions-provider (api.sanctionlists.demo)  
**Duration:** 35 minutes  
**Customers affected:** all customers, worst for high-volume crypto exchanges

## Bottom Line Up Front
A list update on the provider side made `POST /v2/screen` five times slower for 35 minutes. Risk scoring P95 went from ~780 ms to over 4 s and part of the requests hit the 5000 ms timeout. Recovered without intervention when the provider's index rebuild finished.

## Timeline
- 09:40 UTC - Provider publishes a large consolidated list update.
- 09:44 UTC - Latency KPI of product screening fires; dependency duration for sanctions-provider x5.
- 09:52 UTC - System.TimeoutException 'Sanctions list provider did not answer within 5000 ms' appears on stage 02 Risk scoring.
- 10:15 UTC - Provider index rebuild completes; latency back to baseline.

## What Happened
The provider rebuilt its search index in place after a large list update. Our client has no hedged requests, so every transaction paid the full provider latency. Timeouts were limited to the heaviest customers whose payloads contain many counterparties.

## Impact
Roughly 3 % of transactions in the window timed out and were re-screened automatically. No customer SLA breach.

## 5 Whys
1. Why was risk scoring slow? Because sanctions screening calls took five times longer.
2. Why were the calls slow? Because the provider rebuilt its index during business hours.
3. Why did that reach our customers? Because each counterparty is screened in a separate sequential call.
4. Why sequential? Because batch screening was never enabled on our contract.
5. Why not? Because the volume when the integration was built did not justify it.

## Root Cause
Sequential per-counterparty calls amplified a provider-side latency regression.

## Corrective Actions
- Enable the provider's batch screening endpoint.
- Ask the provider to schedule list rebuilds outside 08:00-20:00 UTC.
- Alert on sanctions-provider dependency latency separately from request latency.
