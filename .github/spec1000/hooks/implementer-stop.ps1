<#
  Stop hook for spec1000-implementer (runs as SubagentStop when the implementer is a subagent).
  The implementer may only finish when, for its task worktree, ONE of these is true:
    1. .spec1000/checks/<task>.json is PASS for the current HEAD and the tree is clean;
    2. SPEC1000-BLOCKED.md exists in the worktree root (contract problem / external blocker);
    3. the tester already ESCALATED the task.
  Worktree resolution: event cwd -> process cwd -> the single worktree in status "implementing".
  If several worktrees are implementing and none can be attributed (parallel subagents in one session), the hook
  allows the stop with a warning: the orchestrator merge guard still requires a tester PASS verdict.
#>
$ErrorActionPreference = 'Continue'
. "$PSScriptRoot/common.ps1"

function Block([string]$reason) { @{ decision = 'block'; reason = $reason } | ConvertTo-Json -Compress; exit 0 }
function Allow([string]$message) { @{ continue = $true; systemMessage = $message } | ConvertTo-Json -Compress; exit 0 }
trap { Allow ("spec1000 stop gate error (allowing; the merge gate still requires a tester PASS): " + $_.Exception.Message) }

$evt = Read-HookInput
$cwd = $null
if ($evt -and $evt.PSObject.Properties.Name -contains 'cwd') { $cwd = [string]$evt.cwd }
$root = Get-GitRoot $cwd
if (-not $root) { $root = Get-GitRoot $null }
if (-not $root) { Allow 'spec1000: not in a repository.' }

$current = Get-CurrentTask $root
if (-not $current) {
    $candidates = @(Get-ImplementingWorktrees (Get-MainRoot $root))
    if ($candidates.Count -eq 1) { $root = $candidates[0]; $current = Get-CurrentTask $root }
    elseif ($candidates.Count -gt 1) { Allow ("spec1000: {0} tasks implementing in parallel in one session; stop gate cannot attribute this subagent. The merge gate still requires a tester PASS." -f $candidates.Count) }
    else { Allow 'spec1000: no assigned task.' }
}
$task = $current.taskId
$gate = 'powershell -NoProfile -ExecutionPolicy Bypass -File .github/spec1000/run-gate.ps1 -Task ' + $task

if (Test-Path (Join-Path $root 'SPEC1000-BLOCKED.md')) { Allow "spec1000: task $task reported BLOCKED. The orchestrator decides." }
$verdictFile = Join-Path $root ".spec1000/verdicts/$task.json"
if (Test-Path $verdictFile) {
    $verdict = Get-Content $verdictFile -Raw | ConvertFrom-Json
    if ($verdict.status -eq 'ESCALATED') { Allow "spec1000: task $task escalated after $($verdict.iteration) iterations." }
}

$head = (& git -C $root rev-parse HEAD).Trim()
$dirty = @(& git -C $root status --porcelain | Where-Object { $_ -notmatch '\.spec1000/' -and $_ -notmatch 'SPEC1000-BLOCKED\.md' })
if ($dirty.Count -gt 0) { Block "spec1000 [$task]: $($dirty.Count) uncommitted file(s) in $root. Commit on your task branch, then run (from $root): $gate" }

$checkFile = Join-Path $root ".spec1000/checks/$task.json"
if (-not (Test-Path $checkFile)) { Block "spec1000 [$task]: self-check not run. From $root run: $gate   and fix every FAIL before finishing." }
$check = Get-Content $checkFile -Raw | ConvertFrom-Json
if ($check.headSha -ne $head) { Block "spec1000 [$task]: last self-check was for $($check.headSha.Substring(0,10)) but HEAD is $($head.Substring(0,10)). Re-run: $gate" }
if ($check.status -ne 'PASS') {
    $fails = @($check.results | Where-Object { $_.status -eq 'FAIL' } | ForEach-Object { "$($_.check): $($_.details)" })
    Block ("spec1000 [$task]: self-check FAILED. Fix and re-run. Failing: " + ($fails -join ' || '))
}
Allow "spec1000 [$task]: self-check PASS for $($head.Substring(0,10)). Ready for spec1000-tester."
