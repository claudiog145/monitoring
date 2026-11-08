**Root Cause Analysis**

Core-gateway 4.18 — Connection pool regression slows balances and transfers on every channel

**Bottom Line Up Front**

On 2026-09-15 a core-gateway release (4.18) reduced the CICS/MQ connection pool from 64 to 16 sessions. Every call to the core banking mainframe (partner core-banking) became about five times slower for 41 minutes. Balances and transfers slowed down on web-banking and mobile-api, and POST /core/v1/transfers started failing with System.TimeoutException ("CICS transaction TRF1 timed out after 5000 ms"). Rolling back to 4.17 restored service. No money was lost; about 150 transfers were retried by customers.

**What Happened**

At 10:12 (UTC-5) the 4.18 deployment of core-gateway finished. Within five minutes the Latency P95 KPI of the core service crossed its 7-day baseline by more than 300 %, followed by banca-web and banca-movil (GET /banca/cuentas/saldos, GET /api/mobile/v2/accounts/balance, POST /banca/transferencias, POST /api/mobile/v2/transfers). Exceptions on core-gateway rose from ~2 to ~10 per 5-minute bucket, almost all System.TimeoutException on CoreGateway.PostTransfer. Traffic volume stayed normal, which ruled out a load spike.

**Impact**

- 41 minutes of degraded service on all channels; transfer success rate fell from 99.3 % to 91 %.
- Branches: all (the effect was not limited to one BranchId).
- Partners: core-banking (mainframe) calls slowed; antifraud, card-network and ach-clearing were healthy.

**Timeline (UTC-5)**

- 10:12 core-gateway 4.18 deployed by the release pipeline.
- 10:17 KPI anomaly: Latency P95 (core, domain) +310 % vs baseline.
- 10:21 Triage groups core, banca-web and banca-movil anomalies under partner core-banking.
- 10:38 On-call correlates the start with the deployment; rollback approved.
- 10:53 Rollback to 4.17 complete; latency back to baseline at 10:58.

**Root Cause — Plain Language**

A configuration default changed in the new version of the mainframe connector library. The pool of sessions that core-gateway keeps open to the mainframe shrank to a quarter of its size, so requests queued waiting for a free session.

**5 Whys Analysis**

Why 1: Why were balances and transfers slow? Because every call through core-gateway waited for a mainframe session.
Why 2: Why did calls wait? Because the CICS/MQ pool had only 16 sessions instead of 64.
Why 3: Why did the pool shrink? Because connector library 9.x changed the default MaxSessions and 4.18 relied on the default.
Why 4: Why was it not caught in pre-production? Because the performance test runs with 5 % of production concurrency.
Why 5: Why is the pool size not explicit? Because the setting was never part of the core-gateway configuration baseline.

**Corrective Actions**

- Pin MaxSessions=64 explicitly in core-gateway configuration (done).
- Add a pool-saturation alert on core-gateway (queue wait > 200 ms).
- Run the core performance test at 50 % of production concurrency before every core-gateway release.

**Metadata**

Date: 2026-09-15
Company: Banco Andino (demo) — synthetic document
Scenario: core-gw-slow-deploy
Apps: core-gateway, web-banking, mobile-api
Steps: POST /core/v1/transfers, GET /banca/cuentas/saldos, POST /api/mobile/v2/transfers
Partner: core-banking
Exceptions: System.TimeoutException
