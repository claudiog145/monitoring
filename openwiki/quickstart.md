---
type: Guide
title: JourneyMonitor quickstart
description: Entry point for the OpenWiki documentation of the .NET JourneyMonitor portal, including setup, repository layout, runtime areas, and where future agents should start.
tags: [quickstart, dotnet, azure-functions, operations-portal]
---

# JourneyMonitor quickstart

This repository is the .NET 10 / Azure port of arrivia's Agentic Operations Portal for **Customer Journeys**. All product code lives under `Portal/`, with root-level setup guidance in `README.md` and deeper migration notes in `Portal/README.md`, `Portal/ARCHITECTURE.md`, and `Portal/MIGRATION.md`.

Start here, then follow the concept pages for the area you need:

- [Architecture overview](architecture.md) explains how the Razor Pages host, Azure Functions, Core use cases, Infrastructure adapters, Application Insights, SQL, and authentication fit together.
- [Data flows](data-flows.md) explains how journey snapshots, overview/dashboard metrics, and exception incidents move from Application Insights into SQL-backed API responses.
- [Monitoring agents](monitoring-agents.md) explains the Latency Spike Monkey, Doctor Monkey, SQL-vs-KQL detector rollout, persisted settings, and sensitive endpoint gates.

## Local setup path

The intended local workflow is Visual Studio with two separate instances:

1. Open `Portal/JourneyMonitor.slnx` in Visual Studio 2026 with .NET 10 support.
2. Run `JourneyMonitor.Functions` in one instance. The Functions host listens on `http://localhost:7071`.
3. Run `JourneyMonitor.Web` in another instance with the HTTPS profile. The browser opens `https://localhost:7152`.
4. Use `az login` with Monitoring Reader access when you need live Application Insights data.

The root `README.md` calls out two operational hazards: do not use Visual Studio Multiple startup projects for this solution, and kill leftover `func.exe` or `JourneyMonitor.Web` processes when port `7071` or build outputs are locked.

## What the portal does

JourneyMonitor serves the legacy portal UI from `JourneyMonitor.Web/wwwroot` and keeps most rendering logic in browser JavaScript for migration parity. The browser talks to Web proxy endpoints, which forward to Azure Functions through `JourneyMonitorApiClient`; Functions then call pure Core use cases and Infrastructure adapters.

Customer Journey reads come from SQL snapshots via `/api/journeys/db`, while `/api/journeys/refresh` re-queries Application Insights and writes a new snapshot. Overview, Analytics, Exceptions, Simian Army, and Settings have been connected to real Functions/SQL-backed paths beyond the original static JSON mocks.

## Repository map

| Path | Purpose |
|---|---|
| `Portal/src/JourneyMonitor.Core` | Domain models, options, analytics, mappers, incident planning, monitoring agents, and use cases with no Azure/HTTP/Razor dependency. |
| `Portal/src/JourneyMonitor.Infrastructure` | Application Insights REST client, KQL resources, in-memory cache, demo fixtures, SQL/Dapper stores, monitoring repositories, and GitHub Copilot LLM client. |
| `Portal/src/JourneyMonitor.Functions` | Azure Functions isolated worker: HTTP APIs, timers for journey/incident sync, Latency Spike Monkey, and settings endpoints. |
| `Portal/src/JourneyMonitor.Web` | Razor Pages/static UI host, Microsoft Entra integration, and thin API proxy endpoints. |
| `Portal/tests/JourneyMonitor.Tests` | xUnit characterization and service tests for parity, analytics, filtering, monitoring agents, settings, incidents, and SQL-row assembly. |
| `docs/product` and `docs/sessions` | Product/session notes, especially the latency and Doctor Monkey implementation plans that explain why current source choices exist. |

## Change guidance for future agents

- Start with existing docs (`Portal/README.md`, `Portal/ARCHITECTURE.md`, `Portal/MIGRATION.md`) and the relevant wiki page before reading large source files.
- Keep business logic in `JourneyMonitor.Core`; Functions and Web endpoints should stay thin.
- Do not document or inspect secret values. Settings and tokens are configured by named options and secrets, not by copying credentials into docs.
- Use `dotnet test Portal/tests/JourneyMonitor.Tests/JourneyMonitor.Tests.csproj` as the main validation command when changing product logic.

## Backlog

- Deployment and publish scripts — `Portal/scripts/publish-*.ps1`; deferred because this pass focused on runtime architecture and product behavior.
- Full KQL reference — `Portal/src/JourneyMonitor.Infrastructure/Kql/*.txt`; deferred because query ownership is documented at the flow level and every query body is too detailed for the first wiki pass.
- UI renderer internals — `Portal/src/JourneyMonitor.Web/wwwroot/app.js`; deferred beyond API seams because the file is large and most renderer behavior remains legacy parity code.
