# FakeAppInsights

A local stand-in for the Application Insights REST query API (contract v5 §3,
`.github/spec1000/contracts-demo-telemetry.md`). It answers
`POST|GET /v1/apps/{appId}/query` by running the KQL on a Kustainer engine, so the platform can be
pointed at it with `Insights:QueryEndpoint` and never talks to Azure.

| Endpoint | Purpose |
|---|---|
| `POST /v1/apps/{appId}/query` | Body `{ "query": "<KQL>", "timespan": "<ISO 8601>"? }` |
| `GET /v1/apps/{appId}/query?query=…&timespan=…` | Same, as query string |
| `GET /v1/apps` | `[{ "appId", "database" }]` — every application the server answers to |
| `GET /health` | `{"status":"ok","kusto":"reachable|unreachable"}` |

Configuration: `FakeAppInsights:KustoUrl` (Kustainer base URL, e.g. `http://kustainer:8080`) and
`FakeAppInsights:ApiKey` (value expected in the `X-Api-Key` header).

## appId → database (the rule DemoGenerator must follow)

Every demo "application" is **one Kustainer database** holding the six App Insights tables of
`Portal/demo/kusto/schema.kql`. Its name is the archetype `apps[].database` (e.g. `bank-web-banking`).

The appId of each application is registered in a dedicated registry database:

| Item | Value |
|---|---|
| Registry database | `_demo_registry` |
| Registry table | `_app(appId:string, database:string)` |
| One row per | archetype app: `appId` = `apps[].appId` (e.g. `demo-bank-web-banking`), `database` = `apps[].database` |

How DemoGenerator registers (idempotent, safe to repeat on every reset):

```kusto
// management endpoint, db = "NetDefaultDB"
.create database ['_demo_registry'] volatile ifnotexists   // same persistence mode as the app databases
                                                           // (`persist` needs explicit paths on Kustainer)
// management endpoint, db = "_demo_registry"
.create-merge table _app (appId:string, database:string)
.set-or-replace _app <| _app
    | where appId !in ('demo-bank-web-banking')     // the appIds being (re)registered
    | union (datatable(appId:string, database:string) ['demo-bank-web-banking', 'bank-web-banking'])
```

How FakeAppInsights resolves the `{appId}` of the URL (first match wins, case-insensitive):

1. **Registry:** a row of `_demo_registry._app` with that `appId` whose `database` exists.
2. **Fallback — same name:** a database whose name equals `appId`.
3. **Fallback — `demo-` prefix:** if `appId` starts with `demo-`, a database named `appId` without the
   prefix (so `demo-bank-web-banking` → `bank-web-banking` even if the registry row is missing).
4. Otherwise `404 ApplicationNotFoundError`.

`app('<x>')` inside a query (contract §3.1) accepts an ARM component id
(`/subscriptions/*/resourceGroups/*/providers/microsoft.insights/components/<name>`), a database name
or an appId: `<name>` is used if it is a database, else it is resolved as an appId with the steps above.

Other rules:

- Databases whose name starts with `_`, and Kustainer's own `NetDefaultDB`, are never applications.
- If the registry has two rows for the same `appId`, the one with the lowest `database` (ordinal) wins;
  DemoGenerator should not produce that (the `set-or-replace` above prevents it).
- The catalog (`.show databases` + the registry) is cached for **30 s**. An unknown appId forces one
  refresh (at most every 2 s), so a database created by a reset is visible on the next query.
- Database names must match `^[A-Za-z0-9._-]{1,260}$`.
- `GET /v1/apps` lists every registered appId whose database exists, plus every other application
  database with `appId` = its own name.

## timespan

When `timespan` is present (`PT6H`, `P1D`, `<start>/<end>`, `<start>/<duration>`), every reference to
`requests`, `exceptions`, `dependencies`, `traces`, `pageViews` and `customEvents` — unqualified or via
`database()`/`app()` — becomes `(T | where timestamp between (from .. to))`.

Queries that read tables **without naming them as a table expression** cannot be filtered that way, so
when a `timespan` is present they are **rejected** with `400 BadArgumentError` (decision: reject, not
filter):

- `union *` or any wildcard table name (`union req*`, `union withsource=T *`),
- every `find` (also `find in (requests) …`: Kusto only accepts table names inside `in (…)`, so a
  filtered sub-query cannot be placed there — verified on Kustainer, `SYN0002`),
- a `search` that is not piped from a table (`search 'x'`, `search in (requests) 'x'`), same reason.

`requests | search 'x'` and `(requests | union exceptions) | search 'x'` are accepted: the tables are
filtered before `search`. Without `timespan` all of these constructs are passed to the engine unchanged.
The platform does not use any of them (see `Portal/demo/compat/compat-report.md`).

## Response format

`{ "tables": [ { "name", "columns": [ { "name", "type" } ], "rows": [ [ … ] ] } ] }`: result tables only
(Kusto metadata tables such as `@ExtendedProperties` or `QueryStatus` are dropped), named
`PrimaryResult`, `Table_1`, `Table_2`… Types follow App Insights (`real`, `long`, `int`, `bool`,
`datetime`, `timespan`, `guid`, `decimal`, `string`, `dynamic`); `datetime` is ISO 8601 UTC with `Z`;
`dynamic` values are JSON **strings**. KQL errors → `400 BadArgumentError` with the engine message in
`error.innererror.message`; wrong or missing `X-Api-Key` → `401 InvalidApiKeyError`.

## Logging

One Information line per query with appId, database, elapsed ms, tables and rows. The query text is
logged only at Debug level.
