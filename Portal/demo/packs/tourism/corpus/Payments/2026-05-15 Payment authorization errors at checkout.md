# Root Cause Analysis - Payment authorization errors at checkout

**Date:** 2026-05-15
**Company:** Arrivia (demo) - synthetic document written for the demo stack; not a real incident
**Scenario (demo):** `payment-declines`
**Verticals:** Shared Booking Services, Cruises, Air - **Apps:** sor-frontend, cruise-core, air-api - **Stages:** 07 Booking (`POST /checkout/completepurchase`), 09 Payment (`POST /cruisepayment.do`), 07 Air Booking (`POST /api/air/book`)
**Partners involved:** payment-gateway
**Severity:** Sev 1 - **Duration:** 31 minutes

## Bottom Line Up Front
The payment gateway rotated its TLS intermediate certificate and one of its two regional endpoints served an incomplete chain. Half of the authorization calls from every vertical failed and surfaced as `Travel.Payments.PaymentAuthorizationException`. Hotel/car checkout, cruise payment and air booking were all affected. The gateway fixed its chain; we also added the missing intermediate to our trust bundle.

## Impact
- About 50 % of payment authorizations failed for 31 minutes, all clubs and all verticals.
- Members retried and most bookings were recovered.

## Timeline (UTC)
- 20:15 Gateway certificate rotation in its secondary region.
- 20:18 Dependency failures to api.paygateway-demo.example jump to 50 %.
- 20:28 Exceptions KPI for Shared Booking Services fires; the exception rates of the other verticals rose too, some close to their thresholds.
- 20:46 Gateway corrects the chain; failures stop.

## What Happened
DNS round-robin sent half of our calls to the region with the incomplete chain. Our HTTP client could not build the chain and failed the TLS handshake. Because every vertical uses the same gateway, exceptions rose in every vertical at the same moment; the Shared Booking Services Exceptions KPI fired.

## Root Cause
External: an incomplete certificate chain at the gateway. Contributing: our trust store did not include the gateway's new intermediate.

## 5 Whys
1. Why did payments fail? Because TLS handshakes to the gateway failed for half of the calls.
2. Why did the handshake fail? Because one gateway region served an incomplete chain.
3. Why only half? Because DNS round-robin alternated between regions.
4. Why did our client not complete the chain itself? Because the intermediate was not in our trust bundle.
5. Why did we not know about the rotation? Because the notice went to an unmonitored mailbox.

## Corrective Actions
- Add the gateway's intermediates to the trust bundle and monitor its rotation notices.
- Retry once against the other region on handshake failure.
- Correlate simultaneous exception spikes across verticals into one incident.

## Lessons Learned
When several verticals fail at the same stage type at the same moment, look for the shared partner first.
