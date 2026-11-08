# Root Cause Analysis - Cruise API degraded after a cluster node loss

**Date:** 2025-11-20
**Company:** Arrivia (demo) - synthetic document written for the demo stack; not a real incident
**Scenario (demo):** `cluster-node-loss`
**Vertical:** Cruises - **App:** cruise-web-api (alias cruise-api) - **Stages:** 02 Cruise Listing (`GET /api/cruise/searchmatrix`, `GET /api/cruise/getcustomerid`), 07 Passenger (`GET /api/guest/get-guests`), 08 Options (`GET /api/cruise/featuresandoptions`, `GET /api/cruise/insurance`)
**Severity:** Sev 3 - **Duration:** 27 minutes

## Bottom Line Up Front
One node of the cluster that hosts the Cruise API was lost after a host-level patch reboot did not complete. Partitions moved to the remaining nodes, which answered up to fifteen times slower and returned `System.Fabric.FabricTransientException` ("Service partition is not ready") for about 30 % of requests until the cluster rebalanced. Cruise Core itself was healthy.

## Impact
- Every Cruise API stage slowed down by up to fifteen times; search matrix and options pages felt sluggish for all clubs.
- About 30 % of Cruise API requests failed and were retried by the browser; many members saw an error banner.
- Cruise bookings were not measurably affected.

## Timeline (UTC)
- 03:12 Scheduled host patching reboots node 3; the node does not rejoin the cluster.
- 03:22 Exceptions KPI for the Cruises vertical fires (FabricTransientException on SearchMatrix.Build).
- 03:27 Latency P95 KPI for Cruises fires (P95 well above its 7-day baseline).
- 03:30 Platform on-call removes the failed node and triggers a rebalance.
- 03:39 Partitions settle; latency and errors back to normal.

## What Happened
The cluster ran with the minimum node count for its placement constraints. Losing one node left no spare capacity: the primary replicas that moved concentrated on two nodes, and requests that arrived while a partition was moving were rejected as not ready.

## Root Cause
Capacity was sized for steady state, not for one node down during patching, and the patch orchestration rebooted a node without checking cluster health first.

## 5 Whys
1. Why did the Cruise API slow down? Because two nodes served the load of three.
2. Why did one node disappear? Because it did not come back after a patch reboot.
3. Why did that affect members? Because there was no spare node to absorb the moved partitions.
4. Why was there no spare node? Because the cluster was sized at the minimum for its constraints.
5. Why did patching go ahead? Because the orchestration did not gate on cluster health.

## Corrective Actions
- Add one node of headroom to the Cruise API cluster.
- Gate patch reboots on cluster health and on one node at a time.
- Keep FabricTransientException in the Cruises exception KPI; link this RCA in the runbook.

## Lessons Learned
A uniform, many-fold slowdown across all stages of one app, with transient "partition not ready" errors and a healthy database, points at the hosting cluster rather than the code or the partners.
