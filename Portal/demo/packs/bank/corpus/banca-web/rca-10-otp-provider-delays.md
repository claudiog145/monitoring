**Root Cause Analysis**

SMS/OTP provider delays — slow and failed logins on web and mobile

**Bottom Line Up Front**

On 2026-02-27 the SMS/OTP provider Mensajería Andes (partner otp-sms) queued messages after a carrier routing problem. For 27 minutes POST /v1/otp/send was eight times slower and most of the calls failed. Logins on web-banking (POST /auth/login) and mobile-api (POST /api/mobile/v2/auth/login) became slow and the business KPI "Logins exitosos" fell to less than half of the same hour of the previous days.

**What Happened**

At 08:40 (UTC-5), during the morning ramp-up, the Latency P95 KPI of banca-web and banca-movil fired for the login steps. Dependency telemetry showed the latency concentrated on otp.mensajeriaandes.demo; failed sends surfaced as System.Net.Http.HttpRequestException and as System.TimeoutException ("OTP challenge expired before validation") when the SMS arrived too late. Antifraud scoring was healthy.

**Impact**

- 27 minutes; ~200 failed logins, most customers retried successfully.
- All branches; web and mobile.
- Partner: otp-sms.

**Timeline (UTC-5)**

- 08:37 Provider starts queuing messages.
- 08:40 KPI anomaly: Latency P95 (banca-web).
- 08:46 PartnerFailures (banca-movil, otp-sms) above threshold.
- 08:55 Provider confirms carrier routing problem.
- 09:04 Messages flowing normally.

**Root Cause — Plain Language**

One mobile carrier changed its routing and the provider's messages to that carrier queued. The bank has a single OTP provider and no alternative factor during the incident.

**5 Whys Analysis**

Why 1: Why did logins fail? Because OTP codes arrived late or never.
Why 2: Why late? Because the provider queued messages.
Why 3: Why queued? Because a carrier changed routing without notice.
Why 4: Why did it affect all customers? Because there is one OTP provider.
Why 5: Why only one? Because the second provider contract expired in 2025.

**Corrective Actions**

- Second OTP provider with automatic failover on latency.
- Offer push-notification approval in the app as an alternative factor.

**Metadata**

Date: 2026-02-27
Company: Banco Andino (demo) — synthetic document
Scenario: otp-delays
Apps: web-banking, mobile-api
Steps: POST /auth/login, POST /api/mobile/v2/auth/login
Partner: otp-sms
Exceptions: System.Net.Http.HttpRequestException, System.TimeoutException
