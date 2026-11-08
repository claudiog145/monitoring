# ai-reliable-platform

.NET 10 port of the Arrivia operations portal (Customer Journeys).

All application code is in [`Portal/`](Portal/). Open **`Portal/JourneyMonitor.slnx`** in Visual Studio.

## First-time setup (Visual Studio)

Run **Azure Functions and the web app in two separate Visual Studio instances**. Do not use Multiple startup projects in one VS: leftover `func.exe` / `JourneyMonitor.Web.exe` lock port 7071 or the `.exe` and the next build fails.

### 1. Prerequisites

- Visual Studio **2026 (18.0+)** with **ASP.NET and web development** and **Azure development** (Azure Functions). The solution targets `net10.0`; Visual Studio 2022 17.x cannot load/build it.
- .NET 10 SDK
- Azure Functions Core Tools v4 (the Azure workload usually installs this)
- `az login` with **Monitoring Reader** on App Insights `sor-frontend-ai-prod-usw` (for live Hotels/Cars data)
- Org NuGet feeds: restore/build **from Visual Studio**, not from a random terminal

### 2. Clone and open

```bash
git clone https://github.com/arriviainc-softeng/ai-reliable-platform.git
cd ai-reliable-platform
```

Open `Portal/JourneyMonitor.slnx`.

### 3. Local Functions settings

`Portal/src/JourneyMonitor.Functions/local.settings.json` is in the repo (Hotels tenant + App Insights Application ID already filled). Do not change Web `appsettings.json` (`AzureAd:TenantId` empty, `JourneyMonitorApi:BaseUrl` = `http://localhost:7071`).

### 4. Run — instance A (Functions)

1. Open a **first** Visual Studio window on `Portal/JourneyMonitor.slnx`.
2. Set startup project: **`JourneyMonitor.Functions`** only (profile in `Properties/launchSettings.json` starts the Functions host on port 7071).
3. F5. Do **not** run that project as a plain console app.

Wait until it listens on **http://localhost:7071**. If it asks for storage / Azurite, start Azurite and F5 again.

### 5. Run — instance B (Web)

1. Open a **second** Visual Studio window on the **same** `Portal/JourneyMonitor.slnx`.
2. Set startup project: **`JourneyMonitor.Web`** only.
3. Profile: **https**.
4. F5.

The browser should open **https://localhost:7152**. Accept the dev certificate if prompted.

### 6. What you should see

- Static tabs (Overview, Platform Health, etc.) work even if Functions is down (local JSON).
- **Customer Journeys** needs Functions. Hotels and Cars should load **live** data after `az login`.
- Air returns 400 until both `Insights__Air__TenantId` and `Insights__Air__AppInsightsAppId` are set in `local.settings.json`. Cruises needs `Insights__Cruises__AppInsightsAppId` (tenant can fall back to Hotels).

### If something is locked

| Error | Fix |
|---|---|
| MSB3027 / cannot copy `JourneyMonitor.Web.exe` | Shift+F5, then kill leftover `JourneyMonitor.Web` |
| `Port 7071 is unavailable` | Kill leftover `func.exe`, then F5 Functions again |

PowerShell:

```powershell
Get-Process func,JourneyMonitor.Web -ErrorAction SilentlyContinue | Stop-Process -Force
```

More detail: [`Portal/README.md`](Portal/README.md).
