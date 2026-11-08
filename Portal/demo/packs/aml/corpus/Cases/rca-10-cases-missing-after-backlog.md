# Root Cause Analysis - Cases opened late after an alerts backlog

**Date:** 2026-02-18  
**Company:** Kinectify (demo) - synthetic document, no real customer or incident  
**Scenario:** alert-queue-backlog  
**Product:** Case Management (cases)  
**Stages:** POST /api/cases/open; PROCESS alert-generation  
**Partner:** alert-queue (internal)  
**Duration:** 40 minutes  
**Customers affected:** all customers

## Bottom Line Up Front
Analysts reported an empty case inbox. The cause was upstream: the alerts worker was failing on `alerts-queue` with ServiceBusException, so AlertRaised and then CaseOpened dropped. Case Management itself was healthy. Resolved by draining the alert backlog.

## Timeline
- 13:00 UTC - Alerts worker starts failing on message locks.
- 13:08 UTC - Business KPI 'Alerts raised' fires.
- 13:20 UTC - Analysts report fewer new cases; Case Management KPIs are normal.
- 13:40 UTC - Alert backlog drained; cases open normally.

## What Happened
Case Management only opens a case when an alert is raised. The incident looked like a Case Management problem but every technical KPI of product cases stayed normal; the upstream alerts KPIs showed the real issue.

## Impact
Case creation delayed up to 40 minutes.

## 5 Whys
1. Why were there fewer cases? Because fewer alerts were raised.
2. Why? Because the alerts worker was failing on expired locks.
3. Why did analysts not know? Because the case inbox shows no upstream health.
4. Why was the investigation slow? Because the first team paged was Case Management.
5. Why? Because the page came from a user report, not from the Business KPI.

## Root Cause
Upstream alert generation failure surfaced as a downstream symptom in Case Management.

## Corrective Actions
- Show alerts pipeline health in the case inbox.
- Route Business KPI 'Alerts raised' to the Alerting team.
- Add a runbook link from Case Management to the alerts worker.
