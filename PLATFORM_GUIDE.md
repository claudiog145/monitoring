# Platform Guide

Everything in one place: what the monkeys are, how an investigation actually runs, what the
dashboard shows, what the memory bank is, and what was changed most recently and why.

Written for someone — human or agent — who has never seen this codebase.

---

## 1. The idea

Most monitoring tells you *that* something is wrong. This platform tries to tell you *what* is
wrong and *why*, and then refuses to claim it knows when it does not.

That refusal is the design centre. Every number an agent quotes is checked against the real system
before the case is allowed to close. An agent that cannot ground its answer escalates to a human
instead of producing a confident-sounding guess. If you change one thing in this codebase, do not
change that.

---

## 2. The monkeys

Seven agents, each with one job. They are named after Netflix's Simian Army, but unlike the
original they observe rather than break things — every one of them currently runs in `Observe`
mode.

| Monkey | Key | What it does |
|---|---|---|
| **Latency Scout** | `latency-scout-monkey` | Watches a per-club latency priority board. Packages anything worth a deeper look into a self-contained handoff. |
| **Exception Scout** | `exception-scout-monkey` | Same, for exceptions. |
| **Inspector** | `inspector-monkey` | The L3 investigator. Five specialists, three debating models, a planner. Covered in detail below. |
| **RCA Agent** | `rca-monkey` | Writes the publishable Root Cause Analysis from a finished investigation. It does not investigate — it composes. |
| **Latency Spike** | `latency-spike-monkey` | Detects individual latency spikes that repeat within a stage beyond a threshold. |
| **SLO Burn-Rate** | `slo-burn-rate-monkey` | Google SRE multiwindow multi-burn-rate method against configured SLOs. Pages or tickets. |
| **Doctor** | `doctor-monkey` | Deep root-cause diagnosis of latency findings: classifies, retrieves precedents, calls an LLM for a cited verdict. |

### The two-stage design

The Scouts and the Inspector are deliberately split:

- A **Scout** is cheap and runs constantly. It never calls an LLM. Its job is triage: look at a
  board of aggregated rows and decide which ones deserve expensive attention.
- The **Inspector** is expensive and runs on a queue. It makes ~11 LLM calls and burns ~88,000
  input tokens per investigation.

The artefact passed between them is an **investigation package** (`monitoring.InvestigationPackage`):
a self-contained bundle with the board row, the drill-down evidence, the KQL that produced it, and
the resolved entity. "Self-contained" matters — an L3 engineer can re-run the exact query in Azure
without reconstructing anything.

### Pausing

Agents can be paused from the dashboard (`monitoring.AgentPauseSetting`) or disabled by
configuration (`ScoutLatency:Enabled` / `ScoutException:Enabled`). These are two independent
switches and the UI reports which one fired. A paused agent writes no cycle at all; a disabled one
records a "disabled" cycle. Do not collapse them.

---

## 3. How an Inspector investigation runs

Seven phases, visible live in the dashboard:

```
preprocess → scope → agents → debate → synthesis → grounding → verdict
```

### The five specialists

| Specialist | Source | Reads |
|---|---|---|
| `LogsAgent` | Application Insights | Four bounded KQL tools (drill-down, club baseline, signature breakdown, client breakdown) |
| `MemoryAgent` | Knowledge base | Past incidents — "have we solved this before?" |
| `CodeAgent` | Azure DevOps | Code search across the organisation's repositories |
| `DocsAgent` | Azure DevOps | Wiki and runbook search |
| `ReproAgent` | Stored journeys | Reconstructs the user flow. It reconstructs — it does not reproduce |

A specialist that has no source says so. It never degrades into "found nothing", because an
investigation that could not look is not the same as one that looked and saw nothing clean.

### The debate

Three models argue over the same evidence (`claude-sonnet-5.5`, `gpt-4o`, `claude-opus-5.5`),
then a Planner model synthesises. Round 0 is independent; two further rounds reconcile.

The debate is roughly 64% of the token spend. `SkipDebateWhenContributorsBelow` exists to skip it
when the blackboard is nearly empty, and defaults to 0 (never skip) because the measured data did
not justify a non-zero value.

### Grounding — the important part

Before a verdict is allowed, `InspectorGroundingService` takes every artifact the synthesis cited
and checks it against `CaseArtifactIndex` — the set of things the tools actually returned. Each
citation lands as **verified**, **fabricated** (the model invented an identifier) or
**unverifiable** (we have no index to check it against).

Fabricated and unverifiable are counted separately and never summed. One is a statement about the
model; the other is a statement about this platform.

### The verdict gates

`InspectorVerdictService` is policy, not another model opinion. To `resolve`, all three must hold:

| Gate | Default | Meaning |
|---|---|---|
| Conclusion | `root_cause` | The synthesis actually found one |
| Confidence | ≥ 0.75 | `MinConfidenceToResolve` |
| Verified citation ratio | 1.0 | Every cited artifact checked out |

Anything else escalates, carrying a `BlockedBy` that names exactly what was missing. Those
`BlockedBy` strings are the single most useful diagnostic in the whole system — when the platform
is not resolving cases, read them first. They told us all three of the defects fixed most
recently.

---

## 4. The dashboard

Nine tabs at <http://localhost:5080>:

| Tab | What it is for |
|---|---|
| **Overview** | Health at a glance |
| **Journeys** | Customer booking funnels per club and vertical |
| **Health** | Application health signals |
| **Analytics** | Trends |
| **Simian Army** | The monkeys: status, cycles, live phase, Run Now, pause |
| **Memory Bank** | What the agents have read — see below |
| **Club Map** | Which clubs we observe. Read-only, from `brand.ClubInventory` |
| **Exceptions** | Exception boards |
| **Settings** | AppInsights tokens, LLM client, alert channels, latency thresholds, KQL queries |

The Web app holds no business logic — it calls Functions over HTTP. Two endpoints are gated by
shared secrets (`X-Settings-Write-Secret`, `X-Internal-Api-Secret`) and both **fail closed**: if
the secret is not configured, every write returns 401 rather than being allowed through.

---

## 5. The two "memory banks" — they are different things

The phrase is used for two separate concepts in this repository. Keep them apart.

### 5.1 The application's Memory Bank (a product feature)

The dashboard's **Memory Bank** tab: *"What the agents have read."* Past incidents imported into
the knowledge base, shown exactly as indexed rather than as the original file.

How it works:

```
docs/RCAS-md/  (RCA corpus, markdown)
      │  POST /api/knowledge/import   (KnowledgeImportService)
      ▼
  chunks → embeddings (text-embedding-3-small / ada-002 via GitHub Copilot)
      ▼
  InMemoryKnowledgeRetriever
      ▼
  MemoryAgent (Inspector)  and  DoctorAgentService
      "have we seen this before?"
```

Relevant code: `KnowledgeImportService`, `SearchKnowledgeFunction`,
`GetKnowledgeDocumentsFunction`, `ImportKnowledgeCorpusFunction`, `InMemoryKnowledgeRetriever`.
The corpus is bind-mounted into the Functions container at `/corpus` (read-only) from
`docs/RCAS-md`.

A successful Doctor report is itself indexed back into the knowledge base, so the system's own
conclusions become precedents for later cases.

### 5.2 The agent memory bank (this repository's `memory-bank/`)

Persistent context for an **AI agent working on this codebase**. It exists because a fresh agent
session has none of the reasoning that produced the current state, and that reasoning is where
most of the value is. Read it in numeric order. See [`memory-bank/`](memory-bank/).

---

## 6. Multi-tenant: how the platform knows whose data it is reading

Three Application Insights resources across two Azure tenants:

| Domain | Tenant | Token key |
|---|---|---|
| Hotels | `60396ff5-…` | `arrivia` |
| Cruises | `60396ff5-…` (same tenant) | `arrivia` |
| Air | `b0298d22-…` | `ice` |

`AppInsightsTelemetrySource.ResolveTenantKey` maps a tenant UUID to the key the token is stored
under, then `AppInsightsTokenService` decrypts it. If no token is stored it falls back to
`DefaultAzureCredential`, which in local Docker will fail — honestly and in the log.

Tokens are encrypted with ASP.NET DataProtection. **Web and Functions must share the key ring**
(`dataprotection-keys` volume, same `ApplicationName`) or a token saved in one cannot be read by
the other. They also run under the same uid, because DataProtection writes key files `0600`.

### The company profile

Who the data belongs to is described by data, not code. `monitoring.CompanyProfile` holds a JSON
document per tenant: services, flows, steps, and `entityIdentitySources` — the ordered chain used
to work out which club a telemetry row belongs to.

The chain for `arrivia`, in order:

1. `custom-dimension: VacationClubId`
2. `custom-dimension: ClubId`
3. `custom-dimension: club_id`
4. `query-parameter: cobrand`
5. `query-parameter: cobrandId`
6. **`host`** — falls back to the hostname, resolved to a club through `brand.ClubInventory.Site`

Step 6 declares `entityKind: "host"`, not `"club"`. That distinction is load-bearing: the key
becomes `host:members.example.com`, and `ClubIdentityResolver` is what maps it to a club, reporting
*how* (`host-exact` / `host-base-domain`). Nothing ever claims a hostname is a club id.

Measured on live data: the stamped dimensions cover under 5% of traffic, while 100% of requests and
87% of exceptions carry a url. Without step 6 almost nothing resolves.

---

## 7. KQL: who writes it, and why not the model

All KQL lives as text files in `Portal/src/JourneyMonitor.Infrastructure/Kql/`, served by
`IKqlQueryTextProvider`. `DbKqlQueryTextProvider` lets a row in `monitoring.KqlQuery` override any
query at runtime — editable from the Settings tab, no deployment.

Queries are **not model-authored**. The model picks a tool and a bounded number; the text comes
from the provider, and parameters are substituted with base64 (an alphabet that cannot close a KQL
string literal — that is the real anti-injection boundary, not escaping).

This was a deliberate decision and it was re-examined recently. The analysis, including measured
evidence from the live API, is in
[`memory-bank/60-kql-authoring-analysis.md`](memory-bank/60-kql-authoring-analysis.md). Read it
before proposing that the LLM write its own queries — the short version is that it is feasible but
the risk is concentrated somewhere non-obvious.

---

## 8. Conventions that will trip you up

**Comments explain WHY.** This codebase documents decisions and what breaks otherwise, never what
the code does. A comment restating the code will look wrong next to its neighbours.

**"Unresolved" and "unavailable" are different.** `unresolved` = their mapping gap (the profile
does not describe this row). `unavailable` = our outage (the classifier itself failed). Collapsing
them hides our own failures inside their data quality.

**Failure is never silence.** A search that could not run reports "failed", never "found nothing".
A truncated list says it is truncated. This rule is enforced in several doc comments and at least
one test.

**Fail closed.** Secret-gated endpoints return 401 when the secret is missing, rather than
treating a missing secret as "no check configured".

**One-way dependencies.** `Core` never references `Infrastructure`.

---

## 9. Where the numbers live

| Thing | Where |
|---|---|
| Investigation outcomes | `monitoring.ForensicCase` — decision, confidence, `BlockedBy`, citations, tokens |
| Scout handoffs | `monitoring.InvestigationPackage` |
| Club catalogue | `brand.ClubInventory` (5,615 rows; `Site` is the host, `Domain` is a product vertical) |
| Company profiles | `monitoring.CompanyProfile` + `CompanyProfileAudit` |
| Encrypted tokens | `AppInsightsTokens` |
| Pause switches | `monitoring.AgentPauseSetting` |
| Overridable KQL | `monitoring.KqlQuery` |

Quick look at why nothing is resolving:

```sql
SELECT TOP 10 Decision, Confidence, CitationsVerified, CitationsTotal, BlockedBy
FROM monitoring.ForensicCase
WHERE Status NOT IN ('Queued','Running')
ORDER BY StartedAt DESC;
```
