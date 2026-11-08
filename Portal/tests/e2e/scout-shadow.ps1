<#
.SYNOPSIS
    Spec 1000 SD5 E2E stage "scout-shadow": monitoring.ScoutShadowComparison and GET /api/scouts/{board}/shadow.

.DESCRIPTION
    Checks, against the running docker stack:
      1. the shadow table and its unique key (Board, ProfileKey, EntityKey, Stage, BucketStart) exist;
      2. GET /api/scouts/{latency|exception}/shadow answers 200 with the counts shape and a rows array, and
         rejects an unknown board / an out-of-range hours with 400;
      3. a synthetic comparison written through monitoring.usp_AddScoutShadowComparisons (the procedure the
         Scout cycle uses) is upserted on its key and read back by the API, then removed.
    A full Scout cycle is NOT triggered: it reads the live board and escalates real investigation packages,
    which this stage must never cause. The cycle-side behaviour is covered by ScoutShadowComparatorTests.
    Seeded rows use a synthetic ProfileKey so no real profile's counts move; they are deleted in all cases.

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
$seedProfile = 'e2e-sd5-shadow'
$seedEntity = 'e2e-sd5-entity'

$failures = New-Object System.Collections.Generic.List[string]
function Pass([string]$m) { Write-Host "[PASS] $m" -ForegroundColor Green }
function Fail([string]$m) { Write-Host "[FAIL] $m" -ForegroundColor Red; $failures.Add($m) }
function Info([string]$m) { Write-Host "[INFO] $m" }
function Assert([bool]$cond, [string]$m) { if ($cond) { Pass $m } else { Fail $m } }

function Invoke-Sql([string]$query, [string]$database = 'platformDB') {
    $out = & docker exec jm-sql /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P $saPassword -C -d $database -h -1 -W -b -Q "SET NOCOUNT ON; $query" 2>&1
    if ($LASTEXITCODE -ne 0) { throw "sqlcmd failed: $out" }
    return ,@($out | ForEach-Object { "$_".Trim() } | Where-Object { $_ -ne '' })
}

function Invoke-Http([string]$url) {
    try {
        $r = Invoke-WebRequest -Method Get -Uri $url -UseBasicParsing -TimeoutSec 60
        return [pscustomobject]@{ Status = [int]$r.StatusCode; Body = $r.Content }
    } catch [System.Net.WebException] {
        $resp = $_.Exception.Response
        if ($null -eq $resp) { return [pscustomobject]@{ Status = 0; Body = $_.Exception.Message } }
        $reader = New-Object System.IO.StreamReader($resp.GetResponseStream())
        return [pscustomobject]@{ Status = [int]$resp.StatusCode; Body = $reader.ReadToEnd() }
    }
}

function Has([object]$obj, [string]$name) { return $null -ne $obj -and ($obj.PSObject.Properties.Name -contains $name) }

function Test-ShadowShape([object]$json, [string]$board) {
    if (-not (Has $json 'counts') -or -not (Has $json 'rows')) { return $false }
    $c = $json.counts
    foreach ($n in @('both', 'rulesOnly', 'methodOnly', 'total')) {
        if (-not (Has $c $n) -or -not ($c.$n -is [int] -or $c.$n -is [long])) { return $false }
    }
    if ([long]$c.total -ne ([long]$c.both + [long]$c.rulesOnly + [long]$c.methodOnly)) { return $false }
    return ($json.board -eq $board) -and ($json.rows -is [System.Array] -or $null -eq $json.rows -or @($json.rows).Count -ge 0)
}

Write-Host "== scout-shadow: $FunctionsUrl"

# ------------------------------------------------------------------ 1. table
try {
    $table = Invoke-Sql "SELECT CASE WHEN OBJECT_ID(N'monitoring.ScoutShadowComparison', N'U') IS NULL THEN 0 ELSE 1 END;"
    Assert ($table[0] -eq '1') 'monitoring.ScoutShadowComparison exists'
    $key = Invoke-Sql "SELECT COUNT(*) FROM sys.indexes WHERE object_id = OBJECT_ID(N'monitoring.ScoutShadowComparison') AND name = N'UQ_monitoring_ScoutShadowComparison_Key' AND is_unique = 1;"
    Assert ($key[0] -eq '1') 'unique key (Board, ProfileKey, EntityKey, Stage, BucketStart) exists'
    $procs = Invoke-Sql "SELECT COUNT(*) FROM sys.procedures WHERE SCHEMA_NAME(schema_id) = N'monitoring' AND name IN (N'usp_AddScoutShadowComparisons', N'usp_GetScoutShadowComparisons', N'usp_GetScoutShadowCounts');"
    Assert ($procs[0] -eq '3') 'shadow procedures exist'
} catch {
    Fail "shadow schema check failed: $($_.Exception.Message)"
}

# ------------------------------------------------------------------ 2. API contract, both boards
foreach ($board in @('latency', 'exception')) {
    $r = Invoke-Http "$FunctionsUrl/api/scouts/$board/shadow?hours=24"
    if ($r.Status -ne 200) { Fail "GET /api/scouts/$board/shadow -> $($r.Status) $($r.Body)"; continue }
    Pass "GET /api/scouts/$board/shadow -> 200"
    try {
        $json = $r.Body | ConvertFrom-Json
        Assert (Test-ShadowShape $json $board) "$board shadow has counts {both, rulesOnly, methodOnly, total} and rows"
        Info ("{0}: both={1} rules-only={2} method-only={3} (profile {4})" -f $board, $json.counts.both, $json.counts.rulesOnly, $json.counts.methodOnly, $json.profileKey)
    } catch {
        Fail "$board shadow body is not JSON: $($r.Body)"
    }
}

$bad = Invoke-Http "$FunctionsUrl/api/scouts/not-a-board/shadow"
Assert ($bad.Status -eq 400) "unknown board -> 400 (got $($bad.Status))"
$badHours = Invoke-Http "$FunctionsUrl/api/scouts/latency/shadow?hours=0"
Assert ($badHours.Status -eq 400) "hours=0 -> 400 (got $($badHours.Status))"

# ------------------------------------------------------------------ 3. write path round trip (synthetic, cleaned up)
$bucket = (Get-Date).ToUniversalTime().AddMinutes(-30).ToString('yyyy-MM-ddTHH:mm:00') + '+00:00'
$hourBucket = (Get-Date).ToUniversalTime().AddMinutes(-30).ToString('yyyy-MM-ddTHH:00:00') + '+00:00'
function Write-SeedRows([string]$runId, [string]$agreement) {
    $sql = @"
DECLARE @j nvarchar(max) = (
    SELECT v.runId, v.board, v.profileKey, v.entityKey, v.stage, v.bucketStart, v.ruleRank, v.methodVerdict, v.kpiAnomalyId, v.agreement
    FROM (VALUES
        (CAST(N'$runId' AS uniqueidentifier), N'latency', N'$seedProfile', N'$seedEntity', N'e2e-stage', CAST(N'$bucket' AS datetimeoffset), 3,
         CASE WHEN N'$agreement' = N'both' THEN N'anomalous' ELSE N'not-evaluated' END,
         CASE WHEN N'$agreement' = N'both' THEN CAST(-1 AS bigint) ELSE NULL END, N'$agreement'),
        (CAST(N'$runId' AS uniqueidentifier), N'latency', N'$seedProfile', N'$seedEntity-2', N'', CAST(N'$hourBucket' AS datetimeoffset), NULL,
         N'anomalous', CAST(-2 AS bigint), N'method-only')
    ) AS v(runId, board, profileKey, entityKey, stage, bucketStart, ruleRank, methodVerdict, kpiAnomalyId, agreement)
    FOR JSON PATH);
EXEC monitoring.usp_AddScoutShadowComparisons @RowsJson = @j;
"@
    Invoke-Sql ($sql -replace "`r?`n", ' ') | Out-Null
}

try {
    Invoke-Sql "DELETE FROM monitoring.ScoutShadowComparison WHERE ProfileKey = N'$seedProfile';" | Out-Null
    Write-SeedRows ([guid]::NewGuid().ToString()) 'rules-only'
    # Same keys again from a later cycle: the rules case is now matched by the method -> one row, updated.
    Write-SeedRows ([guid]::NewGuid().ToString()) 'both'
    $stored = Invoke-Sql "SELECT COUNT(*) FROM monitoring.ScoutShadowComparison WHERE ProfileKey = N'$seedProfile';"
    Assert ($stored[0] -eq '2') "a later cycle upserts on (Board, ProfileKey, EntityKey, Stage, BucketStart) (rows: $($stored[0]))"

    $r = Invoke-Http "$FunctionsUrl/api/scouts/latency/shadow?hours=2&profileKey=*&top=500"
    if ($r.Status -ne 200) {
        Fail "GET /api/scouts/latency/shadow?profileKey=* -> $($r.Status) $($r.Body)"
    } else {
        $json = $r.Body | ConvertFrom-Json
        $mine = @($json.rows | Where-Object { $_.profileKey -eq $seedProfile })
        $both = @($mine | Where-Object { $_.entityKey -eq $seedEntity })
        $methodOnly = @($mine | Where-Object { $_.entityKey -eq "$seedEntity-2" })
        Assert ($both.Count -eq 1 -and $both[0].agreement -eq 'both' -and $both[0].methodVerdict -eq 'anomalous' -and $both[0].ruleRank -eq 3 -and $both[0].stage -eq 'e2e-stage') 'API returns the seeded "both" row with its rank, verdict and stage'
        Assert ($methodOnly.Count -eq 1 -and $methodOnly[0].agreement -eq 'method-only' -and $null -eq $methodOnly[0].ruleRank -and $methodOnly[0].stage -eq '') 'API returns the seeded "method-only" row (no rule rank, all-stages definition)'
        Assert ([long]$json.counts.both -ge 1 -and [long]$json.counts.methodOnly -ge 1) 'counts include the seeded rows'
        Assert ($json.profileKey -eq '*') 'profileKey=* reads every profile'
    }

    $active = Invoke-Http "$FunctionsUrl/api/scouts/latency/shadow?hours=2&top=500"
    if ($active.Status -eq 200) {
        $activeJson = $active.Body | ConvertFrom-Json
        Assert (@($activeJson.rows | Where-Object { $_.profileKey -eq $seedProfile }).Count -eq 0) "default profileKey is the active profile ($($activeJson.profileKey)), not the synthetic one"
    } else {
        Fail "GET /api/scouts/latency/shadow (active profile) -> $($active.Status)"
    }
} catch {
    Fail "shadow round trip failed: $($_.Exception.Message)"
} finally {
    try {
        Invoke-Sql "DELETE FROM monitoring.ScoutShadowComparison WHERE ProfileKey = N'$seedProfile';" | Out-Null
        $left = Invoke-Sql "SELECT COUNT(*) FROM monitoring.ScoutShadowComparison WHERE ProfileKey = N'$seedProfile';"
        Assert ($left[0] -eq '0') 'synthetic shadow rows cleaned up'
    } catch {
        Fail "cleanup failed: $($_.Exception.Message)"
    }
}

Info 'Scout cycle not triggered: it reads the live board and would escalate real packages (cycle covered by unit tests).'

Write-Host ''
if ($failures.Count -gt 0) {
    Write-Host ("scout-shadow: {0} check(s) failed." -f $failures.Count) -ForegroundColor Red
    exit 1
}
Write-Host 'scout-shadow passed.' -ForegroundColor Green
exit 0
