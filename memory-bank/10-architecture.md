# 10 — Architecture

## The four projects

```
JourneyMonitor.Web            Dashboard. No business logic — calls Functions over HTTP.
JourneyMonitor.Functions      Timers + HTTP API. The composition root.
JourneyMonitor.Core           Domain, use cases, agent logic. No Azure, no HTTP, no EF.
JourneyMonitor.Infrastructure Application Insights, SQL, KQL text, Azure DevOps, LLM clients.
```

**The dependency rule is one-way and absolute:** `Core` never references `Infrastructure`. Core
defines the abstraction (`ITelemetrySource`, `IKqlQueryTextProvider`, `IAppInsightsTokenService`),
Infrastructure implements it, and `AddJourneyMonitorInfrastructure` wires them.

This is why `Core` is unit-testable without a database or an Azure account, and why there are 1110
tests that run in under a second.

### Where registration belongs

`AddJourneyMonitorInfrastructure` must be able to stand up its own graph. `InspectorDependencyInjectionTests`
exists specifically to catch registrations that were only added to a composition root — the doc
comment says it plainly: *"a missing registration compiles perfectly and only fails on the first
timer tick in a deployed environment."*

That test was failing until recently because `IAppInsightsTokenService` was registered only in the
two `Program.cs` files. If you add a dependency that Infrastructure types need, register it in the
extension, not in the host.

---

## Two Functions hosts

`functions` and `functions-qa` run the same image against different databases and different
Application Insights environments (production vs staging). They have **separate** DataProtection
volumes, which means a token stored through the Web container is readable by `functions` and not
by `functions-qa`. That is intentional, and occasionally surprising.

---

## DataProtection: the constraint that keeps biting

Tokens and LLM credentials are encrypted at rest with ASP.NET DataProtection. For a token saved in
the Web container to be readable by Functions, three things must line up:

1. Same `ApplicationName` (`"JourneyMonitor"`)
2. Same keys directory (`/home/dataprotection-keys`), on a **shared volume**
3. Same uid — DataProtection writes key files `0600`, so a non-root Web could not read a key ring
   created by a root Functions

All three are configured in `docker-compose.yml` with comments explaining what breaks otherwise.
Symptom when one is wrong: `Error accessing token for tenant '...'`, followed by a silent fallback
to `DefaultAzureCredential`, which then fails on IMDS.

For a real deployment this should move to a shared key store (Key Vault or blob). The local
arrangement is a documented compromise, not a model to copy.

---

## Credential resolution

Both telemetry sources follow the same shape:

```
stored encrypted token  →  configured token  →  DefaultAzureCredential
```

`DefaultAzureCredential` is given a container guard: when `DOTNET_RUNNING_IN_CONTAINER` is set and
no `IDENTITY_ENDPOINT`/`MSI_ENDPOINT` exists, Managed Identity is excluded. Without it the chain
spends its entire retry budget on IMDS at `169.254.169.254`, which does not exist in local Docker,
before trying anything that might work.

Relevant: `AppInsightsTelemetrySource.GetCredentialAsync`,
`AzureDevOpsSearchTransport.CreateCredential`.

---

## Company profile: the decoupling layer

`monitoring.CompanyProfile` holds a JSON document per tenant describing services, flows, steps,
the entity identity chain, and a glossary. `ProfileDrivenAdapter` reads it; `CompanyKqlTemplater`
renders parts of it into KQL; `CompanyProfileValidator` refuses documents this build cannot
evaluate rather than letting them silently match nothing.

Identity source kinds the build supports: `custom-dimension`, `query-parameter`, `host`,
`role-name`. All four are implemented end to end — a profile can use `host` without any code
change.

The seed is in `Portal/src/JourneyMonitor.Infrastructure/Sql/monitoring/CreateMonitoringCompanyProfile.sql`.
It is **INSERT-only**, guarded by an existence check, so changing the seed does not change a
database that already has a row. Profile changes therefore need an explicit, idempotent,
**audited** migration block after the seed — there is one in that file to copy the shape from. It
refuses to touch a profile an operator edited in the portal.

---

## KQL

Text files under `Portal/src/JourneyMonitor.Infrastructure/Kql/`, keyed through
`IKqlQueryTextProvider`. `DbKqlQueryTextProvider` overlays `monitoring.KqlQuery` rows on top, with
a 30-second TTL, so a query can be edited from the Settings tab without a deployment. A failed
database read degrades to the built-in text rather than throwing — the built-in is the one that
shipped and is known to work.

Placeholders are `{{LIKE_THIS}}` and are substituted in code. `RequireNoPlaceholders` fails loudly
on any that were missed, because otherwise Azure returns a generic Kusto syntax error that names
nothing useful.

Parameters reach KQL as **base64**, not as escaped strings. Base64's alphabet cannot close a KQL
string literal; escaping can be got wrong. That is the real injection boundary.

---

## The local stack

| Container | Role |
|---|---|
| `jm-sql` | SQL Server 2022 — all persistence |
| `jm-azurite` | Azure Storage emulator — Functions timer leases |
| `jm-functions` | Production-side Functions host |
| `jm-functions-qa` | Staging-side Functions host, separate DB and key ring |
| `jm-web` | Dashboard on port 5080 |
| `jm-sql-init` | Runs once, applies schema and seeds |

`sql-init` can fail on a cold start when SQL is still recovering an existing volume
(`Cannot open database ... Login failed`). It is a race, not corruption — re-run
`docker-compose up -d` once SQL reports healthy.

The compose project name is `portal`, derived from the directory name. Volumes are therefore
`portal_sql-data`, `portal_dataprotection-keys`, and so on. Renaming the `Portal` directory
orphans every volume.
