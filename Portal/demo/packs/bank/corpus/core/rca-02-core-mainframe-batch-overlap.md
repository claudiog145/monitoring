**Root Cause Analysis**

Mainframe end-of-day batch overlapped with online window — core-banking latency

**Bottom Line Up Front**

On 2025-11-28 the mainframe end-of-day batch started 50 minutes early because of a scheduler calendar error. Online CICS transactions BAL1, TRF1 and DEB1 competed with the batch for CPU, and every core-banking call made by core-gateway slowed down about four times for 35 minutes. Balances on web-banking and mobile-api and POST /core/v1/transfers were affected; a small number of transfers failed with System.TimeoutException.

**What Happened**

At 19:10 (UTC-5), during the after-work peak, the Latency P95 KPI of the core service rose above its baseline. Dependencies to cics.mainframe.bancoandino.internal (CICS BAL1, CICS DEB1) and mq.mainframe.bancoandino.internal (PUT TRF1.REQUEST) showed the same multiplier, while core-gateway CPU and memory were normal. The infrastructure team found the batch job chain EOD-01 running in the online window.

**Impact**

- 35 minutes of slow balances and transfers on every channel; transfer failures peaked at 6 %.
- All branches affected.
- Partner: core-banking.

**Timeline (UTC-5)**

- 19:00 EOD-01 starts (planned 19:50).
- 19:10 KPI anomaly on core Latency P95.
- 19:24 Root cause identified in the scheduler; batch paused.
- 19:45 Latency back to baseline.

**Root Cause — Plain Language**

The batch calendar was not updated for the month-end holiday shift, so the nightly batch started while customers were still operating online.

**5 Whys Analysis**

Why 1: Why were core calls slow? Because online CICS transactions competed with the batch for CPU.
Why 2: Why was the batch running? Because the scheduler started it 50 minutes early.
Why 3: Why early? Because the holiday calendar of the scheduler was not updated.
Why 4: Why was it not updated? Because the calendar update is a manual yearly task without an owner.
Why 5: Why is there no guard? Because the scheduler does not check online volume before starting the batch.

**Corrective Actions**

- Assign an owner and a checklist to the scheduler calendar.
- Add a guard: EOD-01 waits while online TRF1 volume is above the evening threshold.
- Alert when core-banking dependency latency exceeds 3x baseline for 10 minutes.

**Metadata**

Date: 2025-11-28
Company: Banco Andino (demo) — synthetic document
Scenario: core-gw-slow-deploy
Apps: core-gateway, web-banking, mobile-api
Steps: GET /core/v1/accounts/balances, POST /core/v1/transfers
Partner: core-banking
Exceptions: System.TimeoutException
