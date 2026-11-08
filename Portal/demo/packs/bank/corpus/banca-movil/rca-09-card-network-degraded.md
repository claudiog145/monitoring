**Root Cause Analysis**

Card network issuer API degraded — customers cannot lock cards from the app

**Bottom Line Up Front**

On 2026-03-09 the issuer API of the card network Red Cóndor (partner card-network) degraded after a capacity change on its side. For 33 minutes half of the calls to POST /issuer/v1/cards/block and GET /issuer/v1/cards/status failed and the rest were four times slower. In mobile-api, POST /api/mobile/v2/cards/lock and GET /api/mobile/v2/cards failed with System.Net.Http.HttpRequestException. Customers who wanted to block a lost card were redirected to the call center.

**What Happened**

At 20:05 (UTC-5) the PartnerFailures KPI of banca-movil flagged card-network above 45 % failed calls. The mobile Exceptions KPI fired two buckets later. Core-banking, antifraud and OTP calls were healthy. The network confirmed a capacity incident at 20:20.

**Impact**

- 33 minutes; ~40 card lock attempts failed; call-center volume tripled.
- All branches; mobile channel.
- Partner: card-network.

**Timeline (UTC-5)**

- 20:02 Red Cóndor applies a capacity change.
- 20:05 KPI anomaly: PartnerFailures (banca-movil, partner card-network).
- 20:12 App banner: "bloqueo de tarjetas por call center".
- 20:20 Network confirms incident.
- 20:38 Network recovers.

**Root Cause — Plain Language**

The card network reduced the capacity of the issuer API cluster serving our bank. The app had no fallback to block a card when the network is unavailable.

**5 Whys Analysis**

Why 1: Why did card locks fail? Because the card network API failed.
Why 2: Why did it fail? Because the network reduced capacity on its side.
Why 3: Why did customers notice? Because the lock is synchronous with the network.
Why 4: Why synchronous? Because the bank has no local block list for its own cards.
Why 5: Why no local block list? Because it was descoped from the 2024 cards project.

**Corrective Actions**

- Local block list in payments-svc so a card lock never depends on the network.
- Contractual notice for capacity changes from the network.

**Metadata**

Date: 2026-03-09
Company: Banco Andino (demo) — synthetic document
Scenario: card-network-degraded
Apps: mobile-api
Steps: POST /api/mobile/v2/cards/lock, GET /api/mobile/v2/cards
Partner: card-network
Exceptions: System.Net.Http.HttpRequestException
