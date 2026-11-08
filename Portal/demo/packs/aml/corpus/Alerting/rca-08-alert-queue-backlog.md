# Root Cause Analysis - Alert queue backlog: ServiceBusException storm in alert generation

**Date:** 2026-10-06  
**Company:** Kinectify (demo) - synthetic document, no real customer or incident  
**Scenario:** alert-queue-backlog  
**Product:** Alerting (alerts)  
**Stages:** PROCESS alert-generation  
**Partner:** alert-queue (sb-kinectify.servicebus.demo, internal)  
**Duration:** 40 minutes  
**Customers affected:** all customers

## Bottom Line Up Front
The alerts worker fell behind on `alerts-queue`: receive latency grew 8x, message locks expired before processing finished and about 75 % of messages failed with `Azure.Messaging.ServiceBus.ServiceBusException: The lock supplied is invalid`. AlertRaised dropped and suspicious activity reached analysts late. Fixed by scaling the worker and shortening the per-message work.

## Timeline
- 19:10 UTC - A rules deployment adds two expensive alert rules.
- 19:15 UTC - Exceptions KPI of product alerts fires (exception storm at stage 01 Alert generation).
- 19:20 UTC - Business KPI 'Alerts raised' (Decrease) fires.
- 19:25 UTC - Latency KPI of product alerts fires (P95 of the worker several times last week's).
- 19:35 UTC - Worker scaled from 4 to 10 instances; lock duration extended.
- 19:50 UTC - Backlog drained; exceptions back to baseline.

## What Happened
Each message evaluates all active rules. The new rules made processing slower than the lock duration, so locks expired, messages were redelivered and processed again, which increased the backlog further.

## Impact
About 1 200 alerts were raised 10-40 minutes late. No alert was lost (dead-letter queue empty).

## 5 Whys
1. Why did the worker throw ServiceBusException? Because message locks expired before completion.
2. Why did they expire? Because processing time exceeded the lock duration.
3. Why was processing slower? Because two expensive rules were added.
4. Why was that not caught? Because rule performance is not tested before activation.
5. Why did it cascade? Because redelivered messages added load to an already saturated worker.

## Root Cause
Rule deployment increased per-message processing time beyond the Service Bus lock duration, causing a redelivery spiral.

## Corrective Actions
- Auto-renew message locks during processing.
- Scale the alerts worker on queue length.
- Performance-test new alert rules against a replay sample.
