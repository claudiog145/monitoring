# Root Cause Analysis - Payment gateway timeouts after partner TLS certificate rotation

**Date:** 2025-09-22
**Company:** Mercado Libre (demo) - synthetic document, every name and number is invented
**Demo scenario:** `payment-gateway-outage`
**Services:** payments (`POST /api/payments`)
**Partner:** payment-gateway (`gateway.payments-partner.demo`)
**Severity:** SEV-2 - duration 52 minutes

## Bottom Line Up Front
The payment gateway rotated its TLS certificate to a new intermediate CA. Half of our payments pods had an outdated trust bundle, so their `POST /v1/charges` calls failed during the handshake. Charges failed for roughly 45 % of buyers on every site until the bundle was refreshed.

## What Happened
- 09:10 UTC - Exceptions KPI of `payments` rises: `System.Net.Http.HttpRequestException` against `gateway.payments-partner.demo`.
- 09:14 UTC - Payments approved drops 40 % compared with the same hour last week.
- 09:35 UTC - On-call correlates failures with specific pod instances (cloud_RoleInstance) - not with sites.
- 10:02 UTC - Trust bundle redeployed; failures stop.

## 5 Whys
1. Why did charges fail? Because TLS handshakes with the payment gateway failed on some pods.
2. Why only some pods? Because they ran an older base image without the new intermediate CA.
3. Why was the image old? Because the payments deployment pins the base image and the nightly rebuild was disabled.
4. Why was the rebuild disabled? Because it once broke the build during a Hot Sale freeze and was never re-enabled.
5. Why was it not re-enabled? Because no owner was assigned to freeze exceptions.

## Corrective Actions
- Re-enable nightly base-image rebuilds for payments.
- Subscribe to partner certificate-rotation notices.
- Add an alert on dependency failures per partner (payment-gateway, card-acquirer).
