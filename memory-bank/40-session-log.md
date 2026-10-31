# 40 — Session log

What was changed, why, and the measurement that justified it. Newest first.

The git history did not come with this repository, so this file is the record of how the code got
to its current state.

---

## 2026-10-31 — Three capabilities the Inspector was missing

### The symptom

Every investigation escalated at confidence 0.10–0.25 (0.75 is needed to resolve), **while
reporting 100% verified citations**. Nine investigations, zero resolved.

That combination is the interesting part: the grounding was perfect and the confidence was
terrible. The models were not hallucinating — they were telling the truth about not having enough
to work with.

### How the three causes were found

By categorising what the models actually asked for in `BlockedBy` across all cases:

| What they asked for | Mentions |
|---|---|
| Azure DevOps credentials | 19 |
| Scope / entity (club or host) | 8 |
| The rows that had been truncated | 7 |
| A pivot no tool provides | 9 |

The first three were fixed. The fourth is still open — see [`50-open-issues.md`](50-open-issues.md).

### Fix 1 — Azure DevOps could never authenticate

`AzureDevOpsSearchTransport` used a bare `DefaultAzureCredential`, which in a container with no
Managed Identity spends its retry budget on IMDS at `169.254.169.254` and fails. **20 failures in
one log window.** `DocsAgent` and `CodeAgent` — two of five specialists — contributed nothing to
any case.

Changed: the same container/Managed-Identity guard `AppInsightsTelemetrySource` already had, plus
a token chain (stored encrypted under key `azure-devops` → configured `AccessToken` → credential
chain). Every message names the Azure DevOps resource literally, because an earlier incident was
caused by documentation naming the ARM scope.

**Verified:** token failures 20 → 0; both agents now reach `almsearch.dev.azure.com`.

### Fix 2 — 90% of the evidence was being thrown away

`Envelope` enforced its character budget by **dropping whole rows**. Measured on a live package:
34 rows, 2,373 characters per serialized row, `StackTrace` alone averaging 850. At the old 8,000
budget the model received **3 of 34 rows**, and then reported *"only 2 of 166 exception rows were
sampled"* — which was accurate.

Changed: verbose fields (`StackTrace`, `Message`, …) are shortened through a ladder
(1200 → 600 → 360 → 240) before any row is dropped. Identifiers are never shortened — citations
are verified against them. `MaxToolResultChars` raised 8,000 → 48,000, derived:
`2373 − (850−240) − (600−240) = 1403` per row, `34 × 1403 = 47,702`.

`InspectorToolPayload` gained `FieldsShortened` so "rows are missing" and "text was abbreviated"
stopped being the same claim, and the LogsAgent prompt was taught the difference — without that,
the code-side distinction never reaches the model.

**Verified:** evidence per case 2–3 rows → 21–88.

### Fix 3 — No package ever resolved an entity

Every package carried `ClubId NULL`, `EntityKey '[host/club unavailable]'`. Three of the
LogsAgent's four tools refused to run.

Cause: the `arrivia` profile identified clubs only by custom dimensions. Measured over 3h on the
live Hotels/SOR resource:

| Identifier | Coverage |
|---|---|
| `url` (carries the host) | **100% of requests, 87% of exceptions** |
| `ClubId` | 852 rows |
| `club_id` | 342 rows |
| `VacationClubId` | 6 rows |
| `CobrandId` | **0 rows** |

Changed: the identity chain now ends in a `host` source — a kind already supported end to end by
`CompanyKqlTemplater` and `ProfileDrivenAdapter`, simply never configured. Declared as
`entityKind: "host"`, not `"club"`: the key becomes `host:...`, and `ClubIdentityResolver` maps it
to a club through `brand.ClubInventory.Site` while reporting *how* (`host-exact` /
`host-base-domain`). Nothing claims a hostname is a club id.

`CobrandId` removed (0 occurrences). An idempotent, audited migration applies the same change to
databases already seeded. `scout-drilldown.kql.txt` now projects `Host` on both branches so
per-row evidence can corroborate the package header.

**Verified:** `host:stays.tmobiletravel.com` → `host-exact` → ClubId 15403, "T-Mobile TRAVEL".

### Found while verifying

- **`IAppInsightsTokenService` was registered only in the composition roots**, so
  `AddJourneyMonitorInfrastructure` could not stand up its own graph. Both
  `InspectorDependencyInjectionTests` were failing on exactly the thing they exist to catch. Now
  registered in the extension; both pass.
- **`ClubIdentityResolver` compared telemetry hosts against raw `ClubInventory.Site`.** 8 of the
  1,370 distinct values end in a slash and matched neither the exact nor the base-domain test,
  silently costing those clubs every host resolution.

### Cost

Measured, not estimated: **~88k input tokens per investigation, against ~74k before (+19%)** —
far below what the 6× budget increase suggested, because the budget is a ceiling, not a target.

### Outcome

Tests 1109 pass / 1 fails (pre-existing). All three blockers removed and verified against the live
stack. **`resolved` is still 0** — see [`50-open-issues.md`](50-open-issues.md).

---

## 2026-10-30 — Retired Copilot model ids

The Inspector reported *"the Planner model (claude-sonnet-5) was unavailable: HTTP 400
model_not_supported"* and every investigation failed.

`claude-sonnet-5` and `claude-opus-5` had been removed from the GitHub Copilot catalogue. That
took out the Planner (no synthesis → the case fails) **and** two thirds of the debate roster,
leaving `gpt-4o` debating alone.

Probed the live catalogue *and* `/chat/completions` separately, because being catalogue-listed is
not proof a model is callable:

| Model | `/chat/completions` |
|---|---|
| `claude-sonnet-5` | 400 `model_not_supported` |
| `claude-opus-5` | 400 `model_not_supported` |
| `gpt-5.3-codex` | 400 — listed but not callable |
| `claude-sonnet-5.5` | ok |
| `claude-opus-5.5` | ok |
| `gpt-4o` | ok |

Defaults are now `claude-sonnet-5.5` (Planner) and
`claude-sonnet-5.5,gpt-4o,claude-opus-5.5` (roster). The Doctor's `gpt-4o-mini` and the embedding
models were probed and still answer.

**Lesson worth keeping:** the model catalogue and the callable set are different. Probe
`/chat/completions` before trusting a model id.

---

## 2026-10-30 — Stored tenant tokens made usable

Tokens saved through Settings never reached the agents. Two independent defects:

1. **The DataProtection key ring was not shared with Web.** `docker-compose` mounted
   `dataprotection-keys` into Functions but not into Web, so Web encrypted with an ephemeral
   in-container ring and Functions could not decrypt. Fixed by mounting the same volume and
   aligning the uid — Functions runs as root and DataProtection writes key files `0600`, so a
   non-root Web could not read the ring.
2. **The documented scope was wrong.** Every hint said
   `--scope https://management.azure.com/`, while the query client requests
   `https://api.applicationinsights.io/.default`. A token for the wrong audience is accepted by
   the credential chain and rejected by the API. Corrected in seven places.

Also fixed earlier in the same work: a connection string missing the `Password=` keyword, and the
UUID → tenant-key mapping the scouts need to find a stored token.
