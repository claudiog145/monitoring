# Spec 1000 × Scouts: contract addendum v4 (DRAFT, not frozen)

Status: **APPROVED 2026-11-06** - implemented in code by SD0, then frozen as tag `spec1000-contracts-v4`. It becomes frozen as tag `spec1000-contracts-v4` only when task SD0 is approved. Until then, no SD task may start. **Branch rule: no `git push`, no `gh pr create`.**

## §1 Grain and entity dimension

- `KpiDefinition` gains init-only `KpiGrain Grain { get; init; } = KpiGrain.Domain` (`enum KpiGrain { Domain, Entity }`, JSON camelCase `domain` | `entity`). It also gains `int MaxEntitiesPerBucket { get; init; } = 500` (entity grain only, range 1–5000). The unique key becomes `(ProfileKey, Pillar, Kpi, Domain, Stage, Grain)`.
- `KpiBucket`, `KpiDetectionState` and `KpiAnomaly` gain `string EntityKey` (init, default `""`). `""` means domain aggregate, which is today's behaviour. `KpiBucket` gains `long? FailedCount`.
- SQL (idempotent):
  - Add `EntityKey nvarchar(256) NOT NULL DEFAULT N''` to `KpiBucket`, `KpiDetectionState` and `KpiAnomaly`.
  - Recreate the PKs: `KpiBucket (KpiDefinitionId, EntityKey, BucketStart)` and `KpiDetectionState (KpiDefinitionId, EntityKey)`.
  - Add `KpiBucket.FailedCount bigint NULL`, plus `Grain nvarchar(16) NOT NULL DEFAULT N'domain'` and `MaxEntitiesPerBucket int NOT NULL DEFAULT 500` on `KpiDefinition`.
  - Index `IX_monitoring_KpiAnomaly_Entity (KpiDefinitionId, EntityKey, DetectedAt DESC)`.
- Store reads gain `string? entityKey = null` (null = domain aggregate `""`). A new method, `GetEntityRangeAsync(kpiDefinitionId, from, to)`, returns every entity. A new state method, `GetEntityStatesAsync(kpiDefinitionId)`, does the same for state.

## §2 Source

- `KpiSourceBucket` gains the optional members `EntityKey`, `P50`, `P95`, `Mean` and `FailedCount`. Entity-grain sources return aggregated statistics, computed in KQL with `percentile()` and `countif(success == false)`, and **no samples**.
- KQL templates `kpi-{kind}-{domain}.kql.template.txt` are rendered with `CompanyKqlTemplater` (`SourceUnion`, `HostFilter`, `EntityIdentity`) for the definition's `ProfileKey`. A new token, `{{grain}}`, takes `domain` or `entity`. Domain-grain output keeps the v1 contract; entity-grain output adds `EntityKey`, `P50`, `P95`, `Mean` and `FailedCount`. No company literal may remain in Core/Infrastructure C#.

## §3 Evaluation

- `EntityDetectionMonitor` runs inside the existing monitor timer, after `PillarHealthMonitorService`. For every enabled entity-grain definition, it evaluates every entity series with **the same** `IKpiEvaluator`, against the history of the same entity. State, consecutive count and cooldown are kept per entity.
- An entity anomaly writes a `KpiAnomaly` with its `EntityKey`. It does **not** change pillar status and does **not** submit a `TriageSignal`.
- Activity (v2 §5 style, kind `kpi-evaluated`): one summary line per definition, `"{Kpi}/{Domain} [entity]: {n} series evaluated, {a} anomalous, {t} triggered"`, so the console doesn't get one line per entity.

## §4 Scout shadow

- `Scout:{Latency|Exception}:DetectionMode` = `Rules` (default during Shadow) | `Method` (SD8, out of scope).
- After each cycle, the Scout writes one `monitoring.ScoutShadowComparison` row per `(EntityKey, Stage, Bucket)` seen by either side in the window:
  - Columns: `RunId`, `Board`, `ProfileKey`, `EntityKey`, `Stage`, `BucketStart`, `RuleRank`, `MethodVerdict` (`anomalous` | `normal` | `not-evaluated`), `KpiAnomalyId` (nullable) and `Agreement`.
  - `Agreement` is one of `both`, `rules-only` or `method-only`. Rows where neither side flags anything are not stored.
- Mapping: the board's `Entity` maps to `EntityKey`, and `Stage` maps to `Stage`. The bucket rule: a rules case counts as matched when a method anomaly for the same entity and stage covers its 5-min interval.
- Escalation to the Inspector does not change while `DetectionMode = Rules`.
- API: `GET /api/scouts/{board}/shadow?hours=&profileKey=` (read, `X-Internal-Api-Secret` not required, same as the other Scout reads) returns the counts and the latest rows.

## §5 Company-agnostic daemons (AG1–AG7)

- **Domains:** `KpiDefinition.Domain` must equal a `Services[].Key` of the definition's profile (case-insensitive). `KpiDomains.All` is only the seed of the incumbent profile. An unknown domain returns a 400 on POST. At run time it is skipped with Warning `"{Kpi}/{Domain} [{profileKey}]: domain is not a service of the profile"`.
- **Source resolution:** service → `TelemetrySourceAliases` → `monitoring.TelemetrySource` → app id + tenant, through `IKqlSourceResolver`. `InsightsOptions` is consulted only when the profile has no alias for the service.
- **`SourceKey` values:**
  - `appinsights:latency` and `appinsights:exceptions` (existing, templated from the profile, see SD2);
  - `appinsights:dependency`, entity grain, `EntityKey = partner key`;
  - `kql:{queryKey}`, a query from `monitoring.KqlQuery` that must return the v1 output contract plus the optional entity columns from §2.
  - A `kql:` query may only use the tokens `{{from}}`, `{{to}}`, `{{bucket}}`, `{{perBucket}}`, `{{stage}}` and `{{grain}}`. It is validated on save with a one-hour dry run (bounded timeout); validation errors return a 400 with the message.
- **Partners:** `CompanyProfile.Partners` is optional, a list of `{ Key, DisplayName, Matchers[] }`. Each matcher has `Kind`, one of `dependency-target-contains`, `dependency-name-contains`, `dependency-type-equals` or `regex`, plus a `Value`. The validator requires unique keys and at least one matcher.
- **Triage business evidence:** for a profile without Fabric, the business dimension comes from that profile's Business-pillar KPI buckets in the signal window. Fabric stays exclusive to the incumbent.
- **Privacy:** `IEvidenceRedactor.Redact(string text, CompanyProfile profile)` runs on every evidence section and every prompt sent by Triage and Inspector. `CompanyProfile.Privacy` is optional, `{ AllowRawEvidence = false, PiiDimensions[], Patterns[] }`. The redaction audit stores counts per pattern, never values.
- **Onboarding:** the draft response gains `suggestedKpis[]` (all `enabled:false`).
- **Isolation guarantee (E2E `agnostic`):** nothing from profile `acme` appears in `arrivia` reads, and the reverse also holds. Daemons process both profiles.

## §6 Amendments (orchestrator, 2026-11-06)

- **A1 EntityKey vs stage:** EntityKey holds ONLY the entity (club/host identity), never the stage. The stage of an entity-grain series is the KPI definition's Stage. Entity KPIs with an empty Stage aggregate all of the service's stages per entity. Shadow matching (§4) treats a rules row (entity, stage) as matched when a method anomaly exists for the same entity in a definition whose Stage equals that stage OR is empty.
- **A2 Domain KPI history reset:** SD2 changes what the incumbent's domain KPIs measure (all hosts, the service's own stages). On merge, the orchestrator resets the buckets, samples, detection state and watermark of the incumbent's domain KPIs, so the H1 backfill rebuilds the history with the new meaning. QA and prod deploy notes must repeat this step.
- **A3 Shared stages:** the incumbent profile maps login, checkout, booking and confirmation to its shared service. SD6 seeds domain and entity KPIs for shared so those stages stay monitored.


- **A4 Incumbent `air` service:** the `arrivia` profile gains an `air` service so that its 6 air KPIs stay valid under AG1. The service must not change the Scout board (it gets no telemetry source alias that would add a cross-tenant `app()` to the board). If that cannot be guaranteed, the fallback is an incumbent exception: for `ProfileKey = arrivia` only, `KpiDomains.All` stays accepted as a domain list, documented and covered by tests.

