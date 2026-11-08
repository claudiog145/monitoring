**Root Cause Analysis**

ACH mutual-TLS certificate expired — interbank transfers failing

**Bottom Line Up Front**

On 2025-12-15 the client certificate that payments-svc presents to the ACH clearing house (partner ach-clearing) expired at 09:00 (UTC-5). Every call to POST /ach/v3/credit-transfers failed the TLS handshake for 31 minutes until a renewed certificate was deployed. Interbank transfers were rejected; internal transfers and bill payments were not affected.

**What Happened**

At 09:04 the PartnerFailures KPI of the pagos service flagged ach-clearing at 100 % failed calls. payments-svc exceptions on POST /payments/v1/ach/transfers were System.Net.Http.HttpRequestException ("The SSL connection could not be established"). The business KPI "Rechazo interbancario (%)" reached 97 %.

**Impact**

- 31 minutes; ~120 interbank transfers rejected (queued for retry).
- All branches. Partner: ach-clearing.

**Timeline (UTC-5)**

- 09:00 Certificate expires.
- 09:04 KPI anomaly: PartnerFailures (pagos, ach-clearing).
- 09:18 Cause identified from the handshake error.
- 09:31 Renewed certificate deployed; success rate back to normal at 09:35.

**Root Cause — Plain Language**

The certificate renewal reminder went to a mailbox of a team that no longer exists, so nobody renewed it in time.

**5 Whys Analysis**

Why 1: Why did ACH calls fail? Because the TLS handshake failed.
Why 2: Why did it fail? Because the client certificate had expired.
Why 3: Why was it not renewed? Because the reminder went to an obsolete mailbox.
Why 4: Why was the mailbox obsolete? Because ownership of payments-svc moved teams in 2025.
Why 5: Why was there no technical alert? Because certificate expiry was not monitored.

**Corrective Actions**

- Monitor certificate expiry of every partner connection (alert at 30 and 7 days).
- Move partner certificates to automated renewal.

**Metadata**

Date: 2025-12-15
Company: Banco Andino (demo) — synthetic document
Scenario: ach-outage
Apps: payments-svc
Steps: POST /payments/v1/ach/transfers
Partner: ach-clearing
Exceptions: System.Net.Http.HttpRequestException
