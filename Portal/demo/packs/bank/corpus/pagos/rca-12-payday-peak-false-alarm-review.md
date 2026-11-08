**Root Cause Analysis**

Payday (quincena) traffic peak — review of a legitimate peak that must not page

**Bottom Line Up Front**

On 2026-08-31 (month-end and payday) traffic grew about 50 % on every channel for four hours: balances, transfers, bill payments and card authorizations. Latency, error rate and partner failure rates stayed normal. In 2025 the old threshold-based alerting paged the on-call for this pattern three times; with the current KPIs (same-hour-of-day baselines on latency and business counts, static thresholds on error and partner failure rates, Decrease direction on business counts) no anomaly was raised, neither during the peak nor when traffic went back to normal. This review documents the pattern so that future peaks are recognised as legitimate.

**What Happened**

Salaries were credited at 06:00 (UTC-5). From 11:00 the request count of web-banking, mobile-api and payments-svc rose by ~50 %, with the usual lunch and after-work shape. Latency P95 stayed within 10 % of baseline, Exceptions grew in proportion to traffic (the error rate stayed at ~0.6 %), and the business KPIs "Transferencias completadas" and "Pagos de servicios" increased — which is not an anomaly for a Decrease KPI.

**Impact**

- None. No incident; capacity headroom stayed above 40 % on core-gateway.

**Timeline (UTC-5)**

- 06:00 Payroll credits posted by the core.
- 11:00-15:00 Traffic +50 % on all channels.
- 15:30 Traffic back to the usual curve.

**Root Cause — Plain Language**

There was no failure. Payday and month-end bring a predictable, legitimate surge of customers.

**5 Whys Analysis**

Why 1: Why did traffic grow? Because salaries were credited.
Why 2: Why did it not page this time? Because KPIs look at latency, error rates and drops, not at raw volume increases.
Why 3: Why did it page in 2025? Because the old rule alerted on absolute request counts.
Why 4: Why was that rule there? Because it was the only signal available before App Insights KPIs.
Why 5: Why keep this review? So the Inspector can explain a future peak as payday instead of an incident.

**Corrective Actions**

- Keep business count KPIs with direction Decrease only, compared with the same hour of the previous days: a baseline of the last two hours would read the end of a four-hour peak as a drop.
- Publish the payday calendar to the operations dashboard.

**Metadata**

Date: 2026-08-31
Company: Banco Andino (demo) — synthetic document
Scenario: payday-peak
Apps: web-banking, mobile-api, payments-svc, core-gateway
Steps: GET /banca/cuentas/saldos, POST /banca/transferencias, POST /payments/v1/card-authorizations
Partner: none
