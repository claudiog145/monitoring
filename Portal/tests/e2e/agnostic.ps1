<#
.SYNOPSIS
    Spec 1000 AGE2E E2E stage "agnostic": a second company (acme) on the live docker stack, isolated from the incumbent.

.DESCRIPTION
    Against the running docker stack (contract v4 §5, isolation guarantee):
      1. creates company profile 'acme' through the settings API - enabled, NOT activated - with its own services
         (checkout, screening), a partner (watchlists) and a closed Privacy block;
      2. registers acme-owned telemetry sources: 'acme-web' is a STAND-IN that points at the same Application
         Insights app the incumbent's hotel front end uses (so a query has real rows to read), 'acme-screening' is
         a disabled placeholder. Both are created with profileKey=acme, so with AG8 they are owned by acme; the
         profile is saved first without aliases (AG8 needs the owner to exist) and then with them (AG8 needs the
         aliases to be in the catalog);
      3. saves the catalog query 'kpi:acme-alerts' and a Business KPI 'kql:kpi:acme-alerts' (the KPI save runs
         the one-hour dry run against the stand-in app), plus a domain latency KPI on acme's 'checkout' service;
         checks that acme cannot use an incumbent alias in a kql: query nor a service it does not have;
      4. runs KPI ingestion through Run Now (X-Internal-Api-Secret) and asserts acme KpiBucket rows and
         watermarks exist, and that nothing of acme is mixed into the incumbent's KPIs;
      5. asserts isolation of the HTTP reads in both directions: /api/settings/kpis, /api/health/pillars,
         /api/health/anomalies and /api/triage/investigations with profileKey=acme vs arrivia (and the default,
         which follows the active profile);
      6. always (try/finally) removes everything it created: acme KPIs and their buckets, samples, state,
         watermarks, anomalies, triage investigations, queue items and audit rows (SQL - there is no delete API),
         the catalog queries (revert API, then their audit rows), the telemetry sources (delete API, then their
         audit rows) and the profile (SQL - there is no delete API). Daemon activity log lines that mention acme
         are left in place: the log is shared and append-only.
    The incumbent must be untouched: still active, same KPI set and profile version, KPI 1 still BaselinePercent
    7 days / 50 %, same telemetry sources.

    Thresholds are set so the acme KPIs never trigger an anomaly (no LLM call, no notification).
    Exit 0 = all passed, 1 = a check failed.
#>
[CmdletBinding()]
param(
    [string]$FunctionsUrl = 'http://localhost:8081',
    [string]$WebUrl = 'http://localhost:5080'
)

$ErrorActionPreference = 'Continue'
Set-StrictMode -Version 2.0

$saPassword = $env:MSSQL_SA_PASSWORD; if (-not $saPassword) { $saPassword = 'Jm_LocalDev_1!' }
$internalSecret = $env:Monitoring__InternalApiSecret; if (-not $internalSecret) { $internalSecret = 'Jm_LocalDev_InternalApiSecret_1!' }
$settingsSecret = $env:Monitoring__SettingsWriteSecret; if (-not $settingsSecret) { $settingsSecret = 'Jm_LocalDev_SettingsSecret_1!' }

$acme = 'acme'
$incumbent = 'arrivia'
$marker = 'e2e-agnostic'
$webAlias = 'acme-web'
$placeholderAlias = 'acme-screening'
$alertsKey = 'kpi:acme-alerts'
$foreignKey = 'kpi:acme-foreign-probe'
$environment = 'production'

$failures = New-Object System.Collections.Generic.List[string]
function Pass([string]$m) { Write-Host "[PASS] $m" -ForegroundColor Green }
function Fail([string]$m) { Write-Host "[FAIL] $m" -ForegroundColor Red; $failures.Add($m) }
function Info([string]$m) { Write-Host "[INFO] $m" }
function Assert([bool]$cond, [string]$m) { if ($cond) { Pass $m } else { Fail $m } }

function Invoke-Sql([string]$query, [string]$database = 'platformDB') {
    $out = & docker exec jm-sql /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P $saPassword -C -d $database -h -1 -W -b -Q "SET NOCOUNT ON; SET QUOTED_IDENTIFIER ON; $query" 2>&1
    if ($LASTEXITCODE -ne 0) { throw "sqlcmd failed: $out" }
    return ,@($out | ForEach-Object { "$_".Trim() } | Where-Object { $_ -ne '' })
}

function Invoke-Http([string]$method, [string]$url, [hashtable]$headers = @{}, $body = $null) {
    $params = @{ Method = $method; Uri = $url; Headers = $headers; UseBasicParsing = $true; TimeoutSec = 300 }
    if ($null -ne $body) {
        $json = if ($body -is [string]) { $body } else { $body | ConvertTo-Json -Depth 12 }
        $params.Body = [Text.Encoding]::UTF8.GetBytes($json)
        $params.ContentType = 'application/json; charset=utf-8'
    }
    try {
        $r = Invoke-WebRequest @params
        return [pscustomobject]@{ Status = [int]$r.StatusCode; Body = $r.Content }
    } catch [System.Net.WebException] {
        $resp = $_.Exception.Response
        if ($null -eq $resp) { return [pscustomobject]@{ Status = 0; Body = $_.Exception.Message } }
        $reader = New-Object System.IO.StreamReader($resp.GetResponseStream())
        return [pscustomobject]@{ Status = [int]$resp.StatusCode; Body = $reader.ReadToEnd() }
    }
}

function Read-Json($response) {
    if ($null -eq $response -or $response.Status -ne 200 -or [string]::IsNullOrWhiteSpace($response.Body)) { return $null }
    try { return ($response.Body | ConvertFrom-Json) } catch { return $null }
}

function Has([object]$obj, [string]$name) { return $null -ne $obj -and ($obj.PSObject.Properties.Name -contains $name) }

function Short([string]$text) { if ($null -eq $text) { return '' }; if ($text.Length -gt 300) { return $text.Substring(0, 300) + '...' }; return $text }

function Ids($array) { return @(@($array) | Where-Object { $null -ne $_ -and (Has $_ 'kpiDefinitionId') } | ForEach-Object { [int]$_.kpiDefinitionId }) }

function PillarKpiIds($pillars) {
    $ids = @()
    foreach ($p in @($pillars)) { if (Has $p 'kpis') { $ids += Ids $p.kpis } }
    return ,$ids
}

$write = @{ 'X-Settings-Write-Secret' = $settingsSecret }
$internal = @{ 'X-Internal-Api-Secret' = $internalSecret }
$sqlAcme = "N'$acme'"

function Get-AcmeKpiIds { return @((Invoke-Sql "SELECT KpiDefinitionId FROM monitoring.KpiDefinition WHERE ProfileKey = $sqlAcme ORDER BY KpiDefinitionId;") | ForEach-Object { [int]$_ }) }

function Remove-AcmeEverything([bool]$quiet) {
    $problems = @()
    try {
        Invoke-Sql @"
DECLARE @ids TABLE (Id int PRIMARY KEY);
INSERT @ids SELECT KpiDefinitionId FROM monitoring.KpiDefinition WHERE ProfileKey = $sqlAcme;
DELETE q FROM monitoring.AgentWorkQueue q
 WHERE q.PayloadType = N'triage-signal'
   AND q.PayloadId IN (SELECT CONVERT(nvarchar(36), InvestigationId) FROM monitoring.TriageInvestigation WHERE ProfileKey = $sqlAcme);
DELETE FROM monitoring.TriageInvestigation WHERE ProfileKey = $sqlAcme;
DELETE FROM monitoring.KpiAnomaly WHERE KpiDefinitionId IN (SELECT Id FROM @ids);
DELETE FROM monitoring.KpiBucket WHERE KpiDefinitionId IN (SELECT Id FROM @ids);
DELETE FROM monitoring.KpiSample WHERE KpiDefinitionId IN (SELECT Id FROM @ids);
DELETE FROM monitoring.KpiDetectionState WHERE KpiDefinitionId IN (SELECT Id FROM @ids);
DELETE FROM monitoring.IngestionWatermark WHERE KpiDefinitionId IN (SELECT Id FROM @ids);
DELETE FROM monitoring.KpiDefinitionAudit WHERE KpiDefinitionId IN (SELECT Id FROM @ids) OR ProfileKey = $sqlAcme;
DELETE FROM monitoring.KpiDefinition WHERE KpiDefinitionId IN (SELECT Id FROM @ids);
"@ | Out-Null
    } catch { $problems += "KPI rows: $($_.Exception.Message)" }

    foreach ($key in @($alertsKey, $foreignKey)) {
        $exists = (Invoke-Sql "SELECT COUNT(*) FROM monitoring.KqlQuery WHERE QueryKey = N'$key';")
        if ([int]$exists[0] -gt 0) {
            $r = Invoke-Http 'POST' "$FunctionsUrl/api/settings/kql/$([Uri]::EscapeDataString($key))/revert" $write @{ changedBy = $marker }
            if ($r.Status -ne 200) { $problems += "revert $key -> $($r.Status) $(Short $r.Body)" }
        }
    }
    try {
        Invoke-Sql "DELETE FROM monitoring.KqlQuery WHERE QueryKey IN (N'$alertsKey', N'$foreignKey'); DELETE FROM monitoring.KqlQueryAudit WHERE QueryKey IN (N'$alertsKey', N'$foreignKey');" | Out-Null
    } catch { $problems += "KQL rows: $($_.Exception.Message)" }

    foreach ($alias in @($webAlias, $placeholderAlias)) {
        $exists = (Invoke-Sql "SELECT COUNT(*) FROM monitoring.TelemetrySource WHERE Alias = N'$alias';")
        if ([int]$exists[0] -gt 0) {
            $r = Invoke-Http 'POST' "$FunctionsUrl/api/settings/telemetry-sources/$alias/$environment/delete" $write @{ changedBy = $marker }
            if ($r.Status -ne 200) { $problems += "delete source $alias -> $($r.Status) $(Short $r.Body)" }
        }
    }
    try {
        Invoke-Sql "DELETE FROM monitoring.TelemetrySource WHERE Alias IN (N'$webAlias', N'$placeholderAlias'); DELETE FROM monitoring.TelemetrySourceAudit WHERE Alias IN (N'$webAlias', N'$placeholderAlias');" | Out-Null
    } catch { $problems += "source rows: $($_.Exception.Message)" }

    try {
        Invoke-Sql "DELETE FROM monitoring.CompanyProfile WHERE ProfileKey = $sqlAcme; DELETE FROM monitoring.CompanyProfileAudit WHERE ProfileKey = $sqlAcme;" | Out-Null
    } catch { $problems += "profile rows: $($_.Exception.Message)" }

    if (-not $quiet) {
        Assert ($problems.Count -eq 0) ("cleanup: every acme artifact removed" + $(if ($problems.Count -gt 0) { " - " + ($problems -join '; ') } else { '' }))
    }
}

function Snapshot-Incumbent {
    $active = Read-Json (Invoke-Http 'GET' "$FunctionsUrl/api/settings/company-profiles-active")
    # The E2E% fixture KPI is rewritten by the shared 'detection' stage, which may run on this stack at the same time.
    $kpis = (Invoke-Sql "SELECT CONCAT(KpiDefinitionId, '|', Enabled, '|', DetectionMethod, '|', MethodParamsJson) FROM monitoring.KpiDefinition WHERE ProfileKey = N'$incumbent' AND Kpi NOT LIKE N'E2E%' ORDER BY KpiDefinitionId;")
    $profile = (Invoke-Sql "SELECT CONCAT(Version, '|', Enabled, '|', CONVERT(nvarchar(40), UpdatedAt, 127)) FROM monitoring.CompanyProfile WHERE ProfileKey = N'$incumbent';")
    $sources = (Invoke-Sql "SELECT CONCAT(Alias, '|', Environment, '|', Enabled, '|', ResourceId, '|', ISNULL(AppId, N'')) FROM monitoring.TelemetrySource WHERE Alias NOT IN (N'$webAlias', N'$placeholderAlias') ORDER BY Alias, Environment;")
    $kpi1 = (Invoke-Sql "SELECT CONCAT(ProfileKey, '|', DetectionMethod, '|', MethodParamsJson) FROM monitoring.KpiDefinition WHERE KpiDefinitionId = 1;")
    return [pscustomobject]@{
        Active  = $(if ($null -ne $active -and (Has $active 'profileKey')) { [string]$active.profileKey } else { $null })
        Kpis    = ($kpis -join "`n")
        Profile = ($profile -join "`n")
        Sources = ($sources -join "`n")
        Kpi1    = ($kpi1 -join "`n")
    }
}

function New-AcmeProfile([bool]$withAliases) {
    $checkoutAliases = @(); $screeningAliases = @()
    if ($withAliases) { $checkoutAliases = @($webAlias); $screeningAliases = @($placeholderAlias) }
    return [ordered]@{
        profileKey  = $acme
        displayName = 'Acme Screening (E2E)'
        labels      = [ordered]@{ service = 'Service'; flow = 'Flow'; step = 'Step'; entity = 'Store'; owner = 'Team' }
        services    = @(
            [ordered]@{
                key = 'checkout'; displayName = 'Checkout'; telemetrySourceAliases = $checkoutAliases; hostFilters = @()
                flows = @([ordered]@{ key = 'purchase'; displayName = 'Purchase'; steps = @(
                    [ordered]@{ key = 'any-page'; displayName = 'Any page'; order = 1; matchers = @([ordered]@{ kind = 'path-contains'; value = '/'; caseSensitive = $false }) }
                ) })
            },
            [ordered]@{
                key = 'screening'; displayName = 'Screening'; telemetrySourceAliases = $screeningAliases; hostFilters = @()
                flows = @([ordered]@{ key = 'review'; displayName = 'Review'; steps = @(
                    [ordered]@{ key = 'lookup'; displayName = 'Lookup'; order = 1; matchers = @([ordered]@{ kind = 'path-contains'; value = '/lookup'; caseSensitive = $false }) }
                ) })
            }
        )
        entityIdentitySources = @([ordered]@{ order = 1; kind = 'custom-dimension'; key = 'StoreId'; entityKind = 'store' })
        glossary    = [ordered]@{ serviceNoun = 'service'; flowNoun = 'flow'; stepNoun = 'step'; entityNoun = 'store'; ownerNoun = 'team'; description = 'Synthetic second company created and removed by the agnostic E2E stage.' }
        enabled     = $true
        notes       = "Created by $marker; removed when the stage ends."
        updatedBy   = $marker
        partners    = @([ordered]@{ key = 'watchlists'; displayName = 'Watchlists'; matchers = @([ordered]@{ kind = 'dependency-target-contains'; value = 'watchlists.example.test' }) })
        privacy     = [ordered]@{ allowRawEvidence = $false; piiDimensions = @('CustomerRef'); patterns = @() }
    }
}

function New-Kpi([string]$pillar, [string]$kpi, [string]$domain, [string]$sourceKey, [string]$direction, [string]$aggregation, [int]$sampleSize, [string]$methodParams) {
    return [ordered]@{
        kpiDefinitionId = 0; pillar = $pillar; kpi = $kpi; domain = $domain; stage = ''; sourceKey = $sourceKey
        direction = $direction; aggregation = $aggregation; evaluationFrequency = '01:00:00'; bucketSize = '01:00:00'; lookbackWindow = '06:00:00'
        sampleSize = $sampleSize; minObservationsPerBucket = 0; detectionMethod = 'StaticThreshold'; methodParamsJson = $methodParams
        consecutiveCount = 3; cooldownMinutes = 120; enabled = $true; updatedAt = (Get-Date).ToUniversalTime().ToString('o'); updatedBy = $marker; changedBy = $marker
        profileKey = $acme; grain = 'domain'
    }
}

Write-Host "== agnostic: $FunctionsUrl (web $WebUrl)"

$daemons = Invoke-Http 'GET' "$FunctionsUrl/api/daemons"
if ($daemons.Status -ne 200) {
    Fail "GET /api/daemons -> 200 (got $($daemons.Status)); the stack is not ready"
    exit 1
}

# A leftover of an interrupted run is ours only if this stage created it.
$existing = (Invoke-Sql "SELECT ISNULL(UpdatedBy, N'') FROM monitoring.CompanyProfile WHERE ProfileKey = $sqlAcme;")
if ($existing.Count -gt 0 -and $existing[0] -ne $marker) {
    Fail "profile '$acme' already exists and was not created by this stage (UpdatedBy '$($existing[0])'); refusing to touch it"
    exit 1
}
if ($existing.Count -gt 0) { Info 'removing leftovers of a previous agnostic run' }
# Without an acme profile, acme rows are orphans of an interrupted run; this is a no-op when there are none.
Remove-AcmeEverything $true

$before = Snapshot-Incumbent
Assert ($before.Active -eq $incumbent) "the active profile is '$incumbent' before the stage (got '$($before.Active)')"

try {
    # -------------------------------------------------------------- 1. profile (enabled, not active), no aliases yet
    $r = Invoke-Http 'PUT' "$FunctionsUrl/api/settings/company-profiles/$acme" $write (New-AcmeProfile $false)
    Assert ($r.Status -eq 200) "PUT company-profiles/$acme (no aliases yet) -> 200 (got $($r.Status): $(Short $r.Body))"

    # -------------------------------------------------------------- 2. acme-owned telemetry sources
    $catalog = Read-Json (Invoke-Http 'GET' "$FunctionsUrl/api/settings/telemetry-sources")
    $sources = @(); if ($null -ne $catalog -and (Has $catalog 'sources')) { $sources = @($catalog.sources) }
    # Stand-in: the incumbent's queryable hotel front end (the only row with an app id). Same app, acme's own row.
    $standIn = $sources | Where-Object { $_.alias -eq 'sor-frontend' -and $_.environment -eq $environment -and $_.enabled -and $_.appId } | Select-Object -First 1
    if ($null -eq $standIn) { $standIn = $sources | Where-Object { $_.environment -eq $environment -and $_.enabled -and $_.appId } | Select-Object -First 1 }
    Assert ($null -ne $standIn) 'a queryable telemetry source exists to stand in for acme'
    if ($null -eq $standIn) { throw 'no stand-in telemetry source' }
    Info "stand-in app: alias '$($standIn.alias)' (acme gets its own row '$webAlias' pointing at the same app)"

    $r = Invoke-Http 'PUT' "$FunctionsUrl/api/settings/telemetry-sources" $write ([ordered]@{
        alias = $webAlias; environment = $environment; kind = 'app-insights'; resourceId = $standIn.resourceId
        appId = $standIn.appId; tenantId = $standIn.tenantId; enabled = $true
        notes = "E2E stand-in for acme (same app as '$($standIn.alias)'); removed by $marker."; changedBy = $marker; profileKey = $acme })
    Assert ($r.Status -eq 200) "PUT telemetry-sources $webAlias (profileKey=acme) -> 200 (got $($r.Status): $(Short $r.Body))"
    $r = Invoke-Http 'PUT' "$FunctionsUrl/api/settings/telemetry-sources" $write ([ordered]@{
        alias = $placeholderAlias; environment = $environment; kind = 'app-insights'; resourceId = $standIn.resourceId
        enabled = $false; notes = "E2E disabled placeholder for acme; removed by $marker."; changedBy = $marker; profileKey = $acme })
    Assert ($r.Status -eq 200) "PUT telemetry-sources $placeholderAlias (disabled placeholder) -> 200 (got $($r.Status): $(Short $r.Body))"

    $catalog = Read-Json (Invoke-Http 'GET' "$FunctionsUrl/api/settings/telemetry-sources")
    $ownRow = $null; if ($null -ne $catalog) { $ownRow = @($catalog.sources) | Where-Object { $_.alias -eq $webAlias } | Select-Object -First 1 }
    if ($null -ne $ownRow -and (Has $ownRow 'ownerProfileKey')) {
        Assert ($ownRow.ownerProfileKey -eq $acme) "$webAlias is owned by '$acme' (got '$($ownRow.ownerProfileKey)')"
        $r = Invoke-Http 'PUT' "$FunctionsUrl/api/settings/company-profiles/$acme" $write ((New-AcmeProfile $true) | ForEach-Object { $_.services[0].telemetrySourceAliases = @($webAlias, $standIn.alias); $_ })
        Assert ($r.Status -eq 400 -and $r.Body -like "*belongs to profile*") "acme cannot claim the incumbent alias '$($standIn.alias)' -> 400 (got $($r.Status): $(Short $r.Body))"
    } else {
        Info 'telemetry source ownership (AG8) is not in this build; ownership assertions skipped'
    }

    # -------------------------------------------------------------- 3. profile with its own aliases
    $r = Invoke-Http 'PUT' "$FunctionsUrl/api/settings/company-profiles/$acme" $write (New-AcmeProfile $true)
    Assert ($r.Status -eq 200) "PUT company-profiles/$acme with its own aliases -> 200 (got $($r.Status): $(Short $r.Body))"
    $active = Read-Json (Invoke-Http 'GET' "$FunctionsUrl/api/settings/company-profiles-active")
    Assert ($null -ne $active -and $active.profileKey -eq $incumbent) "acme is enabled but NOT active (active '$(if ($null -ne $active) { $active.profileKey })')"

    # -------------------------------------------------------------- 4. kql: query + KPIs
    $alertsText = @"
// $marker - requests per bucket of acme's stand-in app.
app('{{SOURCE:$webAlias}}').requests
| where timestamp >= {{from}} and timestamp < {{to}}
| summarize TotalCount = sum(itemCount) by BucketStart = bin_at(timestamp, {{bucket}}, datetime(1970-01-01))
| project ['kind'] = "bucket", BucketStart, TotalCount = tolong(TotalCount), Value = todouble(TotalCount)
"@
    $r = Invoke-Http 'PUT' "$FunctionsUrl/api/settings/kql/$([Uri]::EscapeDataString($alertsKey))" $write ([ordered]@{ kqlText = $alertsText; description = 'acme alerts (E2E)'; enabled = $true; changedBy = $marker })
    Assert ($r.Status -eq 200) "PUT settings/kql/$alertsKey -> 200 (got $($r.Status): $(Short $r.Body))"

    $alertsKpi = New-Kpi 'Business' 'Alerts' 'checkout' "kql:$alertsKey" 'Decrease' 'Count' 0 '{"threshold":-1}'
    $r = Invoke-Http 'POST' "$FunctionsUrl/api/settings/kpis" $write $alertsKpi
    Assert ($r.Status -eq 200) "POST kql:$alertsKey Business KPI (dry run against the stand-in app) -> 200 (got $($r.Status): $(Short $r.Body))"
    $alertsSaved = Read-Json $r

    $latencyKpi = New-Kpi 'Application' 'Latency' 'checkout' 'appinsights:latency' 'Increase' 'P95' 60 '{"threshold":100000000}'
    $r = Invoke-Http 'POST' "$FunctionsUrl/api/settings/kpis" $write $latencyKpi
    Assert ($r.Status -eq 200) "POST appinsights:latency domain KPI on acme 'checkout' -> 200 (got $($r.Status): $(Short $r.Body))"
    $latencySaved = Read-Json $r

    # Isolation on write: an incumbent service, and an incumbent alias inside a kql: query, are refused for acme.
    $r = Invoke-Http 'POST' "$FunctionsUrl/api/settings/kpis" $write (New-Kpi 'Application' 'Latency' 'hotels' 'appinsights:latency' 'Increase' 'P95' 60 '{"threshold":100000000}')
    Assert ($r.Status -eq 400) "POST acme KPI on the incumbent service 'hotels' -> 400 (got $($r.Status): $(Short $r.Body))"
    $foreignText = $alertsText.Replace("{{SOURCE:$webAlias}}", "{{SOURCE:$($standIn.alias)}}")
    $r = Invoke-Http 'PUT' "$FunctionsUrl/api/settings/kql/$([Uri]::EscapeDataString($foreignKey))" $write ([ordered]@{ kqlText = $foreignText; description = 'acme foreign alias probe (E2E)'; enabled = $true; changedBy = $marker })
    if ($r.Status -eq 200) {
        $r = Invoke-Http 'POST' "$FunctionsUrl/api/settings/kpis" $write (New-Kpi 'Business' 'ForeignProbe' 'checkout' "kql:$foreignKey" 'Decrease' 'Count' 0 '{"threshold":-1}')
        Assert ($r.Status -eq 400 -and $r.Body -like "*$($standIn.alias)*") "acme kql: KPI reading the incumbent alias '$($standIn.alias)' -> 400 (got $($r.Status): $(Short $r.Body))"
    } else {
        Info "foreign-alias probe query refused at save ($($r.Status)); KPI probe skipped"
    }

    $acmeIds = Get-AcmeKpiIds
    Assert ($acmeIds.Count -eq 2) "exactly the two acme KPIs exist (got $($acmeIds -join ','))"
    $alertsId = $(if ($null -ne $alertsSaved -and (Has $alertsSaved 'kpiDefinitionId')) { [int]$alertsSaved.kpiDefinitionId } else { 0 })
    $latencyId = $(if ($null -ne $latencySaved -and (Has $latencySaved 'kpiDefinitionId')) { [int]$latencySaved.kpiDefinitionId } else { 0 })

    # -------------------------------------------------------------- 5. ingestion (Run Now), retried while a timer run holds the lock
    $deadline = (Get-Date).AddMinutes(6)
    $ingested = $false
    while (-not $ingested -and (Get-Date) -lt $deadline) {
        $run = Invoke-Http 'POST' "$FunctionsUrl/api/monkeys/kpi-ingestion/run" $internal
        if ($run.Status -ne 200) { Fail "Run Now kpi-ingestion -> 200 (got $($run.Status): $(Short $run.Body))"; break }
        $marks = (Invoke-Sql "SELECT COUNT(*) FROM monitoring.IngestionWatermark WHERE KpiDefinitionId IN ($($acmeIds -join ','));")
        if ([int]$marks[0] -ge $acmeIds.Count) { $ingested = $true } else { Info "acme watermarks: $($marks[0])/$($acmeIds.Count); retrying Run Now"; Start-Sleep -Seconds 10 }
    }
    Assert $ingested 'Run Now kpi-ingestion processed every acme KPI (watermark per KPI)'

    $alertsBuckets = [int](Invoke-Sql "SELECT COUNT(*) FROM monitoring.KpiBucket WHERE KpiDefinitionId = $alertsId;")[0]
    Assert ($alertsBuckets -gt 0) "KpiBucket rows for kql:$alertsKey (got $alertsBuckets)"
    $latencyBuckets = [int](Invoke-Sql "SELECT COUNT(*) FROM monitoring.KpiBucket WHERE KpiDefinitionId = $latencyId;")[0]
    Assert ($latencyBuckets -gt 0) "KpiBucket rows for acme checkout latency (got $latencyBuckets)"
    $mixed = [int](Invoke-Sql "SELECT COUNT(*) FROM monitoring.KpiDefinition WHERE KpiDefinitionId IN ($($acmeIds -join ',')) AND ProfileKey <> $sqlAcme;")[0]
    Assert ($mixed -eq 0) 'no acme KPI is stored under another profile'
    $foreignBuckets = [int](Invoke-Sql "SELECT COUNT(*) FROM monitoring.KpiBucket b JOIN monitoring.KpiDefinition d ON d.KpiDefinitionId = b.KpiDefinitionId WHERE d.ProfileKey = $sqlAcme AND d.KpiDefinitionId NOT IN ($($acmeIds -join ','));")[0]
    Assert ($foreignBuckets -eq 0) 'every acme bucket belongs to an acme definition'
    $b = Read-Json (Invoke-Http 'GET' "$FunctionsUrl/api/health/kpis/$alertsId/buckets?hours=12")
    Assert ($null -ne $b -and @($b).Count -gt 0 -and -not (@($b) | Where-Object { [int]$_.kpiDefinitionId -ne $alertsId })) "GET health/kpis/$alertsId/buckets -> only that acme KPI's buckets"

    # -------------------------------------------------------------- 6. isolation of the reads, both directions
    $acmeKpis = @(Read-Json (Invoke-Http 'GET' "$FunctionsUrl/api/settings/kpis?profileKey=$acme"))
    $acmeKpiIds = Ids $acmeKpis
    Assert ((@($acmeKpiIds | Sort-Object) -join ',') -eq (@($acmeIds | Sort-Object) -join ',')) "GET settings/kpis?profileKey=acme -> exactly acme's KPIs (got $($acmeKpiIds -join ','))"
    Assert (-not ($acmeKpis | Where-Object { $_.profileKey -ne $acme })) 'every acme KPI read carries profileKey acme'

    $incumbentKpis = @(Read-Json (Invoke-Http 'GET' "$FunctionsUrl/api/settings/kpis?profileKey=$incumbent"))
    $incumbentIds = Ids $incumbentKpis
    Assert ($incumbentIds.Count -gt 0 -and -not ($incumbentIds | Where-Object { $acmeIds -contains $_ })) "GET settings/kpis?profileKey=arrivia -> no acme KPI ($($incumbentIds.Count) KPIs)"
    Assert (-not ($incumbentKpis | Where-Object { $_.profileKey -ne $incumbent })) 'every arrivia KPI read carries profileKey arrivia'
    $defaultIds = Ids @(Read-Json (Invoke-Http 'GET' "$FunctionsUrl/api/settings/kpis"))
    Assert ($defaultIds.Count -eq $incumbentIds.Count -and -not ($defaultIds | Where-Object { $acmeIds -contains $_ })) 'GET settings/kpis (active profile) -> the incumbent set, no acme KPI'
    $web = Invoke-Http 'GET' "$WebUrl/api/settings/kpis?profileKey=$acme"
    if ($web.Status -eq 200) {
        $webIds = Ids @(Read-Json $web)
        Assert ((@($webIds | Sort-Object) -join ',') -eq (@($acmeIds | Sort-Object) -join ',')) 'Web proxy settings/kpis?profileKey=acme -> exactly acme KPIs'
    } else {
        Info "Web proxy settings/kpis?profileKey=acme -> $($web.Status); proxy check skipped"
    }

    $p = Invoke-Http 'GET' "$FunctionsUrl/api/health/pillars?domain=checkout&profileKey=$acme"
    $pillars = @(Read-Json $p)
    $pillarIds = PillarKpiIds $pillars
    Assert ($p.Status -eq 200 -and $pillarIds.Count -gt 0 -and -not ($pillarIds | Where-Object { $acmeIds -notcontains $_ })) "GET health/pillars?domain=checkout&profileKey=acme -> only acme KPIs (got $($pillarIds -join ','))"
    Assert (-not ($pillars | Where-Object { (Has $_ 'profileKey') -and $_.profileKey -ne $acme })) 'acme pillar read is scoped to acme'
    $p = Invoke-Http 'GET' "$FunctionsUrl/api/health/pillars?domain=checkout&profileKey=$incumbent"
    $ids = PillarKpiIds @(Read-Json $p)
    Assert ($p.Status -eq 200 -and -not ($ids | Where-Object { $acmeIds -contains $_ })) 'GET health/pillars?domain=checkout&profileKey=arrivia -> no acme KPI'
    $p = Invoke-Http 'GET' "$FunctionsUrl/api/health/pillars?domain=hotels&profileKey=$acme"
    $ids = PillarKpiIds @(Read-Json $p)
    Assert ($p.Status -eq 200 -and $ids.Count -eq 0) "GET health/pillars?domain=hotels&profileKey=acme -> no KPI (got $($ids -join ','))"
    $p = Invoke-Http 'GET' "$FunctionsUrl/api/health/pillars"
    $ids = PillarKpiIds @(Read-Json $p)
    Assert ($p.Status -eq 200 -and -not ($ids | Where-Object { $acmeIds -contains $_ })) 'GET health/pillars (active profile) -> no acme KPI'

    $a = Invoke-Http 'GET' "$FunctionsUrl/api/health/anomalies?profileKey=$incumbent&top=500"
    $ids = @(@(Read-Json $a) | Where-Object { $null -ne $_ } | ForEach-Object { [int]$_.kpiDefinitionId })
    Assert ($a.Status -eq 200 -and -not ($ids | Where-Object { $acmeIds -contains $_ })) 'GET health/anomalies?profileKey=arrivia -> no acme anomaly'
    $a = Invoke-Http 'GET' "$FunctionsUrl/api/health/anomalies?profileKey=$acme&top=500"
    $ids = @(@(Read-Json $a) | Where-Object { $null -ne $_ } | ForEach-Object { [int]$_.kpiDefinitionId })
    Assert ($a.Status -eq 200 -and -not ($ids | Where-Object { $acmeIds -notcontains $_ })) 'GET health/anomalies?profileKey=acme -> only acme anomalies'

    $t = Invoke-Http 'GET' "$FunctionsUrl/api/triage/investigations?profileKey=$incumbent"
    $tj = Read-Json $t
    $leaks = @(@($tj) | Where-Object { $null -ne $_ -and (Has $_ 'signal') -and $null -ne $_.signal -and (Has $_.signal 'profileKey') -and $_.signal.profileKey -eq $acme })
    Assert ($t.Status -eq 200 -and $leaks.Count -eq 0) 'GET triage/investigations?profileKey=arrivia -> no acme investigation'
    $t = Invoke-Http 'GET' "$FunctionsUrl/api/triage/investigations?profileKey=$acme"
    $tj = Read-Json $t
    $leaks = @(@($tj) | Where-Object { $null -ne $_ -and (Has $_ 'signal') -and $null -ne $_.signal -and (Has $_.signal 'profileKey') -and $_.signal.profileKey -ne $acme })
    Assert ($t.Status -eq 200 -and $leaks.Count -eq 0) 'GET triage/investigations?profileKey=acme -> no incumbent investigation'
}
catch {
    Fail "agnostic stage aborted: $($_.Exception.Message)"
}
finally {
    Remove-AcmeEverything $false
}

# -------------------------------------------------------------- 7. nothing left behind, incumbent untouched
$left = (Invoke-Sql @"
SELECT CONCAT(
  (SELECT COUNT(*) FROM monitoring.CompanyProfile WHERE ProfileKey = $sqlAcme), '|',
  (SELECT COUNT(*) FROM monitoring.CompanyProfileAudit WHERE ProfileKey = $sqlAcme), '|',
  (SELECT COUNT(*) FROM monitoring.KpiDefinition WHERE ProfileKey = $sqlAcme), '|',
  (SELECT COUNT(*) FROM monitoring.KpiDefinitionAudit WHERE ProfileKey = $sqlAcme), '|',
  (SELECT COUNT(*) FROM monitoring.TriageInvestigation WHERE ProfileKey = $sqlAcme), '|',
  (SELECT COUNT(*) FROM monitoring.TelemetrySource WHERE Alias IN (N'$webAlias', N'$placeholderAlias')), '|',
  (SELECT COUNT(*) FROM monitoring.TelemetrySourceAudit WHERE Alias IN (N'$webAlias', N'$placeholderAlias')), '|',
  (SELECT COUNT(*) FROM monitoring.KqlQuery WHERE QueryKey IN (N'$alertsKey', N'$foreignKey')), '|',
  (SELECT COUNT(*) FROM monitoring.KqlQueryAudit WHERE QueryKey IN (N'$alertsKey', N'$foreignKey')));
"@)
Assert ($left.Count -eq 1 -and $left[0] -eq '0|0|0|0|0|0|0|0|0') "no acme row left (profile|audit|kpi|kpiAudit|triage|source|sourceAudit|kql|kqlAudit = $($left -join ''))"
$orphans = [int](Invoke-Sql "SELECT (SELECT COUNT(*) FROM monitoring.KpiBucket b WHERE NOT EXISTS (SELECT 1 FROM monitoring.KpiDefinition d WHERE d.KpiDefinitionId = b.KpiDefinitionId)) + (SELECT COUNT(*) FROM monitoring.IngestionWatermark w WHERE NOT EXISTS (SELECT 1 FROM monitoring.KpiDefinition d WHERE d.KpiDefinitionId = w.KpiDefinitionId));")[0]
Assert ($orphans -eq 0) "no bucket or watermark left without its KPI (got $orphans)"

$after = Snapshot-Incumbent
Assert ($after.Active -eq $incumbent) "'$incumbent' is still the active profile (got '$($after.Active)')"
Assert ($after.Kpis -eq $before.Kpis) 'the incumbent KPI set is unchanged'
Assert ($after.Profile -eq $before.Profile) 'the incumbent profile is unchanged'
Assert ($after.Sources -eq $before.Sources) 'the other telemetry sources are unchanged'
Assert ($after.Kpi1 -like "$incumbent|BaselinePercent|*" -and $after.Kpi1 -like '*"lookbackDays":7*' -and $after.Kpi1 -like '*"thresholdPercent":50*') "KPI 1 is still BaselinePercent 7d/50 ($($after.Kpi1))"

if ($failures.Count -gt 0) {
    Write-Host "agnostic: $($failures.Count) check(s) failed" -ForegroundColor Red
    exit 1
}
Write-Host 'agnostic: all checks passed' -ForegroundColor Green
exit 0
