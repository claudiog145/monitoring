# DT0 evidence - `docker compose` without the demo profile is unchanged

Captured from `Portal/` on the DT0 branch, comparing the base commit (b750399) with this change.

| Command | Services |
|---|---|
| base: `docker compose config --services` | azurite, functions, functions-qa, sql, sql-init, web |
| DT0: `docker compose config --services` | azurite, functions, functions-qa, sql, sql-init, web |
| DT0: `docker compose --profile demo config --services` | azurite, demo-generator, fake-appinsights, functions, functions-demo, functions-qa, kustainer, sql, sql-init, web, web-demo |

Full rendered `docker compose config` (no profile), base vs DT0 - every differing line:

```
=> MSSQL_DATABASE_DEMO: platformDemo
```

The only difference is the new `MSSQL_DATABASE_DEMO` variable of `sql-init`, which makes it provision
`platformDemo` with the same scripts as `platformDB` (contract v5 section 2). No service is added, removed or
otherwise changed without `--profile demo`.