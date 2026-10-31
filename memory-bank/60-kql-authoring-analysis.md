# 60 — Should the LLM write its own KQL?

Researched 2026-10-31 with empirical probes against the live Application Insights API and the
official Kusto parser. Recorded here so the question does not get re-opened from first principles.

**Short answer: it is feasible and containable, but it is not recommended as the next step, and
the risk is concentrated somewhere non-obvious.**

---

## The current design, and that it was deliberate

Queries are **not** model-authored. `IKqlQueryTextProvider` says so explicitly:

> *"These are NOT model-authored: the model picks the tool and a bounded number, the text comes
> from here"*

and `InspectorOptions`:

> *"A model-authored query against production telemetry must be bounded in code — the prompt is
> not a boundary."*

Reversing this is legitimate but should be explicit and documented, not incremental.

---

## What the API actually permits (measured, not assumed)

| Construct | Result |
|---|---|
| `externaldata(...)` from an arbitrary URL | **ALLOWED** |
| `externaldata` + `strcat` (dynamic URL) | Blocked — the URI must be a literal |
| Literal URL with a query string (`?leak=X`) | **ALLOWED** |
| `app("other-resource")` / cross-resource `union` | **ALLOWED** |
| Scan with no time filter | **ALLOWED** — counted **145,105,383 rows** |
| `mv-expand` bomb (1M × 100) | **ALLOWED** |
| `.show tables` (control command) | Blocked |
| `evaluate http_request(...)` | Blocked |

### The one strong mitigation

The API's **`timespan` parameter bounds the query regardless of what the KQL says**, and the model
cannot override it because it travels outside the query body.

Measured on the same unbounded query: **145,105,383 rows → 3,124** with `timespan=PT5M`. A
46,000× reduction, free.

---

## Validation is possible — and harder than it looks

Microsoft ships the official parser: **`Microsoft.Azure.Kusto.Language` 12.4.1** (the one behind
Kusto Explorer's IntelliSense). It parses KQL and exposes a syntax tree, so a deny-by-default
allowlist is genuinely buildable. That is better than regex matching, which is trivially bypassed.

**But a prototype written specifically to catch dangerous constructs missed the worst one.**

A visitor that flagged dangerous *function calls* correctly caught `app(...)` — and let
`externaldata` through completely, because `externaldata` is not a function call. It is an
`ExternalDataExpression`, its own node type.

And the language accepts **two spellings**, both confirmed accepted by the live API:

```
externaldata    → ACCEPTED
external_data   → ACCEPTED
```

A denylist filtering `"externaldata"` does not filter `"external_data"`.

The correct approach is allowlist **by node kind**, deny by default — which means triaging
**605 `SyntaxKind` values** (23 of which suggest cross-boundary reach by name alone). Achievable,
but the first careful attempt failed silently, and that is the number to weigh.

---

## The risk that no KQL guardrail addresses

This system reads **text an attacker can influence**. Exception messages, URLs and stack traces
from public production traffic enter the model's context — real examples in the data include
`window.onerror@https://members.ulifestylecollection.com/...`.

Under option A the chain becomes:

```
public traffic → crafted exception message → LLM context → KQL executed against production
```

A validator bounds *what* can run. It does not stop an attacker *choosing* what runs within the
permitted set. With `externaldata` and literal URLs allowed, "permitted" can include an outbound
call with context data embedded.

Today this chain does not exist: the model picks a tool and bounded numbers, nothing else.

---

## Non-security objections that matter just as much

1. **Reproducibility.** The drill-down KQL is stored in `RawEvidenceJson` precisely so L3 can
   re-run it. With generated queries, two investigations of the same incident can see different
   data and reach different conclusions with nothing looking broken.

2. **It undermines grounding, which is the part that works.** Citations verify **100%** today
   because `CaseArtifactIndex` checks them against known-query artifacts. If the model authors the
   query, it also authors the universe it is verified against — it verifies itself.

3. **Unpredictable cost.** A careless `mv-expand` scans hundreds of millions of rows. Azure bills
   that.

4. **It bypasses an audit trail that already exists.** `monitoring.KqlQuery` +
   `DbKqlQueryTextProvider` already allow hot-editing queries from the portal, versioned and
   audited. "Why did this alert fire" currently has a text to point at.

---

## What the evidence says to do instead

Categorising what the models actually asked for across all cases:

| Asked for | Mentions | Status |
|---|---|---|
| Azure DevOps credentials | 19 | fixed |
| Scope / entity | 8 | fixed |
| Truncated rows | 7 | fixed |
| **A pivot no tool provides** | **9** | open |

**None of the three fixed defects needed generated KQL.** The models never asked for a different
query — they asked for the scope and the rows they had been promised.

The 9 remaining mentions are a real gap, but they cluster into about **five enumerable pivots**
(listed in [`50-open-issues.md`](50-open-issues.md)). Adding bounded tools covers them with zero
injection surface.

Spending option A's risk budget to cover five known pivots is a poor trade. If, after those tools
exist, the models keep asking for *new* pivots, that is evidence the space is genuinely open — and
then this decision deserves revisiting with data rather than intuition.

---

## If it is built anyway — the minimum viable safe design

1. **Force `timespan` at the API level** on every call. The only bound the KQL cannot touch.
2. **Deny-by-default validation by `SyntaxKind`**, never by name or regex.
3. **One resource per case** — reject `app()` / cross-resource `union` in the tree.
4. **Block `ExternalDataExpression` by node type** (covers both spellings at once).
5. **Row cap, timeout, and a per-case query budget.**
6. **Persist every generated query** with the case that produced it. Without it there is no
   post-mortem.
7. **Shadow mode first** — the model proposes, the system does **not** execute, everything is
   logged. Two weeks of that answers both "are the queries any good?" and "is the space really
   open?" without exposing anything.

Point 7 is the recommendation: it turns an irreversible architecture decision into a measurable
experiment.
