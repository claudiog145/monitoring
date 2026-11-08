# Demo on a new machine — `demo-setup.ps1`

> Español: [DEMO-SETUP.md](DEMO-SETUP.md)

Leaves the **Agentic Operations Portal** (http://localhost:5080) running against a **fake Azure** with synthetic
data for 4 companies: Arrivia (travel), Banco Andino (banking), Kinectify (AML) and Mercado Libre (retail).
There is no need to copy data between machines: telemetry is **regenerated** (deterministic, same seed) and the
platform is **seeded** from the repository packs (`Portal/demo/packs`).

## Requirements

| | |
|---|---|
| Windows + PowerShell 5.1 or later | |
| Docker Desktop | with **14 GB or more** for containers (Settings → Resources). With less, the script falls back to the `tiny` scale. |
| Free disk space | ~30 GB (8 days of telemetry take ~23 GB in Kustainer) |
| LLM token (`Ai__GitHubToken`) | the script asks for it if missing. Without a token, detection still works, but there are no LLM investigations or Memory Bank. |
| Cloned repository | `git clone` of the repo, checked out on the demo branch |

## Prepare (one time only, ~45-60 min the first time)

```powershell
cd <repo>\Portal\demo\scripts
.\demo-setup.ps1
```

The script, in order:

1. Checks Docker and the available memory.
2. Writes the demo block into `Portal/.env` (the token stays only in that file, which is not versioned).
3. Builds and starts SQL, Azurite, Functions, Web, Kustainer, the fake Azure and the generator.
4. Generates **8 days of history** (~12.8 M events, ~8 min) and restarts Kustainer to free memory.
5. Loads profiles, telemetry sources, KQL queries and KPIs for the 4 companies.
6. Imports each pack's RCAs into the **Memory Bank**.
7. Creates the **"· demo 1m"** KPIs (1-minute buckets, fixed threshold: detection in ~2 min) and waits for the
   daemons to load their history (12 h) in order to **calibrate** the thresholds. This is the longest step (20-40 min).
8. Leaves Arrivia as the active company, monkeys and daemons enabled, queue empty.
9. Verifies everything and prints a per-company summary.

You can re-run it whenever you want: whatever already exists is reused.

| Option | What it does |
|---|---|
| `-Fresh` | Deletes the synthetic telemetry and the `platformDB` database (takes a backup first) and prepares everything from scratch. Asks for confirmation (`-Force` skips it). |
| `-SkipBuild` | Does not rebuild images. |
| `-SkipReset` | Does not regenerate telemetry if it already exists. |
| `-SkipCorpus` | Does not import the Memory Bank. |
| `-IncludeArriviaCorpus` | Also imports Arrivia's real RCAs from `docs/RCAS-md` (not versioned; only if you copied them). |
| `-ActiveCompany banco` | Active company when it finishes. |
| `-Days 8 -Scale laptop` | Days of history and scale (`tiny`, `laptop`, `showcase`). |

## Demo day

| When | What |
|---|---|
| 25 min before | `docker restart jm-kustainer` (frees memory; data is preserved) |
| 20 min before | `.\demo-incidents.ps1 -All -Minutes 90` (one incident in every company) |
| 15 min before | `.\demo-incidents.ps1 -Detections` (the 4 companies in `[OK]`) |
| 15 → 3 min before | Optional: switch the active company through all 4 (**Company** selector, ~3 min each) so Scouts and the Inspector already have cases in every company |
| Demo | Switch companies with the **Company** selector: Daemons, Triage and Simian Army show their anomalies and investigations |
| When finished | `.\demo-incidents.ps1 -Stop` |

During the demo, do not run builds, tests or `docker restart`.

## How detection works in the demo

- **Daemons** (KPI ingestion + monitors) evaluate the 4 companies at once. The "· demo 1m" KPIs detect an
  incident in ~1.5-2.5 min; the calibrated KPIs from the packs take 10-25 min.
- **Triage** creates one investigation per anomaly; the **Inspector** completes it in 30-90 s.
- **Scouts** (`Method` mode) follow the daemon anomalies of the **active company**: every 2 min they deliver an
  "Investigate now" package per anomaly, with evidence from the whole affected service, and the Inspector investigates it.
- **RCA**: written only when the Inspector names a root cause or its confidence is above 30%. With synthetic data
  it usually stays below that; to show one, use the button on the case.

## If something fails

| Symptom | What to do |
|---|---|
| The portal or the API do not respond | `docker stats --no-stream jm-kustainer`. If it is close to its memory limit: `docker restart jm-kustainer`. |
| `docker` returns "500 Internal Server Error" | Docker Desktop is saturated: wait 1-2 min; if it persists, `docker restart jm-kustainer` or restart Docker Desktop. |
| `-Detections` does not show a company | Wait 3 more minutes; check `.\demo-incidents.ps1 -Check` and that the incident is active (`-Status`). |
| The setup finished with uncalibrated KPIs | Re-run `.\demo-setup.ps1 -SkipBuild -SkipReset -SkipCorpus`. |
| You want to start from scratch | `.\demo-setup.ps1 -Fresh` |

## What does NOT travel with the repo

- Arrivia's real RCAs (`docs/RCAS-md`) are not in the repository. On another machine Arrivia uses the RCAs from its
  pack (`packs/tourism/corpus`), unless you copy that folder and use `-IncludeArriviaCorpus`.
- Investigations, cases and RCAs generated in other demos: the setup starts clean.
