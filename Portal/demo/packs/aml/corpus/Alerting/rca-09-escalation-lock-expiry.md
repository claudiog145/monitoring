# Root Cause Analysis - Case escalations delayed by lock expiry on escalations-queue

**Date:** 2025-10-15  
**Company:** Kinectify (demo) - synthetic document, no real customer or incident  
**Scenario:** alert-queue-backlog  
**Product:** Alerting (alerts)  
**Stages:** PROCESS case-escalation; PROCESS alert-generation  
**Partner:** alert-queue (internal)  
**Duration:** 32 minutes  
**Customers affected:** all customers

## Bottom Line Up Front
A slow `INSERT cases.Cases` on the case database made `PROCESS case-escalation` exceed its message lock. ServiceBusException on `escalations-queue` rose and alert generation slowed because both workers share the same host pool. Recovered after an index rebuild on the case database.

## Timeline
- 03:30 UTC - Nightly index maintenance on the case database starts late.
- 03:36 UTC - Exceptions KPI of product alerts fires.
- 03:50 UTC - Root cause found in dependency duration of `INSERT cases.Cases`.
- 04:02 UTC - Maintenance finished; lock errors stop.

## What Happened
Index maintenance overlapped with the European morning peak. Escalation inserts slowed, the lock expired and messages were redelivered, putting pressure on the shared alerts worker pool.

## Impact
About 300 escalations were 5-30 minutes late.

## 5 Whys
1. Why did escalations fail? Because message locks expired.
2. Why? Because `INSERT cases.Cases` was slow.
3. Why? Because index maintenance ran during peak.
4. Why during peak? Because the maintenance window was defined in local time and moved with daylight saving.
5. Why did it affect alert generation? Because both workers share the same pool.

## Root Cause
Maintenance window drifted into peak hours; shared worker pool spread the impact.

## Corrective Actions
- Define maintenance windows in UTC.
- Separate the escalation and alert generation worker pools.
- Auto-renew message locks.
