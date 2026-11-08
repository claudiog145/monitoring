**Root Cause Analysis**

Mobile API release 7.4 — NullReferenceException storm on mobile transfers

**Bottom Line Up Front**

On 2026-06-04 mobile-api release 7.4 introduced a mapping of favourite beneficiaries without a null check. For customers with no favourites, POST /api/mobile/v2/transfers threw System.NullReferenceException in BeneficiaryMapper.MapFavourites. About 60 % of mobile transfers failed for 22 minutes until the release was rolled back. Web transfers (POST /banca/transferencias) were not affected.

**What Happened**

At 12:40 (UTC-5), during the lunch peak, the Exceptions KPI of banca-movil jumped from ~3 to about 20 exceptions per 5-minute bucket. All new exceptions had the same problemId (System.NullReferenceException at BeneficiaryMapper.MapFavourites). Latency and the dependencies to core-banking and antifraud were normal, which pointed to the application code and not to a partner. Completed transfers dropped about 25 %, below the 50 % threshold of the business KPI "Transferencias completadas", which did not fire: the Exceptions KPI was the signal.

**Impact**

- 22 minutes; ~80 mobile transfers failed and were retried later by most customers.
- All branches; only the mobile channel.
- No partner involved.

**Timeline (UTC-5)**

- 12:35 Release 7.4 of mobile-api enabled for 100 % of users.
- 12:40 KPI anomaly: Exceptions (banca-movil), exception rate above its 4 % threshold.
- 12:47 Triage links the anomaly to the 7.4 deployment (same problemId, new since the release).
- 12:57 Rollback to 7.3; exceptions back to baseline at 13:02.

**Root Cause — Plain Language**

The new code assumed every customer has at least one favourite beneficiary. Customers without favourites received an empty payload, the mapper dereferenced it, and the transfer failed before reaching the core.

**5 Whys Analysis**

Why 1: Why did mobile transfers fail? Because BeneficiaryMapper.MapFavourites threw NullReferenceException.
Why 2: Why was the payload null? Because customers without favourites get no favourites object.
Why 3: Why was it not handled? Because the contract test data always had favourites.
Why 4: Why was the test data incomplete? Because it was generated from a single persona.
Why 5: Why did the canary not stop it? Because the canary stage was skipped to meet the release window.

**Corrective Actions**

- Null-safe mapping and a contract test with empty favourites (done).
- Re-enable the 5 % canary for mobile-api with automatic rollback on the Exceptions KPI.
- Add personas without favourites to the test data generator.

**Metadata**

Date: 2026-06-04
Company: Banco Andino (demo) — synthetic document
Scenario: mobile-transfer-exceptions
Apps: mobile-api
Steps: POST /api/mobile/v2/transfers
Partner: none
Exceptions: System.NullReferenceException
