# 50 — Open issues

What is broken, what is known-failing, and what the evidence says to do next.

---

## 1. No investigation resolves — the remaining blocker

**Status:** open. This is the headline issue.

Three blockers were removed and verified (see [`40-session-log.md`](40-session-log.md)), but
`resolved` is still **0**. Confidence rose only to 0.12–0.25 against the 0.75 needed. The
synthesis keeps concluding `insufficient_evidence`.

Two measured reasons:

### 1a. A 200-row package still does not fit the character budget

`200 × 1403 = 280,600` characters against a 48,000 budget. Roughly **900 characters of every row
are identifiers that must not be shortened** — citations are verified against them, so shortening
one turns a real citation into a reported fabrication.

Options, none yet taken:
- Narrow the drill-down projection (does it need `Url` *and* `Path` *and* `Instance` on every row?)
- Cap `StackTrace` harder at the KQL (currently 2000) rather than in the envelope
- Accept fewer rows but summarise the remainder server-side

### 1b. 9 blocker mentions ask for pivots no tool provides

Counted across all cases. They cluster into about five concrete pivots:

1. Preceding-bucket count / adjacent-window comparison
2. Host-level metrics (e.g. `PROD-PHXWEB11`) and its dependencies
3. Breakdown by `type` + `operation` + `CorrelationId`
4. Request traces for the 500s
5. Unmasked stack / response body for the 404s

**Recommended next step:** add three or so bounded tools covering these. They are enumerable and
known, the pattern already exists, and it adds no injection surface. See
[`60-kql-authoring-analysis.md`](60-kql-authoring-analysis.md) for why free-form model-authored
KQL is *not* the recommended way to close this.

---

## 2. `CouplingInventoryTests` fails — 3 hardcoded company literals

**Status:** open, pre-existing. Expect **1109 passed / 1 failed**.

```
JourneyMonitor.Core/Monitoring/TenantResolver.cs:77          "arrivia"
JourneyMonitor.Infrastructure/AppInsights/AppInsightsTelemetrySource.cs:146   return "arrivia";
JourneyMonitor.Infrastructure/AppInsights/AppInsightsTelemetrySource.cs:162   return "cruises";
```

The last two are inside `ResolveTenantKey`, which hardcodes the UUID → token-key map — precisely
the coupling this branch exists to remove. The test is right and the code is wrong.

**Fix:** drive the mapping from configuration (an optional `TokenKey` per Insights domain, or the
company profile) instead of literals. Do **not** relax the test.

---

## 3. Tokens expire hourly with no refresh

**Status:** open, by design for now.

Application Insights and Azure DevOps tokens last ~1 hour and must be re-pasted. Everything
degrades honestly when they expire (fall back to `DefaultAzureCredential`, which then fails and
says so) but the agents go blind until someone notices.

Options: a device-code refresh flow, a service principal for non-interactive environments, or a
visible expiry warning in the dashboard.

---

## 4. DataProtection keys live in a Docker volume

**Status:** acceptable locally, not for deployment.

Tokens are encrypted with a key ring on a shared volume, and Web is forced to run as root so it
can read a ring written by Functions. A real deployment should back DataProtection with Key Vault
or blob storage. The compose comments say this; it has not been done.

---

## 5. Host → club mapping is ambiguous for many hosts

**Status:** known, not blocking.

**812 of 1,367** distinct `ClubInventory.Site` values map to more than one `ClubId`
(`redeem.travelsavingsdollars.com` → 290 clubs). `ClubIdentityResolver` takes `FirstOrDefault`, so
the `ClubId`/`ClubName` attached to a host-resolved package is **one arbitrary member of that
set**.

This does not block investigations — the Inspector's tools gate on `EntityKey`, not `ClubId` — but
the club label on a host-resolved package is best-effort and should not be quoted as fact.
Consider reporting the ambiguity rather than silently picking.

---

## 6. Legacy production boards still carry the pre-fix identity chain

**Status:** deliberate, needs a decision.

`scout-latency-priorities.kql.txt` and `scout-exception-priorities.kql.txt` are the boards that
actually run (`Scout:UseComposedBoard` is unset). They still read `customDimensions.CobrandId` and
still build `Host` without `tolower`.

Not changed, because removing `CobrandId` alters `GroupKey`/`CaseId` and lowercasing changes the
dedup key — for the two queries that drive production alerting. Harmless today (the board's
`Entity` is only a grouping key; the package's `EntityKey` comes from the profile and is
lowercased), but it is divergence that will confuse someone.

---

## 7. `sql-init` can fail on a cold start

**Status:** cosmetic race.

On a restart against an existing volume, `sql-init` may run while SQL is still recovering:
`Cannot open database "platformDB" requested by the login`. Re-run `docker-compose up -d` once
SQL reports healthy. A readiness retry in the init script would remove it.

---

## 8. Pre-existing oddity: `Success` splits into two columns

`scout-drilldown.kql.txt`'s `union` yields `Success_string` from the requests branch and
`Success_bool` from the exceptions branch, both visible in stored `RawEvidenceJson`. Harmless,
untidy, unrelated to recent work.
