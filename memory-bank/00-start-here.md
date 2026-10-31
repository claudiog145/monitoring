# 00 — Start here

You are working on a .NET 10 platform that monitors customer booking journeys and runs autonomous
agents that investigate problems in them.

Read [`../PLATFORM_GUIDE.md`](../PLATFORM_GUIDE.md) first if you have not. This file covers what
that guide does not: the things that will waste your time if nobody tells you.

---

## The five things that will bite you first

### 1. Tokens expire in about an hour

The agents read Application Insights with a user token pasted into the portal. If the platform
suddenly "stops seeing data", check the token before you debug anything else:

```powershell
curl http://localhost:5080/api/appinsights/tokens
```

`isExpired: true` explains most sudden blindness. Regenerate and re-store — the exact command is
in the root `README.md`. There is no automatic refresh; that is a known gap, not a bug you found.

### 2. The scope of the token is not the obvious one

`https://management.azure.com/` is a valid Azure scope and the wrong one here. Application
Insights needs `https://api.applicationinsights.io/.default`. A wrong-scope token is accepted by
the credential chain and rejected by the API, so the error arrives far from the cause. This
already cost one debugging session.

Azure DevOps needs its own: `499b84ac-1321-427f-aa17-267ca6975798/.default`.

### 3. The agents may be paused

Four agents can be paused from the dashboard, and the pause lives in the database
(`monitoring.AgentPauseSetting`). A paused agent logs
`paused - no board query and no cycle recorded` and does nothing. If a monkey "does not run",
check this before reading any code:

```sql
SELECT MonkeyKey, IsPaused FROM monitoring.AgentPauseSetting;
```

### 4. One test fails, and it is not you

`CouplingInventoryTests.NoCompanySpecificLiteralOutsideTheCompanyProfile` fails on three hardcoded
company literals. It is pre-existing and tracked in [`50-open-issues.md`](50-open-issues.md).
Expect **1109 passed / 1 failed**. If you see a different count, that one is yours.

### 5. `docker-compose down -v` destroys more than you expect

The `-v` wipes the SQL volume **and** the DataProtection key ring. That means: every stored token
becomes undecryptable, the club inventory is re-seeded, and all investigation history is gone.
Use `docker-compose down` without `-v` unless you specifically want a clean database.

---

## Orienting yourself in the data

The fastest way to understand what the platform currently thinks is to read its own conclusions:

```sql
SELECT TOP 10 Decision, Confidence, CitationsVerified, CitationsTotal, LEFT(BlockedBy, 200)
FROM monitoring.ForensicCase
WHERE Status NOT IN ('Queued','Running')
ORDER BY StartedAt DESC;
```

`BlockedBy` is written by the system to say exactly what an investigation was missing. It is the
highest-signal diagnostic in the codebase. Three real defects were found by reading it and
counting which complaints repeated.

---

## How to approach a change here

**Measure before you decide.** Defaults in this codebase carry the measurement that justified
them in their doc comment (see `InspectorOptions.MaxToolResultChars` for the pattern). Follow it.
A number without its evidence will be changed back by the next person.

**The agents are honest by design — keep them that way.** The platform's distinguishing feature is
that it escalates instead of guessing. If a change makes a case resolve, check that it resolved
because the evidence improved, not because a gate was loosened.

**Prefer configuration to code for anything company-specific.** This branch exists to decouple one
customer's vocabulary from the engine. Hardcoding a club id, a vertical name or a company name in
`Core`/`Infrastructure` will fail `CouplingInventoryTests`, and that test is right.

---

## Running things

```powershell
cd Portal
docker-compose up -d --build        # whole stack
dotnet build JourneyMonitor.slnx    # compile
dotnet test tests/JourneyMonitor.Tests/JourneyMonitor.Tests.csproj

# Trigger one agent on demand
curl -X POST http://localhost:5080/api/monkeys/latency-scout-monkey/run

# Watch credential decisions
docker-compose logs -f functions | Select-String "stored token|Resolved tenant"
```
