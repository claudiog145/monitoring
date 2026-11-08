**Root Cause Analysis**

ACH clearing house outage — interbank transfers rejected

**Bottom Line Up Front**

On 2026-07-31 (month-end) the ACH clearing house CompensAndes (partner ach-clearing) suffered a storage failure in its primary site. For 38 minutes it rejected about 85 % of credit transfers and answered the rest three times slower. payments-svc returned errors on POST /payments/v1/ach/transfers; the "Rechazo interbancario (%)" business KPI rose from ~1 % to 84 %. Internal transfers (same bank) kept working. Pending transfers were resubmitted automatically after recovery.

**What Happened**

At 16:22 (UTC-5) the PartnerFailures KPI of the pagos service flagged ach-clearing: failed calls to ach.compensandes.demo (POST /ach/v3/credit-transfers) went above 80 %. payments-svc logged System.Net.Http.HttpRequestException for each failed call. Antifraud scoring and core-banking calls stayed healthy. The clearing house confirmed the incident on its status channel at 16:35.

**Impact**

- 38 minutes; ~180 interbank transfers rejected and queued for retry.
- All branches; web and mobile customers saw "transferencia interbancaria no disponible".
- Partner: ach-clearing (external).

**Timeline (UTC-5)**

- 16:20 CompensAndes primary storage fails over.
- 16:22 KPI anomaly: PartnerFailures (pagos, partner ach-clearing).
- 16:25 Business KPI "Rechazo interbancario (%)" above its 25 % threshold.
- 16:35 Clearing house confirms incident; bank publishes a channel banner.
- 16:58 Clearing house recovers; retry queue drained by 17:20.

**Root Cause — Plain Language**

The external clearing house lost its primary storage. The bank depended on a single clearing endpoint with no store-and-forward for interbank transfers.

**5 Whys Analysis**

Why 1: Why were interbank transfers rejected? Because the clearing house rejected the credit transfers.
Why 2: Why did it reject them? Because its primary storage failed.
Why 3: Why did customers see errors? Because payments-svc fails synchronously when the clearing house fails.
Why 4: Why synchronously? Because the store-and-forward queue was only used for scheduled transfers.
Why 5: Why only scheduled? Because instant interbank transfers were designed before the clearing house SLA changed.

**Corrective Actions**

- Accept and queue instant interbank transfers when the clearing house is down (store-and-forward), notifying the customer.
- Subscribe the on-call to the clearing house status feed.

**Metadata**

Date: 2026-07-31
Company: Banco Andino (demo) — synthetic document
Scenario: ach-outage
Apps: payments-svc
Steps: POST /payments/v1/ach/transfers
Partner: ach-clearing
Exceptions: System.Net.Http.HttpRequestException
