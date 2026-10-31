# 20 — The monkeys

Seven agents. All currently run in `Observe` mode — they report, they do not act.

---

## The roster

| Monkey | Cycle | Calls an LLM? | Job |
|---|---|---|---|
| `latency-scout-monkey` | 5 min | No | Triage the latency priority board, package what deserves attention |
| `exception-scout-monkey` | 5 min | No | Same, for exceptions |
| `inspector-monkey` | 1 min | **Yes, heavily** | Investigate one package end to end |
| `rca-monkey` | on demand | Yes | Compose the publishable RCA from a finished investigation |
| `latency-spike-monkey` | 5 min | No | Detect repeated latency spikes within a stage |
| `slo-burn-rate-monkey` | 5 min | No | Multiwindow multi-burn-rate against configured SLOs |
| `doctor-monkey` | 5 min | Yes | Diagnose latency findings with precedents + a guarded LLM call |

### Why Scouts and Inspector are separate

A Scout is cheap, runs constantly, and never calls a model. Its only decision is *which rows
deserve expensive attention*. The Inspector costs ~11 LLM calls and ~88,000 input tokens per case.
Putting triage inside the expensive agent would have meant paying investigation prices for
rejection.

The handoff artefact is an **investigation package**: board row, drill-down evidence, the KQL that
produced it, the resolved entity, the window. Self-contained on purpose — an L3 engineer can
re-run the query in Azure without reconstructing it.

---

## An Inspector investigation, phase by phase

```
preprocess → scope → agents → debate → synthesis → grounding → verdict
```

**preprocess / scope** — parse the package, resolve which entity and window the case is about.
A case with no resolvable entity loses three of the LogsAgent's four tools immediately, because
they have nothing to scope to.

**agents** — five specialists work a shared blackboard, each with its own tools and its own
source. They can ask each other for help, bounded by `MaxHelpHops` (default 3) so "A asks B, B
asks A" cannot run until the budget is gone.

| Specialist | Source | Fails how |
|---|---|---|
| `LogsAgent` | Application Insights, 4 bounded KQL tools | Tools refuse without an entity |
| `MemoryAgent` | Knowledge base (past incidents) | Empty corpus = no precedents |
| `CodeAgent` | Azure DevOps code search | No token = no source |
| `DocsAgent` | Azure DevOps wiki search | No token = no source |
| `ReproAgent` | Stored journeys | Cannot map a domain = no reconstruction |

A sourceless specialist says it is sourceless. It never reports "found nothing", because *could
not look* and *looked and saw nothing* are different facts and only one of them is about the
system under investigation.

**debate** — three models reason over the same blackboard (`claude-sonnet-5.5`, `gpt-4o`,
`claude-opus-5.5`). Round 0 independent, then `DebateRounds` (default 2) of reconciliation. This
is ~64% of token spend.

**synthesis** — the Planner model (`claude-sonnet-5.5`) produces one answer with citations,
confidence and a stated blocker. Budgeted separately (`MaxSynthesisTokens`, 3000) because a
synthesis cut off mid-sentence wastes the whole investigation.

**grounding** — every cited artifact is checked against `CaseArtifactIndex`, the set of things the
tools actually returned. Outcomes: verified / fabricated / unverifiable. Fabricated and
unverifiable are reported separately and never summed.

**verdict** — `InspectorVerdictService`, which is policy and not another model opinion:

| To `resolve`, all three | Default |
|---|---|
| Conclusion is `root_cause` | — |
| Confidence ≥ `MinConfidenceToResolve` | 0.75 |
| Verified citation ratio ≥ `MinVerifiedCitationRatioToResolve` | 1.0 |

Otherwise it escalates with a `BlockedBy` naming what was missing. `no_issue` is also a real
outcome — but only once something cited was confirmed, because closing a case on an unverifiable
all-clear is the most expensive way this agent can be wrong.

---

## The LogsAgent's four tools

| Tool | Needs an entity? | Returns |
|---|---|---|
| `drill_down_events` | No | The raw requests/exceptions behind the board row |
| `club_baseline` | **Yes** | This entity's own history, for comparison |
| `signature_breakdown` | **Yes** | Exception signatures within the entity |
| `client_breakdown` | **Yes** | Client/browser split within the entity |

Three of four need a resolved entity. This is why entity resolution was worth fixing: without it
the LogsAgent sees raw rows and has nothing to compare them against.

Results come back through `Envelope`, which enforces a character budget by **shortening verbose
fields first and dropping rows only as a last resort**, and labels the two cases separately:
`truncated` means rows are missing, `fieldsShortened` means every row is present and only long
text was abbreviated. Identifiers are never shortened — citations are verified against them.

---

## Reading what the agents concluded

```sql
SELECT TOP 10 Decision, Confidence, CitationsVerified, CitationsTotal, LEFT(BlockedBy, 250)
FROM monitoring.ForensicCase
WHERE Status NOT IN ('Queued','Running')
ORDER BY StartedAt DESC;
```

`BlockedBy` is the highest-signal field in the system. When investigations are not resolving,
count which complaints repeat across cases — that is how the three most recent defects were found.

Live state of one agent, including phase and queue:

```powershell
curl http://localhost:5080/api/monkeys | ConvertFrom-Json |
  Where-Object { $_.id -eq 'inspector-monkey' }
```
