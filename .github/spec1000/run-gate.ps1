<#
.SYNOPSIS
  Spec 1000 deterministic quality gate.

.DESCRIPTION
  Mode Check    - run by the implementer (self-check). Writes .spec1000/checks/<Task>.json. Never runs E2E unless -IncludeE2E.
  Mode Verdict  - run ONLY by spec1000-tester. Re-runs every deterministic check, always runs E2E for the task level,
                  records the tester's judgement (-Acceptance/-Review) and writes .spec1000/verdicts/<Task>.json.
                  After maxIterations FAIL verdicts the status becomes ESCALATED.
  Mode Baseline - run ONLY by the orchestrator on the integration branch before Fase 0. Writes .github/spec1000/baseline.json.

  Exit code 0 = PASS, 1 = FAIL/ESCALATED, 2 = usage error.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Task,
    [ValidateSet('Check', 'Verdict', 'Baseline')][string]$Mode = 'Check',
    [ValidateSet('PASS', 'FAIL', '')][string]$Acceptance = '',
    [ValidateSet('PASS', 'FAIL', '')][string]$Review = '',
    [string]$FindingsFile = '',
    [string]$BaseRef = '',
    [switch]$IncludeE2E
)

$ErrorActionPreference = 'Continue'
Set-StrictMode -Version 2.0

function Get-RepoRoot { (& git rev-parse --show-toplevel).Trim() }
$root = Get-RepoRoot
Set-Location $root

$manifest = Get-Content (Join-Path $root '.github/spec1000/tasks.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$taskDef = $manifest.tasks | Where-Object { $_.id -eq $Task }
if (-not $taskDef) { Write-Host "Unknown task '$Task'." -ForegroundColor Red; exit 2 }

$stateDir = Join-Path $root '.spec1000'
New-Item -ItemType Directory -Force -Path (Join-Path $stateDir 'checks'), (Join-Path $stateDir 'verdicts'), (Join-Path $stateDir 'logs') | Out-Null

$results = New-Object System.Collections.Generic.List[object]
function Add-Result([string]$name, [string]$status, [string]$details) {
    $results.Add([pscustomobject]@{ check = $name; status = $status; details = $details })
    $color = 'Green'; if ($status -eq 'FAIL') { $color = 'Red' } elseif ($status -eq 'SKIP') { $color = 'DarkGray' }
    Write-Host ("[{0}] {1} - {2}" -f $status, $name, $details) -ForegroundColor $color
}

function Convert-GlobToRegex([string]$glob) {
    $escaped = [regex]::Escape($glob.Replace('\', '/'))
    $escaped = $escaped.Replace('\*\*/', '(.*/)?').Replace('\*\*', '.*').Replace('\*', '[^/]*').Replace('\?', '[^/]')
    return '^' + $escaped + '$'
}
function Test-Glob([string]$path, [object[]]$globs) {
    foreach ($g in $globs) { if ($path -match (Convert-GlobToRegex $g)) { return $true } }
    return $false
}

function Invoke-Logged([string]$name, [string]$file, [string[]]$arguments) {
    $log = Join-Path $stateDir ("logs/{0}-{1}.log" -f $Task, $name)
    $output = & $file @arguments 2>&1 | ForEach-Object { "$_" }
    $code = $LASTEXITCODE
    $output | Set-Content -Path $log -Encoding UTF8
    return [pscustomobject]@{ ExitCode = $code; Output = $output; Log = $log }
}

function Invoke-BuildAndTest {
    $build = Invoke-Logged 'build' 'dotnet' @('build', $manifest.solution, '-nologo', '-clp:NoSummary')
    $warnings = @($build.Output | Where-Object { $_ -match 'warning CS\d+' } | ForEach-Object { ($_ -replace '\s*\[[^\]]+\]\s*$', '').Trim() } | Sort-Object -Unique)
    $test = $null
    if ($build.ExitCode -eq 0) { $test = Invoke-Logged 'test' 'dotnet' @('test', $manifest.solution, '--no-build', '-nologo') }
    $total = 0; $failed = 0
    if ($test) {
        foreach ($line in $test.Output) {
            if ($line -match 'Failed:\s*(\d+),\s*Passed:\s*(\d+),\s*Skipped:\s*(\d+),\s*Total:\s*(\d+)') {
                $failed += [int]$Matches[1]; $total += [int]$Matches[4]
            }
        }
    }
    return [pscustomobject]@{ Build = $build; Warnings = $warnings; Test = $test; Total = $total; Failed = $failed }
}

# ---------------------------------------------------------------- Baseline
if ($Mode -eq 'Baseline') {
    $bt = Invoke-BuildAndTest
    if ($bt.Build.ExitCode -ne 0 -or -not $bt.Test -or $bt.Test.ExitCode -ne 0) {
        Write-Host "Baseline requires a green build and test run on the integration branch. See $($bt.Build.Log)" -ForegroundColor Red; exit 1
    }
    $baseline = [ordered]@{ sha = (& git rev-parse HEAD).Trim(); createdAt = (Get-Date).ToUniversalTime().ToString('o'); csWarnings = $bt.Warnings.Count; tests = $bt.Total }
    $baseline | ConvertTo-Json | Set-Content -Path (Join-Path $root '.github/spec1000/baseline.json') -Encoding UTF8
    Write-Host "Baseline written: $($bt.Warnings.Count) CS warnings, $($bt.Total) tests." -ForegroundColor Green
    exit 0
}

if ($Mode -eq 'Verdict' -and ([string]::IsNullOrEmpty($Acceptance) -or [string]::IsNullOrEmpty($Review))) {
    Write-Host 'Verdict mode requires -Acceptance PASS|FAIL and -Review PASS|FAIL.' -ForegroundColor Red; exit 2
}

$headSha = (& git rev-parse HEAD).Trim()

# ---------------------------------------------------------------- Base ref
if ([string]::IsNullOrEmpty($BaseRef)) {
    $currentTaskFile = Join-Path $stateDir 'current-task.json'
    if (Test-Path $currentTaskFile) {
        $current = Get-Content $currentTaskFile -Raw | ConvertFrom-Json
        if ($current.taskId -eq $Task) { $BaseRef = $current.baseSha }
    }
    if ([string]::IsNullOrEmpty($BaseRef)) { $BaseRef = (& git merge-base HEAD $manifest.integrationBranch).Trim() }
}

# ---------------------------------------------------------------- 1. Clean tree
$dirty = @(& git status --porcelain | Where-Object { $_ -notmatch '\.spec1000/' })
if ($dirty.Count -gt 0) { Add-Result 'clean-tree' 'FAIL' ("Uncommitted changes: " + (($dirty | Select-Object -First 10) -join '; ') + '. Commit before running the gate.') }
else { Add-Result 'clean-tree' 'PASS' 'Working tree clean.' }

$changed = @(& git diff --name-only "$BaseRef...HEAD" | Where-Object { $_ })
Add-Result 'diff' 'PASS' ("{0} files changed since {1}." -f $changed.Count, $BaseRef.Substring(0, [Math]::Min(10, $BaseRef.Length)))

# ---------------------------------------------------------------- 2. Ownership
$outside = @($changed | Where-Object { (Test-Glob $_ $manifest.alwaysForbidden) -or -not (Test-Glob $_ $taskDef.allowedPaths) })
if ($outside.Count -gt 0) { Add-Result 'ownership' 'FAIL' ("Files outside task ownership: " + ($outside -join ', ')) }
else { Add-Result 'ownership' 'PASS' 'Every changed file is owned by this task.' }

# ---------------------------------------------------------------- 3. Contracts frozen
if ($taskDef.contractsFrozen) {
    $tag = $manifest.contractsTag
    & git rev-parse -q --verify "refs/tags/$tag" | Out-Null
    if ($LASTEXITCODE -ne 0) { Add-Result 'contracts' 'FAIL' "Tag $tag not found. Fase 0 must be approved and tagged first." }
    else {
        $contractDiff = @(& git diff --name-only $tag HEAD -- $manifest.contractFiles | Where-Object { $_ })
        if ($contractDiff.Count -gt 0) { Add-Result 'contracts' 'FAIL' ("Frozen contract files changed: " + ($contractDiff -join ', ')) }
        else { Add-Result 'contracts' 'PASS' "Contract files identical to $tag." }
    }
} else { Add-Result 'contracts' 'SKIP' 'This task defines the contracts.' }

# ---------------------------------------------------------------- 4. Layers
$layerErrors = New-Object System.Collections.Generic.List[string]
foreach ($file in $changed) {
    if (-not (Test-Path $file)) { continue }
    if ($file -like 'Portal/src/JourneyMonitor.Core/*' -and $file -like '*.cs') {
        $bad = Select-String -Path $file -Pattern '^\s*using\s+(Microsoft\.Azure\.Functions|Microsoft\.AspNetCore|Dapper|Microsoft\.Data\.SqlClient|Azure\.)' -List
        if ($bad) { $layerErrors.Add("$file references an infrastructure/host namespace (Core must stay pure).") }
    }
    if ($file -like 'Portal/src/JourneyMonitor.Core/*.csproj') { $layerErrors.Add("${file}: Core project file must not change.") }
    # Functions and Web are checked on ADDED lines only: pre-existing code (e.g. the /api/settings/kql proxy routes) must not fail a task.
    if ($file -like 'Portal/src/JourneyMonitor.Functions/Functions/*.cs') {
        $added = @(& git diff -U0 "$BaseRef...HEAD" -- $file | Where-Object { $_ -like '+*' -and $_ -notlike '+++*' })
        if (@($added | Where-Object { $_ -match '\bDapper\b|SqlConnection|\.QueryAsync<|ITelemetrySource' }).Count -gt 0) { $layerErrors.Add("${file}: Functions must only validate, dispatch and map (no SQL/KQL).") }
    }
    if ($file -like 'Portal/src/JourneyMonitor.Web/*.cs') {
        $added = @(& git diff -U0 "$BaseRef...HEAD" -- $file | Where-Object { $_ -like '+*' -and $_ -notlike '+++*' })
        if (@($added | Where-Object { $_ -match '\bDapper\b|SqlConnection|ITelemetrySource|IKqlQueryTextProvider|\.QueryAsync<' }).Count -gt 0) { $layerErrors.Add("${file}: Web must not query SQL or telemetry.") }
    }
}
if ($layerErrors.Count -gt 0) { Add-Result 'layers' 'FAIL' ($layerErrors -join ' | ') } else { Add-Result 'layers' 'PASS' 'Layer rules respected.' }

# ---------------------------------------------------------------- 5. Secrets in added lines
$secretPattern = 'gho_[A-Za-z0-9]{20,}|ghp_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|AccountKey=[A-Za-z0-9+/=]{20,}|SharedAccessKey=[A-Za-z0-9+/=]{20,}|-----BEGIN [A-Z ]*PRIVATE KEY|xox[baprs]-[A-Za-z0-9-]{10,}'
$addedLines = @(& git diff -U0 "$BaseRef...HEAD" | Where-Object { $_ -like '+*' -and $_ -notlike '+++*' })
$leaks = @($addedLines | Where-Object { $_ -match $secretPattern })
if ($leaks.Count -gt 0) { Add-Result 'secrets' 'FAIL' ("Possible secret in {0} added line(s)." -f $leaks.Count) } else { Add-Result 'secrets' 'PASS' 'No secrets in added lines.' }

# ---------------------------------------------------------------- 6. SQL idempotency + registration
$sqlErrors = New-Object System.Collections.Generic.List[string]
$initSql = Get-Content 'Portal/docker/init-sql.sh' -Raw
foreach ($file in ($changed | Where-Object { $_ -like 'Portal/src/JourneyMonitor.Infrastructure/Sql/*.sql' -and (Test-Path $_) })) {
    $text = Get-Content $file -Raw
    $rel = $file.Substring('Portal/src/JourneyMonitor.Infrastructure/Sql/'.Length)
    if ($initSql -notmatch [regex]::Escape($rel)) { $sqlErrors.Add("$rel is not registered in Portal/docker/init-sql.sh.") }
    $creates = ([regex]::Matches($text, '(?im)^\s*CREATE\s+TABLE')).Count
    $guards = ([regex]::Matches($text, "(?i)IF\s+OBJECT_ID\s*\([^)]*N?'U'\s*\)\s*IS\s+NULL")).Count
    if ($creates -gt $guards) { $sqlErrors.Add("$rel has CREATE TABLE without IF OBJECT_ID(...,'U') IS NULL guard.") }
    if ($text -match '(?im)^\s*CREATE\s+PROCEDURE') { $sqlErrors.Add("$rel uses CREATE PROCEDURE; use CREATE OR ALTER PROCEDURE.") }
}
if ($sqlErrors.Count -gt 0) { Add-Result 'sql' 'FAIL' ($sqlErrors -join ' | ') } else { Add-Result 'sql' 'PASS' 'SQL scripts idempotent and registered.' }

# ---------------------------------------------------------------- 7. Required tests exist
$testErrors = New-Object System.Collections.Generic.List[string]
foreach ($tf in @($taskDef.requiredTestFiles)) { if ($tf -and -not (Test-Path $tf)) { $testErrors.Add("Missing test file $tf.") } }
$testRoots = @('Portal/tests') + @(Get-ChildItem 'Portal/demo' -Directory -Filter '*.Tests' -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName })
$testSources = @($testRoots | ForEach-Object { Get-ChildItem $_ -Recurse -Filter *.cs -ErrorAction SilentlyContinue } | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' })
$allTestText = ($testSources | ForEach-Object { Get-Content $_.FullName -Raw }) -join "`n"
foreach ($name in @($taskDef.requiredTestNames)) {
    if (-not $name) { continue }
    $pattern = '\[(Fact|Theory)[^\]]*\](\s*\[[^\]]*\])*\s*public\s+(async\s+)?(Task|void)\s+' + [regex]::Escape($name) + '\s*\('
    if ($allTestText -notmatch $pattern) { $testErrors.Add("Missing [Fact]/[Theory] test '$name'.") }
}
if ($testErrors.Count -gt 0) { Add-Result 'required-tests' 'FAIL' ($testErrors -join ' | ') }
else { Add-Result 'required-tests' 'PASS' ("{0} required test(s) present." -f @($taskDef.requiredTestNames).Count) }

# ---------------------------------------------------------------- 8. Static checks
$static = @()
if ($taskDef.PSObject.Properties.Name -contains 'staticChecks') { $static = @($taskDef.staticChecks) }
if ($static -contains 'noBrowserDetection') {
    $hit = Select-String -Path 'Portal/src/JourneyMonitor.Web/wwwroot/*.js' -Pattern 'RW_LATENCY_SAMPLE|getJourneyLatencyMetrics|rwLatencyMetric' -List
    if ($hit) { Add-Result 'no-browser-detection' 'FAIL' ("Detection logic still in browser: " + (($hit | ForEach-Object { $_.Filename }) -join ', ')) }
    else { Add-Result 'no-browser-detection' 'PASS' 'No anomaly detection in wwwroot JS.' }
}
if ($static -contains 'nodeSyntax') {
    $jsErrors = @()
    foreach ($js in (Get-ChildItem 'Portal/src/JourneyMonitor.Web/wwwroot' -Filter *.js)) {
        & node --check $js.FullName 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) { $jsErrors += $js.Name }
    }
    if ($jsErrors.Count -gt 0) { Add-Result 'node-syntax' 'FAIL' ("Syntax errors: " + ($jsErrors -join ', ')) } else { Add-Result 'node-syntax' 'PASS' 'All wwwroot JS parses.' }
}
if ($static -contains 'minReplicas') {
    $ok = (Select-String -Path 'Portal/scripts/publish-func-qa.ps1' -Pattern 'min-replicas\s+[1-9]' -Quiet) -and (Select-String -Path '.github/workflows/deploy-qa.yml' -Pattern 'min-replicas\s+[1-9]' -Quiet)
    if ($ok) { Add-Result 'min-replicas' 'PASS' 'Functions QA keeps at least one replica.' } else { Add-Result 'min-replicas' 'FAIL' 'publish-func-qa.ps1 and deploy-qa.yml must set --min-replicas >= 1.' }
}

# ---------------------------------------------------------------- 9. Build + full test suite
$baselinePath = Join-Path $root '.github/spec1000/baseline.json'
if (-not (Test-Path $baselinePath)) {
    Add-Result 'build' 'FAIL' 'baseline.json missing. Orchestrator must run -Mode Baseline first.'
    Add-Result 'tests' 'FAIL' 'baseline.json missing.'
} else {
    $baseline = Get-Content $baselinePath -Raw | ConvertFrom-Json
    $bt = Invoke-BuildAndTest
    if ($bt.Build.ExitCode -ne 0) { Add-Result 'build' 'FAIL' "dotnet build failed. Log: $($bt.Build.Log)" }
    elseif ($bt.Warnings.Count -gt [int]$baseline.csWarnings) { Add-Result 'build' 'FAIL' ("New compiler warnings: {0} > baseline {1}. Log: {2}" -f $bt.Warnings.Count, $baseline.csWarnings, $bt.Build.Log) }
    else { Add-Result 'build' 'PASS' ("Build OK, {0} CS warnings (baseline {1})." -f $bt.Warnings.Count, $baseline.csWarnings) }

    if (-not $bt.Test) { Add-Result 'tests' 'FAIL' 'Tests not run because the build failed.' }
    elseif ($bt.Test.ExitCode -ne 0 -or $bt.Failed -gt 0) { Add-Result 'tests' 'FAIL' ("{0} failing test(s). Log: {1}" -f $bt.Failed, $bt.Test.Log) }
    elseif ($bt.Total -lt [int]$baseline.tests) { Add-Result 'tests' 'FAIL' ("Test count dropped: {0} < baseline {1}." -f $bt.Total, $baseline.tests) }
    else { Add-Result 'tests' 'PASS' ("{0} tests green (baseline {1})." -f $bt.Total, $baseline.tests) }
}

# ---------------------------------------------------------------- 10. E2E
$level = $taskDef.e2eLevel
if ($level -eq 'none') { Add-Result 'e2e' 'SKIP' 'Task has no E2E level.' }
elseif ($Mode -eq 'Verdict' -or $IncludeE2E) {
    $e2e = Invoke-Logged 'e2e' 'powershell' @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $root '.github/spec1000/e2e.ps1'), '-Level', $level)
    if ($e2e.ExitCode -eq 0) { Add-Result 'e2e' 'PASS' "E2E level '$level' passed." } else { Add-Result 'e2e' 'FAIL' "E2E level '$level' failed. Log: $($e2e.Log)" }
} else { Add-Result 'e2e' 'SKIP' "Run with -IncludeE2E to execute level '$level' locally; the tester always runs it." }

# ---------------------------------------------------------------- Tester judgement
if ($Mode -eq 'Verdict') {
    Add-Result 'acceptance' $Acceptance 'Tester verified every acceptance criterion of the prompt.'
    Add-Result 'code-review' $Review 'Code-review agent findings evaluated by the tester.'
}

$failedChecks = @($results | Where-Object { $_.status -eq 'FAIL' })
$status = 'PASS'; if ($failedChecks.Count -gt 0) { $status = 'FAIL' }

$now = (Get-Date).ToUniversalTime().ToString('o')
if ($Mode -eq 'Check') {
    $record = [ordered]@{ task = $Task; mode = 'check'; headSha = $headSha; baseRef = $BaseRef; status = $status; createdAt = $now; results = $results }
    $record | ConvertTo-Json -Depth 6 | Set-Content -Path (Join-Path $stateDir "checks/$Task.json") -Encoding UTF8
} else {
    $historyPath = Join-Path $stateDir "verdicts/$Task.history.json"
    $history = @()
    if (Test-Path $historyPath) { $history = @(Get-Content $historyPath -Raw | ConvertFrom-Json) }
    $iteration = $history.Count + 1
    if ($status -eq 'FAIL' -and $iteration -ge [int]$manifest.maxIterations) { $status = 'ESCALATED' }
    $findings = ''
    if ($FindingsFile -and (Test-Path $FindingsFile)) { $findings = [IO.File]::ReadAllText((Resolve-Path $FindingsFile).Path, [Text.Encoding]::UTF8) }
    $record = [ordered]@{ task = $Task; mode = 'verdict'; headSha = $headSha; baseRef = $BaseRef; status = $status; iteration = $iteration; maxIterations = $manifest.maxIterations; createdAt = $now; results = $results; findings = $findings }
    $record | ConvertTo-Json -Depth 6 | Set-Content -Path (Join-Path $stateDir "verdicts/$Task.json") -Encoding UTF8
    $history += [pscustomobject]@{ iteration = $iteration; headSha = $headSha; status = $status; createdAt = $now }
    ConvertTo-Json -InputObject @($history) -Depth 4 | Set-Content -Path $historyPath -Encoding UTF8
}

Write-Host ''
Write-Host ("GATE {0} {1} -> {2} (HEAD {3})" -f $Mode.ToUpper(), $Task, $status, $headSha.Substring(0, 10)) -ForegroundColor $(if ($status -eq 'PASS') { 'Green' } else { 'Red' })
if ($status -eq 'PASS') { exit 0 } else { exit 1 }
