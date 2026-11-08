**Root Cause Analysis**

Branch B-003 (Arequipa - Yanahuara) — saturated regional link degrades only that branch

**Bottom Line Up Front**

On 2026-10-02 the regional MPLS link that serves branch B-003 saturated after a router firmware update disabled traffic shaping. Operations booked to BranchId B-003 were six times slower and a quarter of them failed with System.TimeoutException for 52 minutes, on every channel (web-banking, mobile-api, payments-svc). The rest of the branches were healthy. Re-enabling traffic shaping restored the service.

**What Happened**

At 11:15 (UTC-5) the entity-grain Latency KPIs of banca-web and banca-movil and the entity-grain Exceptions KPI of banca-web flagged entity B-003, and no other branch. B-003 carries about 7 % of the traffic, so a channel-wide (domain-grain) KPI can move as well and reach Triage without a branch; the entity view is what pointed at the branch. The Inspector grouped the anomalies by BranchId and showed B-003 as the only affected branch. Partners (core-banking, antifraud, card-network) were healthy for the other branches.

**Impact**

- 52 minutes; customers of branch B-003 (about 7 % of the traffic) saw slow balances, failed transfers and failed card authorizations.
- Branch: B-003 only.

**Timeline (UTC-5)**

- 11:05 Firmware update on the Arequipa regional router.
- 11:15 KPI anomaly: Latency P95 (banca-web, entity B-003).
- 11:30 Network team sees link utilization at 100 %.
- 11:57 Traffic shaping re-enabled; latency for B-003 back to normal at 12:07.

**Root Cause — Plain Language**

The router update reset its configuration to defaults and turned off the rule that prioritizes banking traffic. Backups and video traffic filled the link.

**5 Whys Analysis**

Why 1: Why were B-003 operations slow? Because the regional link was saturated.
Why 2: Why was it saturated? Because traffic shaping was disabled.
Why 3: Why was it disabled? Because the firmware update restored default settings.
Why 4: Why was it not verified? Because the post-change checklist did not include QoS.
Why 5: Why did the change happen in business hours? Because the change window policy excluded regional routers.

**Corrective Actions**

- Add QoS verification to the router post-change checklist.
- Include regional routers in the change-window policy (no changes 08:00-20:00).
- Keep entity-grain latency KPIs per branch enabled on every channel.

**Metadata**

Date: 2026-10-02
Company: Banco Andino (demo) — synthetic document
Scenario: branch-degraded
Apps: web-banking, mobile-api, payments-svc
Steps: GET /banca/cuentas/saldos, POST /banca/transferencias
Branch: B-003
Exceptions: System.TimeoutException
