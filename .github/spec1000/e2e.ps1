<#
.SYNOPSIS
  Spec 1000 end-to-end checks against the local docker compose stack (jm-*).

.DESCRIPTION
  Each level runs a fixed set of stages (see $stageMap).
    schema    - tables exist in platformDB and platformDBQA, 24 seeded KPI rows, sql-init re-run is idempotent.
    api       - Triage API (401/202/duplicate/GET), health and settings reads.
    detection - KPI rule end to end: synthetic buckets -> application-health-monitor -> KpiAnomaly ->
                TriageInvestigation -> inspector-monkey processes it -> no e-mail sent (shadow).
    portal    - Web proxy routes and served app.js has no browser-side detection.
    timers    - schema + api + detection + KpiIngestion and ApplicationHealthMonitor timers fire on their own.
    full      - every stage (schema, api, detection, portal, timers).

  Uses a machine-wide lock because the stack binds fixed ports. Rebuilds the stack from the current worktree.
  Exit 0 = all passed, 1 = a check failed, 2 = environment problem (docker down, lock timeout).
#>
[CmdletBinding()]
param(
    [ValidateSet('schema', 'api', 'detection', 'portal', 'timers', 'full', 'daemons', 'daemons-web', 'agnostic', 'scout-shadow', 'demo')][string]$Level = 'schema',
    [switch]$NoBuild,
    [int]$LockTimeoutMinutes = 45
)

$ErrorActionPreference = 'Continue'
Set-StrictMode -Version 2.0

$root = (& git rev-parse --show-toplevel).Trim()
$portal = Join-Path $root 'Portal'
$stageMap = @{
    schema    = @('schema')
    api       = @('schema', 'api')
    detection = @('schema', 'api', 'detection')
    portal    = @('schema', 'api', 'detection', 'portal')
    timers    = @('schema', 'api', 'detection', 'timers')
    full      = @('schema', 'api', 'detection', 'portal', 'timers')
    daemons   = @('schema', 'api', 'detection', 'daemons')
    'daemons-web' = @('schema', 'api', 'detection', 'portal', 'daemons', 'daemons-web')
    agnostic  = @('schema', 'api', 'detection', 'agnostic')
    'scout-shadow' = @('schema', 'api', 'detection', 'daemons', 'scout-shadow')
    demo      = @('schema', 'demo')
}
$stages = $stageMap[$Level]

$functionsUrl = 'http://localhost:8081'
$webUrl = 'http://localhost:5080'
$saPassword = $env:MSSQL_SA_PASSWORD; if (-not $saPassword) { $saPassword = 'Jm_LocalDev_1!' }
$internalSecret = $env:Monitoring__InternalApiSecret; if (-not $internalSecret) { $internalSecret = 'Jm_LocalDev_InternalApiSecret_1!' }
$settingsSecret = $env:Monitoring__SettingsWriteSecret; if (-not $settingsSecret) { $settingsSecret = 'Jm_LocalDev_SettingsSecret_1!' }

$failures = New-Object System.Collections.Generic.List[string]
function Pass([string]$m) { Write-Host "[PASS] $m" -ForegroundColor Green }
function Fail([string]$m) { Write-Host "[FAIL] $m" -ForegroundColor Red; $failures.Add($m) }
function Assert([bool]$cond, [string]$m) { if ($cond) { Pass $m } else { Fail $m } }

function Invoke-Sql([string]$query, [string]$database = 'platformDB') {
    $out = & docker exec jm-sql /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P $saPassword -C -d $database -h -1 -W -b -Q "SET NOCOUNT ON; $query" 2>&1
    if ($LASTEXITCODE -ne 0) { throw "sqlcmd failed: $out" }
    # Leading comma: PowerShell would otherwise unroll 0/1-row results into $null or a bare string.
    return ,@($out | ForEach-Object { "$_".Trim() } | Where-Object { $_ -ne '' })
}

function Invoke-Http([string]$method, [string]$url, [hashtable]$headers = @{}, $body = $null) {
    $params = @{ Method = $method; Uri = $url; Headers = $headers; UseBasicParsing = $true; TimeoutSec = 60 }
    if ($null -ne $body) { $params.Body = ($body | ConvertTo-Json -Depth 8); $params.ContentType = 'application/json' }
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

function Wait-Http([string]$url, [int]$seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        $r = Invoke-Http 'GET' $url
        if ($r.Status -eq 200) { return $true }
        Start-Sleep -Seconds 5
    }
    return $false
}

# ------------------------------------------------------------------ Environment + lock
& docker info *> $null
if ($LASTEXITCODE -ne 0) { Write-Host 'Docker is not running. Start Docker Desktop and retry.' -ForegroundColor Red; exit 2 }

$lockPath = Join-Path ([IO.Path]::GetTempPath()) 'spec1000-e2e.lock'
$lockDeadline = (Get-Date).AddMinutes($LockTimeoutMinutes)
$lock = $null
while (-not $lock) {
    try { $lock = [IO.File]::Open($lockPath, 'OpenOrCreate', 'ReadWrite', 'None') }
    catch {
        if ((Get-Date) -gt $lockDeadline) { Write-Host "Could not acquire $lockPath in $LockTimeoutMinutes min." -ForegroundColor Red; exit 2 }
        Write-Host 'Another E2E run holds the lock, waiting 20s...'; Start-Sleep -Seconds 20
    }
}

$startedAt = (Get-Date).ToUniversalTime()
$e2eKpiId = $null
$restoreInspectorPause = $false
try {
    Push-Location $portal
    $env:HealthMonitoring__Enabled = 'true'
    $env:Triage__Mode = 'Shadow'
    $upArgs = @('compose', 'up', '-d')
    if (-not $NoBuild) { $upArgs += '--build' }
    & docker @upArgs 2>&1 | Select-Object -Last 15 | ForEach-Object { Write-Host $_ }
    Pop-Location

    Assert (Wait-Http "$functionsUrl/api/clubs" 300) 'Functions host answers /api/clubs'

    # -------------------------------------------------------------- schema
    $tables = 'KpiDefinition', 'KpiDefinitionAudit', 'KpiBucket', 'KpiSample', 'KpiDetectionState', 'KpiAnomaly', 'IngestionWatermark', 'TriageInvestigation'
    foreach ($db in 'platformDB', 'platformDBQA') {
        foreach ($t in $tables) {
            $id = Invoke-Sql "SELECT ISNULL(OBJECT_ID(N'monitoring.$t', N'U'), 0)" $db
            Assert ([int]$id[0] -ne 0) "$db.monitoring.$t exists"
        }
    }
    $seedQuery = "SELECT COUNT(*) FROM monitoring.KpiDefinition WHERE UpdatedBy = N'seed' OR Stage = N''"
    $seedBefore = [int](Invoke-Sql $seedQuery)[0]
    Assert ($seedBefore -ge 24) "Seeded KPI definitions present ($seedBefore >= 24)"
    Push-Location $portal
    & docker compose run --rm sql-init 2>&1 | Select-Object -Last 3 | ForEach-Object { Write-Host $_ }
    $rerun = $LASTEXITCODE
    Pop-Location
    Assert ($rerun -eq 0) 'sql-init re-run succeeds (idempotent)'
    $seedAfter = [int](Invoke-Sql $seedQuery)[0]
    Assert ($seedAfter -eq $seedBefore) "Seed re-run does not duplicate rows ($seedAfter)"

    # -------------------------------------------------------------- api
    if ($stages -contains 'api') {
        $signal = @{ detectedAt = (Get-Date).ToUniversalTime().ToString('o'); pillar = 'Application Health'; kpi = 'E2EManual'; detection = 'e2e manual trigger ' + [guid]::NewGuid(); domain = 'hotels'; profileKey = 'arrivia' }
        $r = Invoke-Http 'POST' "$functionsUrl/api/triage/investigations" @{} $signal
        Assert ($r.Status -eq 401) "POST /api/triage/investigations without secret -> 401 (got $($r.Status))"
        $r = Invoke-Http 'POST' "$functionsUrl/api/triage/investigations" @{ 'X-Internal-Api-Secret' = $internalSecret } @{ pillar = 'Application Health' }
        Assert ($r.Status -eq 400) "POST invalid body -> 400 (got $($r.Status))"
        $r1 = Invoke-Http 'POST' "$functionsUrl/api/triage/investigations" @{ 'X-Internal-Api-Secret' = $internalSecret } $signal
        Assert ($r1.Status -eq 202) "POST valid -> 202 (got $($r1.Status))"
        $inv1 = $r1.Body | ConvertFrom-Json
        $r2 = Invoke-Http 'POST' "$functionsUrl/api/triage/investigations" @{ 'X-Internal-Api-Secret' = $internalSecret } $signal
        $inv2 = $r2.Body | ConvertFrom-Json
        Assert ($r2.Status -eq 202 -and $inv2.duplicate -eq $true -and $inv2.investigationId -eq $inv1.investigationId) 'Repeated POST returns the same investigation with duplicate=true'
        $g = Invoke-Http 'GET' "$functionsUrl/api/triage/investigations/$($inv1.investigationId)"
        Assert ($g.Status -eq 200) 'GET /api/triage/investigations/{id} -> 200'
        $g404 = Invoke-Http 'GET' "$functionsUrl/api/triage/investigations/$([guid]::NewGuid())"
        Assert ($g404.Status -eq 404) 'GET unknown investigation -> 404'
        $p = Invoke-Http 'GET' "$functionsUrl/api/health/pillars"
        $pillars = @(); if ($p.Status -eq 200) { $parsedPillars = $p.Body | ConvertFrom-Json; $pillars = @($parsedPillars) }
        Assert ($p.Status -eq 200 -and $pillars.Count -eq 3) 'GET /api/health/pillars -> 3 pillars'
        $k = Invoke-Http 'GET' "$functionsUrl/api/settings/kpis?profileKey=*"
        $kpis = @(); if ($k.Status -eq 200) { $parsedKpis = $k.Body | ConvertFrom-Json; $kpis = @($parsedKpis) }
        Assert ($k.Status -eq 200 -and $kpis.Count -ge 24) 'GET /api/settings/kpis -> >= 24 definitions'
        $a = Invoke-Http 'GET' "$functionsUrl/api/health/anomalies?top=5"
        Assert ($a.Status -eq 200) 'GET /api/health/anomalies -> 200'
    }

    # -------------------------------------------------------------- detection
    if ($stages -contains 'detection') {
        $definition = @{
            kpiDefinitionId = 0; pillar = 'Application'; kpi = 'E2ELatency'; domain = 'hotels'; stage = 'e2e'; sourceKey = 'e2e:none'
            direction = 'Increase'; aggregation = 'P95'; evaluationFrequency = '00:01:00'; bucketSize = '01:00:00'; lookbackWindow = '1.00:00:00'
            sampleSize = 0; minObservationsPerBucket = 1; detectionMethod = 'StaticThreshold'; methodParamsJson = '{"threshold":3000}'
            consecutiveCount = 2; cooldownMinutes = 120; enabled = $true; updatedAt = (Get-Date).ToUniversalTime().ToString('o'); updatedBy = 'e2e'; changedBy = 'e2e'
        }
        $u401 = Invoke-Http 'POST' "$functionsUrl/api/settings/kpis" @{} $definition
        Assert ($u401.Status -eq 401) "POST /api/settings/kpis without secret -> 401 (got $($u401.Status))"
        $existing = Invoke-Sql "SELECT KpiDefinitionId FROM monitoring.KpiDefinition WHERE Kpi = N'E2ELatency' AND Stage = N'e2e'"
        if ($existing.Count -gt 0) { $definition.kpiDefinitionId = [int]$existing[0] }
        $u = Invoke-Http 'POST' "$functionsUrl/api/settings/kpis" @{ 'X-Settings-Write-Secret' = $settingsSecret } $definition
        Assert ($u.Status -eq 200) "POST /api/settings/kpis with secret -> 200 (got $($u.Status): $($u.Body))"
        $e2eKpiId = [int](Invoke-Sql "SELECT KpiDefinitionId FROM monitoring.KpiDefinition WHERE Kpi = N'E2ELatency' AND Stage = N'e2e'")[0]
        $audit = [int](Invoke-Sql "SELECT COUNT(*) FROM monitoring.KpiDefinitionAudit WHERE KpiDefinitionId = $e2eKpiId")[0]
        Assert ($audit -ge 1) 'KPI upsert wrote an audit row'

        $hour = (Get-Date).ToUniversalTime()
        $h0 = New-Object DateTime($hour.Year, $hour.Month, $hour.Day, $hour.Hour, 0, 0, [DateTimeKind]::Utc)
        $fmt = { param($d) $d.ToString('yyyy-MM-ddTHH:mm:ss') + '+00:00' }
        $hm3 = & $fmt $h0.AddHours(-3); $hm2 = & $fmt $h0.AddHours(-2); $hm1 = & $fmt $h0.AddHours(-1); $hnow = & $fmt $h0
        Invoke-Sql @"
DELETE FROM monitoring.KpiAnomaly WHERE KpiDefinitionId = $e2eKpiId;
DELETE FROM monitoring.KpiBucket WHERE KpiDefinitionId = $e2eKpiId;
DELETE FROM monitoring.KpiDetectionState WHERE KpiDefinitionId = $e2eKpiId;
INSERT monitoring.KpiBucket (KpiDefinitionId, BucketStart, TotalCount, SampledCount, P50, P95, Mean, StdDev, Value)
VALUES ($e2eKpiId, '$hm3', 100, 0, 800, 1000, 850, 50, NULL),
       ($e2eKpiId, '$hm2', 100, 0, 2500, 3500, 2600, 300, NULL),
       ($e2eKpiId, '$hm1', 100, 0, 2600, 3600, 2700, 300, NULL);
INSERT monitoring.KpiDetectionState (KpiDefinitionId, ConsecutiveCount, LastBucketEvaluated, CooldownUntil, LastEvaluatedAt)
VALUES ($e2eKpiId, 0, '$hm3', NULL, NULL);
MERGE monitoring.IngestionWatermark AS t USING (SELECT $e2eKpiId AS KpiDefinitionId) AS s ON t.KpiDefinitionId = s.KpiDefinitionId
WHEN MATCHED THEN UPDATE SET Watermark = '$hnow'
WHEN NOT MATCHED THEN INSERT (KpiDefinitionId, Watermark) VALUES ($e2eKpiId, '$hnow');
"@ | Out-Null

        $run = Invoke-Http 'POST' "$functionsUrl/api/monkeys/application-health-monitor/run" @{ 'X-Internal-Api-Secret' = $internalSecret }
        Assert ($run.Status -eq 200) "Run application-health-monitor -> 200 (got $($run.Status): $($run.Body))"
        $anomalies = (Invoke-Sql "SELECT AnomalyId FROM monitoring.KpiAnomaly WHERE KpiDefinitionId = $e2eKpiId")
        Assert ($anomalies.Count -eq 1) "Exactly one KpiAnomaly after two consecutive anomalous buckets (got $($anomalies.Count))"
        $state = Invoke-Sql "SELECT CONCAT(ConsecutiveCount, '|', CASE WHEN CooldownUntil IS NULL THEN 0 ELSE 1 END) FROM monitoring.KpiDetectionState WHERE KpiDefinitionId = $e2eKpiId"
        Assert ($state.Count -eq 1 -and $state[0] -eq '2|1') "Detection state = counter 2 with active cooldown (got $state)"
        if ($anomalies.Count -ge 1) {
            $anomalyId = $anomalies[0]
            $inv = (Invoke-Sql "SELECT CONVERT(nvarchar(36), InvestigationId) FROM monitoring.TriageInvestigation WHERE AnomalyId = $anomalyId")
            Assert ($inv.Count -eq 1) 'One TriageInvestigation linked to the anomaly'
            $linked = (Invoke-Sql "SELECT COUNT(*) FROM monitoring.KpiAnomaly WHERE AnomalyId = $anomalyId AND InvestigationId IS NOT NULL")
            Assert ([int]$linked[0] -eq 1) 'KpiAnomaly.InvestigationId attached'
            $queued = (Invoke-Sql "SELECT COUNT(*) FROM monitoring.AgentWorkQueue WHERE PayloadType = N'triage-signal' AND PayloadId = N'$($inv[0])'")
            Assert ([int]$queued[0] -eq 1) 'TriageSignal enqueued in AgentWorkQueue'

            $rerun2 = Invoke-Http 'POST' "$functionsUrl/api/monkeys/application-health-monitor/run" @{ 'X-Internal-Api-Secret' = $internalSecret }
            $after = [int](Invoke-Sql "SELECT COUNT(*) FROM monitoring.KpiAnomaly WHERE KpiDefinitionId = $e2eKpiId")[0]
            Assert ($rerun2.Status -eq 200 -and $after -eq 1) 'Second monitor run creates no duplicate anomaly'

            # The local DB may have the Inspector paused from the portal; unpause it only for this check and restore it in finally.
            $inspectorPause = (Invoke-Sql "SELECT CONVERT(int, IsPaused) FROM monitoring.AgentPauseSetting WHERE MonkeyKey = N'inspector-monkey'")
            if ($inspectorPause.Count -gt 0 -and $inspectorPause[0] -eq '1') {
                $restoreInspectorPause = $true
                Invoke-Sql "UPDATE monitoring.AgentPauseSetting SET IsPaused = 0 WHERE MonkeyKey = N'inspector-monkey'" | Out-Null
            }
            # Each Inspector run handles one TriageSignal, and older signals may be queued ahead of ours: run until ours is done.
            $deadline = (Get-Date).AddSeconds(240); $status = 'Queued'; $insOk = $true
            while ((Get-Date) -lt $deadline -and $status -in @('Queued', 'Running')) {
                $ins = Invoke-Http 'POST' "$functionsUrl/api/monkeys/inspector-monkey/run" @{ 'X-Internal-Api-Secret' = $internalSecret }
                if ($ins.Status -ne 200) { $insOk = $false }
                $status = (Invoke-Sql "SELECT Status FROM monitoring.TriageInvestigation WHERE InvestigationId = '$($inv[0])'")[0]
                if ($status -in @('Queued', 'Running')) { Start-Sleep -Seconds 5 }
            }
            Assert $insOk 'Run inspector-monkey -> 200'
            Assert ($status -in @('Completed', 'Failed')) "Inspector processed the TriageSignal (status $status)"
            if ($status -eq 'Completed') {
                $json = (Invoke-Sql "SELECT CASE WHEN JSON_VALUE(ResultJson, '$.assessment.alertLevel') IN ('red_alert','amber','informational') AND JSON_QUERY(ResultJson, '$.correlation') IS NOT NULL AND JSON_VALUE(ResultJson, '$.trigger.kpi') = 'E2ELatency' THEN 1 ELSE 0 END FROM monitoring.TriageInvestigation WHERE InvestigationId = '$($inv[0])'")[0]
                Assert ($json -eq '1') 'ResultJson follows the TriageResult contract'
            }
            $sinceUtc = $startedAt.ToString('yyyy-MM-ddTHH:mm:ss') + '+00:00'
            # Only notifications caused by this investigation count; other monitors (e.g. SLO burn rate) may legitimately e-mail during the run.
            $emails = [int](Invoke-Sql "SELECT COUNT(*) FROM monitoring.NotificationLog WHERE Channel = N'email' AND SentAt >= '$sinceUtc' AND SourceId = '$($inv[0])'")[0]
            $notified = (Invoke-Sql "SELECT CONVERT(int, Notified) FROM monitoring.TriageInvestigation WHERE InvestigationId = '$($inv[0])'")[0]
            Assert ($notified -eq '0') 'Shadow mode left the investigation un-notified'
            Assert ($emails -eq 0) 'Shadow mode sent no e-mail'
        }
    }

    # -------------------------------------------------------------- daemons (activity log + status API; runs after detection)
    if ($stages -contains 'daemons') {
        $tbl = (Invoke-Sql "SELECT ISNULL(OBJECT_ID(N'monitoring.DaemonActivity', N'U'), 0)")[0]
        Assert ([int]$tbl -ne 0) 'monitoring.DaemonActivity exists'
        $ds = Invoke-Http 'GET' "$functionsUrl/api/daemons"
        $statuses = @(); if ($ds.Status -eq 200) { $parsedDs = $ds.Body | ConvertFrom-Json; $statuses = @($parsedDs) }
        $coreDaemons = @('kpi-ingestion', 'application-health-monitor', 'business-health-monitor', 'infrastructure-health-monitor', 'triage-monkey')
    $keys = @($statuses | ForEach-Object { $_.daemonKey })
    $missing = @($coreDaemons | Where-Object { $keys -notcontains $_ })
    $unexpected = @($keys | Where-Object { $coreDaemons -notcontains $_ -and $_ -ne 'token-refresh' })
    Assert ($ds.Status -eq 200 -and $missing.Count -eq 0 -and $unexpected.Count -eq 0) "GET /api/daemons -> the 5 Spec 1000 daemons (+ token-refresh when MT1 is present) (got $($keys -join ','))"
        $app = @($statuses | Where-Object { $_.daemonKey -eq 'application-health-monitor' })
        Assert ($app.Count -eq 1 -and $app[0].enabled -eq $true -and $app[0].lastRunAt) 'Application Health Monitor status is enabled and has a last run'
        $bad = Invoke-Http 'GET' "$functionsUrl/api/daemons/activity?top=0"
        Assert ($bad.Status -eq 400) "GET /api/daemons/activity?top=0 -> 400 (got $($bad.Status))"
        $act = Invoke-Http 'GET' "$functionsUrl/api/daemons/activity?afterId=0&top=1000"
        $page = $null; if ($act.Status -eq 200) { $page = $act.Body | ConvertFrom-Json }
        $entries = @(); if ($page) { $entries = @($page.entries) }
        $kinds = @($entries | ForEach-Object { "$($_.daemonKey)|$($_.kind)" } | Sort-Object -Unique)
        foreach ($k in 'application-health-monitor|run-started', 'application-health-monitor|run-completed', 'application-health-monitor|kpi-evaluated', 'application-health-monitor|anomaly', 'application-health-monitor|triage-submitted', 'triage-monkey|investigation-started', 'triage-monkey|investigation-completed') {
            Assert ($kinds -contains $k) "Activity contains $k"
        }
        if ($page) {
            $next = Invoke-Http 'GET' "$functionsUrl/api/daemons/activity?afterId=$($page.lastId)&top=50"
            $nextPage = $null; if ($next.Status -eq 200) { $nextPage = $next.Body | ConvertFrom-Json }
            Assert ($next.Status -eq 200 -and @($nextPage.entries | Where-Object { [long]$_.activityId -le [long]$page.lastId }).Count -eq 0) 'Activity paging with afterId returns only newer entries'
        }
    }

    # -------------------------------------------------------------- daemons-web (portal tab + proxies)
    if ($stages -contains 'daemons-web') {
        $wd = Invoke-Http 'GET' "$webUrl/api/daemons"
        Assert ($wd.Status -eq 200) 'Web proxy /api/daemons -> 200'
        $wa = Invoke-Http 'GET' "$webUrl/api/daemons/activity?afterId=0&top=20"
        Assert ($wa.Status -eq 200) 'Web proxy /api/daemons/activity -> 200'
        $html = Invoke-Http 'GET' "$webUrl/"
        Assert ($html.Status -eq 200 -and $html.Body -match 'tab-btn-daemons' -and $html.Body -match 'tab-daemons') 'Portal has the Daemons tab'
    }
    # -------------------------------------------------------------- portal
    if ($stages -contains 'portal') {
        Assert (Wait-Http $webUrl 120) 'Web answers /'
        $wp = Invoke-Http 'GET' "$webUrl/api/health/pillars"
        Assert ($wp.Status -eq 200) 'Web proxy /api/health/pillars -> 200'
        $wi = Invoke-Http 'GET' "$webUrl/api/triage/investigations?top=5"
        Assert ($wi.Status -eq 200) 'Web proxy /api/triage/investigations -> 200'
        $wk = Invoke-Http 'GET' "$webUrl/api/settings/kpis"
        Assert ($wk.Status -eq 200) 'Web proxy /api/settings/kpis -> 200'
        $js = Invoke-Http 'GET' "$webUrl/app.js"
        Assert ($js.Status -eq 200 -and $js.Body -notmatch 'RW_LATENCY_SAMPLE|getJourneyLatencyMetrics|rwLatencyMetric') 'Served app.js contains no browser-side detection'
    }

    # -------------------------------------------------------------- timers
    if ($stages -contains 'timers') {
        Write-Host 'Waiting up to 150s for the timers to fire on their own...'
        $deadline = (Get-Date).AddSeconds(150); $ingest = $false; $monitor = $false
        while ((Get-Date) -lt $deadline -and -not ($ingest -and $monitor)) {
            $logs = & docker logs jm-functions --since $startedAt.ToString('yyyy-MM-ddTHH:mm:ssZ') 2>&1 | Out-String
            $ingest = $logs -match "Executed 'Functions\.KpiIngestion'"
            $monitor = $logs -match "Executed 'Functions\.ApplicationHealthMonitor'"
            if (-not ($ingest -and $monitor)) { Start-Sleep -Seconds 10 }
        }
        Assert $ingest 'KpiIngestion timer fired without the portal'
        Assert $monitor 'ApplicationHealthMonitor timer fired without the portal'
    }
}
catch {
    Fail ("Unexpected error: " + $_.Exception.Message)
}
finally {
    if ($restoreInspectorPause) {
        try { Invoke-Sql "UPDATE monitoring.AgentPauseSetting SET IsPaused = 1 WHERE MonkeyKey = N'inspector-monkey'" | Out-Null }
        catch { Write-Host "Could not restore inspector-monkey pause: $($_.Exception.Message)" -ForegroundColor Yellow }
    }
    if ($e2eKpiId) {
        try {
            Invoke-Sql "UPDATE monitoring.KpiDefinition SET Enabled = 0 WHERE KpiDefinitionId = $e2eKpiId; DELETE FROM monitoring.KpiBucket WHERE KpiDefinitionId = $e2eKpiId;" | Out-Null
        } catch { Write-Host "Cleanup warning: $($_.Exception.Message)" -ForegroundColor Yellow }
    }
    Remove-Item Env:\HealthMonitoring__Enabled -ErrorAction SilentlyContinue
    Remove-Item Env:\Triage__Mode -ErrorAction SilentlyContinue
    if ($lock) { $lock.Dispose() }
}


# -------------------------------------------------------------- task-owned stage scripts (agnostic, scout-shadow, demo)
# Stages owned by implementation tasks live outside .github (which tasks may not edit):
# Portal/tests/e2e/<stage>.ps1 must exit 0 on success and print [PASS]/[FAIL] lines.
# The demo stage targets the isolated demo stack (functions-demo :8083, web-demo :5090).
foreach ($ownedStage in @('agnostic', 'scout-shadow', 'demo')) {
    if ($stages -notcontains $ownedStage) { continue }
    $stageScript = Join-Path $portal "tests/e2e/$ownedStage.ps1"
    if (-not (Test-Path $stageScript)) { $failures.Add("Stage script missing: Portal/tests/e2e/$ownedStage.ps1"); Write-Host "[FAIL] Stage script missing: Portal/tests/e2e/$ownedStage.ps1" -ForegroundColor Red; continue }
    $stageFunctionsUrl = $functionsUrl; $stageWebUrl = 'http://localhost:5080'
    if ($ownedStage -eq 'demo') { $stageFunctionsUrl = 'http://localhost:8083'; $stageWebUrl = 'http://localhost:5090' }
    & powershell -NoProfile -ExecutionPolicy Bypass -File $stageScript -FunctionsUrl $stageFunctionsUrl -WebUrl $stageWebUrl 2>&1 | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0) { $failures.Add("Stage $ownedStage failed (exit $LASTEXITCODE)") } else { Write-Host "[PASS] Stage $ownedStage" -ForegroundColor Green }
}

Write-Host ''
if ($failures.Count -gt 0) {
    Write-Host ("E2E {0}: {1} check(s) failed." -f $Level, $failures.Count) -ForegroundColor Red
    exit 1
}
Write-Host "E2E $Level passed." -ForegroundColor Green
exit 0
