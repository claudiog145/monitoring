<#
.SYNOPSIS
Spec 1000 MC6 - E2E of the header company switch (contract v6 sections 2 and 6), Playwright via Node.

.DESCRIPTION
For each company (default arrivia, banco, kinectify, mercadolibre): selects it in the portal header,
waits for the switch and a reload, asserts the header background equals the profile's
theme.primaryColor and the company name is shown, then opens Overview, Customer Journeys, Platform
Health, Analytics, Simian Army, Daemons, Memory Bank and Exceptions and asserts none of them shows
another company's vocabulary or service keys (non-tourism companies: no "Hotels", "Cruise",
"T-Mobile", "Club"; every company: no service key / multi-word service name of the others).
Prints [PASS]/[FAIL] per check and exits 0 only when every check passes. The company that was
active at start is restored at the end.

Known artifact: the gate E2E (.github/spec1000/e2e.ps1) posts test triage signals (kpi E2EManual /
E2ELatency, domain "hotels") under whichever company is active when it runs. Their cards/rows are
excluded from the scan and counted in the check line. Run the gate with arrivia active to avoid
creating new ones under other companies.

-ApplyThemes first copies `theme` from Portal/demo/packs/*/profile.json onto the live profiles
(GET + PUT /api/settings/company-profiles/{key} on Functions, every other member kept).

.EXAMPLE
powershell -NoProfile -ExecutionPolicy Bypass -File Portal/tests/e2e/company-switch.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File Portal/tests/e2e/company-switch.ps1 -ApplyThemes -SettingsWriteSecret <secret>
#>
[CmdletBinding()]
param(
    [string]$PortalUrl = 'http://localhost:5080',
    [string]$FunctionsUrl = 'http://localhost:8081',
    [string[]]$Companies = @('arrivia', 'banco', 'kinectify', 'mercadolibre'),
    [switch]$ApplyThemes,
    [string]$SettingsWriteSecret = $env:JM_SETTINGS_WRITE_SECRET,
    [int]$SettleMs = 2500,
    [switch]$Dump
)

$ErrorActionPreference = 'Stop'
$dir = Join-Path $PSScriptRoot 'company-switch'

if (-not (Get-Command node -ErrorAction SilentlyContinue)) {
    Write-Host '[FAIL] node is not on PATH (Playwright runs through Node).'
    exit 1
}

Push-Location $dir
try {
    if (-not (Test-Path (Join-Path $dir 'node_modules\playwright'))) {
        Write-Host 'Installing Playwright locally (Portal/tests/e2e/company-switch/node_modules)...'
        & npm install --no-audit --no-fund --loglevel=error 2>&1 | ForEach-Object { Write-Host $_ }
        if ($LASTEXITCODE -ne 0) { Write-Host '[FAIL] npm install failed.'; exit 1 }
    }
    # No-op when the matching Chromium build is already in the Playwright cache.
    & npx --no-install playwright install chromium 2>&1 | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0) { Write-Host '[FAIL] playwright install chromium failed.'; exit 1 }

    if ($ApplyThemes) {
        if (-not $SettingsWriteSecret) { Write-Host '[FAIL] -ApplyThemes needs -SettingsWriteSecret or JM_SETTINGS_WRITE_SECRET.'; exit 1 }
        & node (Join-Path $dir 'apply-themes.mjs') --functions $FunctionsUrl --secret $SettingsWriteSecret
        if ($LASTEXITCODE -ne 0) { Write-Host '[FAIL] applying the pack themes failed.'; exit 1 }
    }

    $nodeArgs = @((Join-Path $dir 'company-switch.mjs'), '--portal', $PortalUrl, '--companies', ($Companies -join ','), '--settle-ms', "$SettleMs")
    if ($Dump) { $nodeArgs += '--dump' }
    & node @nodeArgs
    $code = $LASTEXITCODE
}
finally {
    Pop-Location
}

if ($code -eq 0) { Write-Host 'COMPANY-SWITCH E2E -> PASS' } else { Write-Host 'COMPANY-SWITCH E2E -> FAIL' }
exit $code
