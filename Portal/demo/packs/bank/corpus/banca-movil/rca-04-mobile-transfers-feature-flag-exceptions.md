**Root Cause Analysis**

Mobile transfers — exceptions after enabling the "new beneficiaries" feature flag

**Bottom Line Up Front**

On 2025-08-19 the feature flag NewBeneficiaries was switched on for all mobile customers. The mobile-api code path behind the flag did not handle beneficiaries migrated from the old model, and POST /api/mobile/v2/transfers threw System.NullReferenceException for about 30 % of requests during 18 minutes. Switching the flag off restored the service.

**What Happened**

At 18:05 (UTC-5) the Exceptions KPI of banca-movil fired. The exception type was new (System.NullReferenceException) and appeared only on POST /api/mobile/v2/transfers. Other mobile steps, the web channel and the core-banking dependency were healthy. The change log showed the flag change at 18:02.

**Impact**

- 18 minutes; ~60 failed mobile transfers.
- All branches; mobile channel only.

**Timeline (UTC-5)**

- 18:02 Flag NewBeneficiaries set to 100 %.
- 18:05 KPI anomaly on Exceptions (banca-movil).
- 18:16 Flag switched off.
- 18:20 Exceptions back to baseline.

**Root Cause — Plain Language**

Beneficiaries migrated from the old model had no "alias" field. The new code read it without checking and failed.

**5 Whys Analysis**

Why 1: Why did transfers fail? Because the new code read a missing field.
Why 2: Why was the field missing? Because migrated beneficiaries never had it.
Why 3: Why did tests not catch it? Because test data contained only beneficiaries created with the new model.
Why 4: Why was the flag enabled for everyone at once? Because the flag tool had no percentage rollout for mobile-api.
Why 5: Why was there no automatic stop? Because flags were not linked to KPI alerts.

**Corrective Actions**

- Backfill the alias field for migrated beneficiaries.
- Percentage rollout for mobile-api flags; automatic flag-off on an Exceptions KPI anomaly.

**Metadata**

Date: 2025-08-19
Company: Banco Andino (demo) — synthetic document
Scenario: mobile-transfer-exceptions
Apps: mobile-api
Steps: POST /api/mobile/v2/transfers
Partner: none
Exceptions: System.NullReferenceException
