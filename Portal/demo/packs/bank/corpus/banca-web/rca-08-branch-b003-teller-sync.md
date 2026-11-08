**Root Cause Analysis**

Branch B-003 — slow operations after a teller synchronization job

**Bottom Line Up Front**

On 2025-06-23 a synchronization job of the teller system of branch B-003 ran during business hours and locked the branch's account partition in core-gateway. Operations of customers booked to B-003 were slow and some failed with System.TimeoutException for 40 minutes on web-banking and mobile-api. Other branches were not affected.

**What Happened**

At 15:20 (UTC-5) the entity-grain Latency KPI of banca-web flagged BranchId B-003 (P95 six times its 2-hour median). Domain-grain KPIs moved only slightly because B-003 carries a small share of the traffic. The exceptions for B-003 were System.TimeoutException on GET /banca/cuentas/saldos and POST /banca/transferencias.

**Impact**

- 40 minutes; customers of branch B-003 only.
- No partner degradation.

**Timeline (UTC-5)**

- 15:15 Teller sync job starts (planned for 21:00).
- 15:20 KPI anomaly: Latency P95 (banca-web, entity B-003).
- 15:42 Job identified and stopped.
- 15:55 Latency for B-003 back to normal.

**Root Cause — Plain Language**

The job was rescheduled by hand to recover from a failure the night before, and the operator picked the wrong day part (15:15 instead of 21:15).

**5 Whys Analysis**

Why 1: Why were B-003 operations slow? Because its account partition was locked.
Why 2: Why was it locked? Because the teller sync job was running.
Why 3: Why was it running in business hours? Because it was rescheduled by hand to the wrong time.
Why 4: Why by hand? Because the job has no automatic retry window.
Why 5: Why no validation? Because the scheduler accepts any time for branch jobs.

**Corrective Actions**

- Automatic retry window for branch sync jobs (21:00-05:00 only).
- Scheduler validation that rejects branch jobs during business hours.

**Metadata**

Date: 2025-06-23
Company: Banco Andino (demo) — synthetic document
Scenario: branch-degraded
Apps: web-banking, mobile-api
Steps: GET /banca/cuentas/saldos, POST /banca/transferencias
Branch: B-003
Exceptions: System.TimeoutException
