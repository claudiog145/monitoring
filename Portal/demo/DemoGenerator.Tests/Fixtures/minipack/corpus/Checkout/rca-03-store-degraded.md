# Root Cause Analysis - Store S-03 cart degradation

**Date:** 2026-03-13  
**Company:** Fixture Shop (demo) - synthetic document for tests

## Bottom Line Up Front
Synthetic incident 'store-degraded' affected step GET /checkout/cart (partner: none). Service was restored after mitigation.

## What Happened
Requests to GET /checkout/cart degraded for 40 minutes. Exceptions and latency rose while traffic stayed normal.

## 5 Whys
1. Why did GET /checkout/cart fail? Because a downstream call degraded.
2. Why did it degrade? Because capacity was exhausted.
3. Why was capacity exhausted? Because a limit was too low.
4. Why was the limit too low? Because it was never reviewed.
5. Why was it not reviewed? Because there was no owner.

## Corrective Actions
- Add an alert on the step latency.
- Review the limit quarterly.