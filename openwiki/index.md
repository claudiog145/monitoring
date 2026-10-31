---
okf_version: "0.1"
---

# Files

- [JourneyMonitor architecture](architecture.md) - Explains the main runtime components of JourneyMonitor, dependency direction, Web-to-Functions proxying, Application Insights and SQL infrastructure, and authentication boundaries.
- [JourneyMonitor data flows](data-flows.md) - Documents how JourneyMonitor moves Application Insights telemetry into SQL journey snapshots, dashboard metrics, overview KPIs, and incident records, and how those projections are read by the portal.
- [Monitoring agents](monitoring-agents.md) - Explains the Latency Spike Monkey and Doctor Monkey architecture, detector rollout choices, persisted findings and settings, LLM safeguards, and sensitive API gates.
- [JourneyMonitor quickstart](quickstart.md) - Entry point for the OpenWiki documentation of the .NET JourneyMonitor portal, including setup, repository layout, runtime areas, and where future agents should start.
