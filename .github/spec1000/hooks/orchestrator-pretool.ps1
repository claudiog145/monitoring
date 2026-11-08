<#
  PreToolUse hook for spec1000-orchestrator.
  Denies:
    - git merge / cherry-pick / pull of feature/spec-1000-<task> unless that task's tester verdict is PASS for the exact
      commit being merged;
    - git tag spec1000-contracts-v1 unless the F0 verdict is PASS for HEAD;
    - pushing or merging into main/master (a human merges the final PR);
    - writing verdict files by hand.
#>
$ErrorActionPreference = 'Continue'

function Write-Decision([string]$decision, [string]$reason) {

    @{ hookSpecificOutput = @{ hookEventName = 'PreToolUse'; permissionDecision = $decision; permissionDecisionReason = $reason } } | ConvertTo-Json -Depth 4 -Compress
    exit 0
}

trap { Write-Decision 'deny' ("spec1000 orchestrator guard failed closed: " + $_.Exception.Message) }
try { $evt = [Console]::In.ReadToEnd() | ConvertFrom-Json } catch { Write-Decision 'deny' 'spec1000 orchestrator guard: unreadable hook input.' }
$toolName = ''
foreach ($n in 'tool_name', 'toolName') { if ($evt.PSObject.Properties.Name -contains $n) { $toolName = [string]$evt.$n } }
$toolArgs = $null
foreach ($n in 'tool_input', 'toolArgs', 'toolInput') {
    if ($evt.PSObject.Properties.Name -contains $n) { $toolArgs = $evt.$n; if ($toolArgs -is [string]) { try { $toolArgs = $toolArgs | ConvertFrom-Json } catch { } } }
}

$root = (& git rev-parse --show-toplevel 2>$null)
if (-not $root) { Write-Decision 'allow' 'Not in a repository.' }
$root = $root.Trim()
$manifest = Get-Content (Join-Path $root '.github/spec1000/tasks.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$wtRoot = [IO.Path]::GetFullPath((Join-Path $root $manifest.worktreeRoot))

function Get-Verdict([string]$taskId) {
    $candidates = @((Join-Path $wtRoot ("{0}/.spec1000/verdicts/{1}.json" -f $taskId.ToLower(), $taskId)), (Join-Path $root ".spec1000/verdicts/$taskId.json"))
    foreach ($c in $candidates) { if (Test-Path $c) { return (Get-Content $c -Raw | ConvertFrom-Json) } }
    return $null
}

if ($toolName -match '(?i)^(edit|create|write|multiedit|str_replace|apply_patch)$' -and $toolArgs) {
    foreach ($k in 'path', 'file_path', 'filePath') {
        if ($toolArgs.PSObject.Properties.Name -contains $k -and ([string]$toolArgs.$k).Replace('\', '/') -match '\.spec1000/(verdicts|checks)/') {
            Write-Decision 'deny' 'spec1000 orchestrator guard: verdicts and checks are written only by run-gate.ps1.'
        }
    }
}

if ($toolName -match '(?i)(powershell|bash|shell|execute|terminal)') {
    $cmd = ''
    if ($toolArgs) { foreach ($k in 'command', 'cmd', 'script') { if ($toolArgs.PSObject.Properties.Name -contains $k -and $toolArgs.$k) { $cmd = [string]$toolArgs.$k } } }

    if ($cmd -match '(?i)-Mode\s+Verdict') { Write-Decision 'deny' 'spec1000 orchestrator guard: only spec1000-tester runs -Mode Verdict.' }
    if ($manifest.noPush -and $cmd -match '(?i)\bgit\s+push\b|\bgh\s+pr\s+create\b') { Write-Decision 'deny' "spec1000 orchestrator guard: $($manifest.noPushReason)" }
    if ($cmd -match '(?i)\bgit\s+push\b[^;&|]*\b(main|master)\b') { Write-Decision 'deny' 'spec1000 orchestrator guard: never push to main. Open a PR from feature/spec-1000; a human merges it.' }
    if ($cmd -match '(?i)\bgit\s+(checkout|switch)\s+(main|master)\b[\s\S]*\bgit\s+merge\b') { Write-Decision 'deny' 'spec1000 orchestrator guard: never merge into main locally.' }
    if ($cmd -match '(?i)\.spec1000[/\\](verdicts|checks)' -and $cmd -match '(?i)(Set-Content|Out-File|Add-Content|>|Copy-Item|Move-Item|Remove-Item|New-Item)') {
        Write-Decision 'deny' 'spec1000 orchestrator guard: verdict/check files are written only by run-gate.ps1.'
    }

    if ($cmd -match ('(?i)\bgit\s+tag\b[^;&|]*' + [regex]::Escape($manifest.contractsTag))) {
        $v = Get-Verdict 'F0'
        if (-not $v -or $v.status -ne 'PASS') { Write-Decision 'deny' 'spec1000 orchestrator guard: F0 has no PASS verdict; contracts cannot be frozen.' }
        Write-Decision 'allow' 'F0 PASS verdict found.'
    }

    $mergeMatches = [regex]::Matches($cmd, '(?i)\bgit\s+(merge|cherry-pick|pull)\b[^;&|]*?feature/spec-1000-([a-z0-9]+)')
    foreach ($m in $mergeMatches) {
        $taskId = $m.Groups[2].Value.ToUpper()
        $taskDef = $manifest.tasks | Where-Object { $_.id -eq $taskId }
        if (-not $taskDef) { Write-Decision 'deny' "spec1000 orchestrator guard: unknown task branch feature/spec-1000-$($m.Groups[2].Value)." }
        $v = Get-Verdict $taskId
        if (-not $v) { Write-Decision 'deny' "spec1000 orchestrator guard: $taskId has no tester verdict. Invoke spec1000-tester first." }
        if ($v.status -ne 'PASS') { Write-Decision 'deny' "spec1000 orchestrator guard: $taskId verdict is $($v.status) (iteration $($v.iteration)). Loop back to the implementer or escalate." }
        $branchSha = (& git -C $root rev-parse "feature/spec-1000-$($m.Groups[2].Value)" 2>$null)
        if (-not $branchSha -or $branchSha.Trim() -ne $v.headSha) { Write-Decision 'deny' "spec1000 orchestrator guard: $taskId verdict is for $($v.headSha) but the branch is at $branchSha. Re-run the tester." }
        foreach ($dep in @($taskDef.dependsOn)) {
            if (-not $dep) { continue }
            $depVerdict = Get-Verdict $dep
            if (-not $depVerdict -or $depVerdict.status -ne 'PASS') { Write-Decision 'deny' "spec1000 orchestrator guard: dependency $dep has no PASS verdict." }
            & git -C $root merge-base --is-ancestor $depVerdict.headSha $manifest.integrationBranch 2>$null
            if ($LASTEXITCODE -ne 0) { Write-Decision 'deny' "spec1000 orchestrator guard: dependency $dep is not merged into $($manifest.integrationBranch) yet." }
        }
    }
}

Write-Decision 'allow' 'spec1000 orchestrator guard: allowed.'
