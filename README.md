# Monitoring — Agentic Operations Platform

A .NET 10 platform that watches customer booking journeys across Application Insights, and runs a
small army of autonomous agents ("monkeys") that detect problems, investigate them, and write up
what they found.

This repository was copied from the `feature/company-profile-decoupling` branch of
`arriviainc-softeng/ai-reliable-platform`. The git history did not come with it — the commit
history of the work that produced this code is summarised in
[`memory-bank/40-session-log.md`](memory-bank/40-session-log.md).

---

## Start here

| If you want to… | Read |
|---|---|
| Understand the whole platform | [`PLATFORM_GUIDE.md`](PLATFORM_GUIDE.md) |
| Work on this code as an AI agent | [`memory-bank/`](memory-bank/) — read it in order, it is written for you |
| Run it locally | The Quick start below |
| Know what is broken and why | [`memory-bank/50-open-issues.md`](memory-bank/50-open-issues.md) |

---

## What this actually does

```
Application Insights (3 resources, 2 Azure tenants)
          │
          ▼
   Scout Monkeys ──────► priority board ──► investigation package
   (detect)                                        │
                                                   ▼
                                          Inspector Monkey
                                          (5 specialists debate,
                                           a planner synthesises)
                                                   │
                                                   ▼
                                          verdict: resolve │ escalate
                                                   │
                                                   ▼
                                             RCA Agent
                                          (writes the report)
```

Everything an agent concludes is checked against the real system before it is allowed to count —
see "Grounding" in the guide. That is the part of this platform that is genuinely unusual, and it
is the part most worth not breaking.

---

## Quick start (Docker)

```powershell
cd Portal
docker-compose up -d --build
```

Then open <http://localhost:5080>.

Five containers come up: SQL Server, Azurite, Functions, Functions-QA, Web. First boot seeds the
database, including the club inventory and the company profile.

### Before the agents can see real data

The agents read Application Insights with **your** Azure token. There is no service principal in
local development, so you paste a token in and the platform stores it encrypted.

```powershell
az account get-access-token `
  --tenant 60396ff5-e90f-4bc5-aced-a4f462f2636e `
  --scope "https://api.applicationinsights.io/.default" `
  --query accessToken -o tsv
```

Paste it at **Settings → AppInsights Tokens** with tenant key `arrivia`.

> The scope matters. A token minted for `https://management.azure.com/` is a perfectly valid token
> that Application Insights rejects, and the failure arrives as a confusing 401. Use the scope
> above exactly.

Tokens last about an hour. There is no automatic refresh — see
[`memory-bank/50-open-issues.md`](memory-bank/50-open-issues.md).

### Optional: let the Docs and Code agents work

Two of the Inspector's five specialists search Azure DevOps. They need their own token, minted for
the Azure DevOps resource:

```powershell
az account get-access-token `
  --tenant ea4d58b6-6980-4312-be9a-ab68edfa574c `
  --scope "499b84ac-1321-427f-aa17-267ca6975798/.default" `
  --query accessToken -o tsv
```

Store it under tenant key `azure-devops`.

---

## Build and test

```powershell
cd Portal
dotnet build JourneyMonitor.slnx
dotnet test tests/JourneyMonitor.Tests/JourneyMonitor.Tests.csproj
```

Expect **1109 passing, 1 failing**. The failure (`CouplingInventoryTests`) is known, pre-existing
and explained in [`memory-bank/50-open-issues.md`](memory-bank/50-open-issues.md) — it is not
something you broke.

---

## Repository layout

```
Portal/
  src/
    JourneyMonitor.Core/            Domain + use cases. No Azure, no HTTP, no EF. Unit-testable.
    JourneyMonitor.Infrastructure/  Application Insights, SQL, KQL text, Azure DevOps, LLM clients.
    JourneyMonitor.Functions/       Timers and HTTP endpoints (the API).
    JourneyMonitor.Web/             The dashboard. Calls Functions over HTTP.
  tests/JourneyMonitor.Tests/       1110 tests.
  docker-compose.yml                The whole local stack.
docs/                               Product and engineering notes (606 files).
openwiki/                           Generated architecture documentation.
memory-bank/                        Persistent context for AI agents working in this repo.
PLATFORM_GUIDE.md                   The full explanation of how everything works.
```

The dependency rule is one-way: `Web → Functions → Core ← Infrastructure`. Core never references
Infrastructure. If you find yourself wanting to, read `Portal/AGENTS.md` first.

---

## A word about `.env`

`Portal/.env` holds real secrets (a GitHub OAuth token used for the LLM calls). It is in
`.gitignore` and must stay there. `Portal/.env.example` documents what belongs in it.

Never commit it, and never paste its contents into an issue, a prompt, or a log.
