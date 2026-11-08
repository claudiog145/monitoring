# Root Cause Analysis - Stock cache stampede

**Date:** 2026-09-19  
**Company:** Fixture Shop (demo) - synthetic document for tests

## Bottom Line Up Front
Synthetic incident 'stock-cache' affected step GET /api/stock (partner: inventory-db). Service was restored after mitigation.

## What Happened
Requests to GET /api/stock degraded for 40 minutes. Exceptions and latency rose while traffic stayed normal.

## 5 Whys
1. Why did GET /api/stock fail? Because a downstream call degraded.
2. Why did it degrade? Because capacity was exhausted.
3. Why was capacity exhausted? Because a limit was too low.
4. Why was the limit too low? Because it was never reviewed.
5. Why was it not reviewed? Because there was no owner.

## Corrective Actions
- Add an alert on the step latency.
- Review the limit quarterly.