# Application Map workbooks (US 241135)

Azure Monitor shared workbooks opened from Application Map **product badges**
(Hotels / Cars / Cruises), filtered by ClubId / PartnerId.

The Map **Telemetry** column does **not** open these workbooks — it opens the
matching App Insights **Logs** blade for free-form queries.

## Where KQL lives

- **KQL:** Stored **inside the Azure workbook resource**. These JSON files are the
  source of truth for recreate / update via the `CREATE-*-WORKBOOK.ps1` scripts.
- **Execution:** Azure Portal runs the KQL against App Insights when you open the workbook.

| File | Azure resource |
|---|---|
| `application-map-hotels-by-clubid.json` | workbook `87e5efd7-…` on `sor-frontend` |
| `application-map-cars-by-clubid.json` | workbook `b2c4e8f0-…` on `sor-frontend` |
| `application-map-cruises-by-clubid.json` | workbook `c7d8e9f0-…` saved on `cruise-core`; KQL reads `cruise-web` by PartnerId and `product = cruise` |
| `application-map-flights-by-clubid.json` | In repo for the later Flights US. The badge does not link yet. |

## Operator params (Jamabelle / Matilde)

- **Time range** — presets 1h → 30d, plus custom range (`allowCustom`). Chart and event list both follow it. The bucket follows the range.
- Hotels and Cars both read `sor-frontend`. Hotels keeps hotel search, rooms, `/rentals/`, and booking calls. Cars keeps `CarSearch`, `CarBooking`, `CarLocationSearch`, `searchlocations`, and `/cars/`. Same markers the journey split already uses.
- **Latest events** — 100 / 500 / 1,000 / 5,000. This limits only the raw event list, the newest events inside the selected range. It does not redraw the chart.

Deep-link from the Map seeds ClubId and TimeRange=24h. Cruises also seeds PartnerId. Hotels and Cars do not.
Operators change Time range and Latest events inside the workbook UI.
It does not send RowLimit. The workbook keeps its own table-row default (1,000).

## Recreate / update in Azure

Requires `az login` on ICE tenant (`60396ff5-…`), subscription `89780439-…`.

```powershell
cd Portal/workbooks/application-map
.\CREATE-HOTELS-WORKBOOK.ps1
.\CREATE-CARS-WORKBOOK.ps1
.\CREATE-CRUISES-WORKBOOK.ps1
```

See also `SUMMARY-241135-for-Cristiano.md`.

## Observed maturity (US 241624)

Product badges on Application Map read `brand.ClubProductMaturityStatus`.
Docker/seed leave most rows at **0 (Not Observed)**. To show **1 (Observed)** for
clubs we already monitor, someone must run this **manual** SQL patch (it is **not**
in `docker/init-sql.sh` and does **not** run on portal start or deploy):

`Portal/src/JourneyMonitor.Infrastructure/Sql/PatchClubProductMaturity_Observed.sql`

**What it does:** raises maturity **0 → 1** for Hotels/Cars (by `ClubId` in
`brand.Information`) and Cruises (by `IcePartnerId` in `Information`). Never
inserts Map product rows (skips UI `—`). Does not touch Flights. Never demotes
existing levels 2/3 (only raises eligible rows 0 → 1).

**UI prerequisite:** web build with `CLUB_MAP_USE_LIVE_MATURITY = true` in `app.js`
(without that flag the Map paints every badge as 0 even if SQL is correct).

### Local (Docker)

1. SSMS or sqlcmd → `127.0.0.1,1433` / database `platformDB` / `sa` + `MSSQL_SA_PASSWORD`.
2. Open the patch → leave `ApplyUpdates = 0` → Execute → review PREVIEW.
3. Set `ApplyUpdates = 1` → Execute again.
4. Rebuild web (`docker compose up -d --build web`) and hard-refresh the Map;
   filter **Product Status = 1 - Observed**.

### QA / PROD

1. Run the same preview → apply steps on the **database the Map API reads**.
2. Deploy/refresh the web app that has live maturity enabled.
3. Re-run the patch when observed coverage changes (new club in `Information`,
   partner id filled in, etc.). It is idempotent.

Full step-by-step and match rules are in the script header.
