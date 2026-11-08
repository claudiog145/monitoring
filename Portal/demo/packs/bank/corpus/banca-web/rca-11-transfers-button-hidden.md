**Root Cause Analysis**

Silent drop of transfers — "Transferir" button hidden by a front-end release

**Bottom Line Up Front**

On 2026-04-14 a front-end release shipped a feature-flag default that hid the "Transferir" button for most customers on web-banking and in the mobile app. For 47 minutes transfers (POST /banca/transferencias, POST /api/mobile/v2/transfers) fell about 75 % without any exception, latency change or partner failure. Only the business KPI "Transferencias completadas" detected it.

**What Happened**

At 13:10 (UTC-5) the business KPI "Transferencias completadas" (TransferCompleted events, web + mobile) dropped far below its 2-hour median. Application KPIs (Latency P95, Exceptions) and partner KPIs were all normal, and login and balance traffic was unchanged. The Inspector pointed at a drop in requests to the transfer steps only, which led to the release of 13:05.

**Impact**

- 47 minutes; an estimated 600 transfers were not attempted (customers came back later or used branches).
- All branches; web and mobile.
- No partner involved.

**Timeline (UTC-5)**

- 13:05 Front-end release with flag TransferEntryV2 default "off".
- 13:10 KPI anomaly: Transferencias completadas (Decrease).
- 13:30 Product team reproduces the hidden button.
- 13:52 Flag fixed; transfers recover by 14:00.

**Root Cause — Plain Language**

The new transfer entry point was guarded by a flag whose default was "off", and the release removed the old entry point. Nothing failed; customers simply could not find the button.

**5 Whys Analysis**

Why 1: Why did transfers drop? Because customers could not find the button.
Why 2: Why was it hidden? Because the new flag defaulted to off and the old button was removed.
Why 3: Why was the default wrong? Because flag defaults are not reviewed in the release checklist.
Why 4: Why did no technical alert fire? Because no request failed.
Why 5: Why was the business KPI the only signal? Because funnel volume per step was not monitored before 2026.

**Corrective Actions**

- Flag defaults in the release checklist; never remove an entry point and add its replacement behind an "off" flag in the same release.
- Keep the business KPI "Transferencias completadas" on 5-minute buckets.

**Metadata**

Date: 2026-04-14
Company: Banco Andino (demo) — synthetic document
Scenario: transfers-drop
Apps: web-banking, mobile-api
Steps: POST /banca/transferencias, POST /api/mobile/v2/transfers
Partner: none
Business event: TransferCompleted
