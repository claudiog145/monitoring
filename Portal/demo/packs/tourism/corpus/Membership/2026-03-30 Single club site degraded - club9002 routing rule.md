# Root Cause Analysis - Single club site degraded (club 9002 routing rule)

**Date:** 2026-03-30
**Company:** Arrivia (demo) - synthetic document written for the demo stack; not a real incident
**Scenario (demo):** `club-degraded`
**Entity:** club 9002 (VacationClubId 9002, host club9002.travel-demo.example) - **Apps:** all (sor-frontend, club-frontend, cruise-core, cruise-web-api, air-ui, air-api)
**Severity:** Sev 3 - **Duration:** 64 minutes

## Bottom Line Up Front
A branding update for club 9002 added an edge routing rule that sent the club's traffic through a debugging origin with request logging enabled. Every page of that one club became five times slower and about 40 % of its requests timed out (`System.TimeoutException`). All other clubs were healthy. Removing the rule restored the club.

## Impact
- Members of club 9002 only: slow pages on every vertical and intermittent timeouts for an hour.
- Club 9002 is the second-largest club (about 11 % of traffic), so the domain error-rate KPIs crossed their thresholds too, but they could not say who was affected; every entity-grain (per club) trigger named club 9002.

## Timeline (UTC)
- 12:05 Branding update for club 9002 published with a new routing rule.
- 12:15 Entity-grain Exceptions KPIs fire for club 9002 on several verticals within minutes of each other, and for no other club; domain Exceptions KPIs fire at the same time without naming a club.
- 12:30 On-call opens the per-club latency view: P95 for club 9002 is about five times its baseline, every other club is normal.
- 12:48 Club account manager reports member complaints.
- 13:09 Routing rule removed; club 9002 back to normal.

## What Happened
Club branding is applied per host. The update for club9002.travel-demo.example included a routing rule copied from a troubleshooting session that forced requests through a logging origin with limited capacity. Because the rule matched only that host, only that club was affected.

## Root Cause
A per-club configuration change carried a temporary troubleshooting rule into production, and per-club changes did not require a review.

## 5 Whys
1. Why was club 9002 slow? Because its requests went through a debugging origin.
2. Why did they go there? Because a routing rule for its host pointed at that origin.
3. Why did the rule exist? Because it was copied from a troubleshooting session into the branding update.
4. Why was it not noticed? Because per-club branding changes are self-service without review.
5. Why did the first alerts not point at the club? Because domain-level KPIs aggregate every club; only the entity-grain KPIs name the affected one.

## Corrective Actions
- Routing rules are no longer editable through the branding workflow.
- Entity-grain latency and exception KPIs stay enabled for every vertical.
- The account manager playbook links to the per-club health view.

## Lessons Learned
If only one club is affected, look at what is configured per club: host, branding and routing.
