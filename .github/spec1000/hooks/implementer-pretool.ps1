<#
  PreToolUse hook for spec1000-implementer.
  The task is resolved from the TARGET of each action (worktree containing the edited file, or the event cwd),
  so it works both when each implementer is a separate Copilot CLI process inside its worktree (recommended)
  and when several implementers run as subagents of one orchestrator session.
  Denies:
    - edits outside a worktree that has .spec1000/current-task.json (implementers never edit the main checkout);
    - edits outside the task's allowedPaths, to governance/state files, or to frozen contracts;
    - shell commands that write governance/state files, run -Mode Verdict/Baseline, or do git branch/merge/push/tag work.
#>
$ErrorActionPreference = 'Continue'
. "$PSScriptRoot/common.ps1"
trap { Write-PreToolDecision 'deny' ("spec1000 guard failed closed: " + $_.Exception.Message) }

$evt = Read-HookInput
if (-not $evt) { Write-PreToolDecision 'deny' 'spec1000 guard: unreadable hook input.' }
$toolName = Get-HookToolName $evt
$toolArgs = Get-HookToolArgs $evt

$cwd = $null
if ($evt.PSObject.Properties.Name -contains 'cwd') { $cwd = [string]$evt.cwd }
$eventRoot = Get-GitRoot $cwd
if (-not $eventRoot) { $eventRoot = Get-GitRoot $null }
if (-not $eventRoot) { Write-PreToolDecision 'allow' 'Not inside a git repository.' }
$mainRoot = Get-MainRoot $eventRoot
$manifest = Get-Manifest $mainRoot
if (-not $manifest) { Write-PreToolDecision 'allow' 'spec1000 manifest not found; guard inactive.' }

$isEdit = $toolName -match '(?i)^(edit|create|write|multiedit|str_replace|str_replace_editor|apply_patch|insert|editfiles|createfile)$'
$isShell = $toolName -match '(?i)(powershell|bash|shell|execute|terminal|run_in_terminal)'

if ($isEdit) {
    $paths = New-Object System.Collections.Generic.List[string]
    if ($toolArgs) {
        foreach ($k in 'path', 'file_path', 'filePath', 'filepath', 'target_file') {
            if ($toolArgs.PSObject.Properties.Name -contains $k -and $toolArgs.$k) { $paths.Add([string]$toolArgs.$k) }
        }
        foreach ($k in 'patch', 'input') {
            if ($toolArgs.PSObject.Properties.Name -contains $k -and $toolArgs.$k) {
                foreach ($m in [regex]::Matches([string]$toolArgs.$k, '(?m)^\*\*\* (?:Add|Update|Delete) File: (.+)$')) { $paths.Add($m.Groups[1].Value.Trim()) }
            }
        }
    }
    if ($paths.Count -eq 0) { Write-PreToolDecision 'deny' "spec1000 guard: could not determine the target path of '$toolName'." }

    foreach ($p in $paths) {
        $root = Get-RootForPath $p $eventRoot
        if (-not $root) { Write-PreToolDecision 'deny' "spec1000 guard: '$p' is not inside a git worktree." }
        $current = Get-CurrentTask $root
        if (-not $current) { Write-PreToolDecision 'deny' "spec1000 guard: '$root' has no assigned task. Implementers only edit inside their task worktree (see .spec1000/current-task.json)." }
        if ($current.status -ne 'implementing') { Write-PreToolDecision 'deny' "spec1000 guard: task $($current.taskId) is in status '$($current.status)'. Edits are only allowed while implementing." }
        $taskDef = $manifest.tasks | Where-Object { $_.id -eq $current.taskId }
        $rel = Get-RelativeTo $p $root
        if (-not $rel) { Write-PreToolDecision 'deny' "spec1000 guard: '$p' resolves outside $root." }
        if ($rel -eq 'SPEC1000-BLOCKED.md') { continue }
        if (Test-Glob $rel $manifest.alwaysForbidden) { Write-PreToolDecision 'deny' "spec1000 guard: '$rel' is a governance/state file. Implementers never edit it." }
        if ($taskDef.contractsFrozen -and (Test-Glob $rel $manifest.contractFiles)) { Write-PreToolDecision 'deny' "spec1000 guard: '$rel' is a frozen contract. Describe the problem in SPEC1000-BLOCKED.md and stop." }
        if (-not (Test-Glob $rel $taskDef.allowedPaths)) { Write-PreToolDecision 'deny' ("spec1000 guard: task {0} does not own '{1}'. Allowed: {2}" -f $taskDef.id, $rel, ($taskDef.allowedPaths -join ', ')) }
    }
    Write-PreToolDecision 'allow' 'spec1000 guard: path owned by the current task.'
}

if ($isShell) {
    $cmd = ''
    if ($toolArgs) { foreach ($k in 'command', 'cmd', 'script') { if ($toolArgs.PSObject.Properties.Name -contains $k -and $toolArgs.$k) { $cmd = [string]$toolArgs.$k } } }
    $norm = $cmd.Replace('\', '/')
    if ($norm -match '(?i)-Mode\s+(Verdict|Baseline)') { Write-PreToolDecision 'deny' 'spec1000 guard: only spec1000-tester writes verdicts and only the orchestrator writes the baseline. Use: run-gate.ps1 -Task <id> -Mode Check' }
    $touchesGovernance = $norm -match '(?i)\.spec1000|\.github/(spec1000|agents|prompts)'
    $isGateRun = $norm -match '(?i)\.github/spec1000/run-gate\.ps1\s+-Task\s+\w+(\s+-Mode\s+Check)?(\s+-IncludeE2E)?\s*$'
    $isRead = ($norm -match '(?i)^\s*(Get-Content|cat|type|Select-String|rg|grep|Get-ChildItem|ls|dir)\b') -and ($norm -notmatch '(?i)(Set-Content|Out-File|Add-Content|>|Remove-Item|Move-Item|Copy-Item|New-Item)')
    if ($touchesGovernance -and -not ($isGateRun -or $isRead)) { Write-PreToolDecision 'deny' 'spec1000 guard: shell access to .spec1000 or .github governance files is limited to reading and to run-gate.ps1 -Mode Check.' }
    if ($norm -match '(?i)\bgit\s+(push|merge|rebase|tag|worktree|cherry-pick|pull|reset\s+--hard|clean\s+-[a-z]*f|switch|checkout\s+-b|checkout\s+(main|master)\b|checkout\s+feature/)') {
        Write-PreToolDecision 'deny' 'spec1000 guard: branch, merge, pull, push and tag operations belong to the orchestrator. Commit on your task branch only.'
    }
    if ($norm -match '(?i)\bgit\s+commit\b.*--no-verify') { Write-PreToolDecision 'deny' 'spec1000 guard: --no-verify is not allowed.' }
    Write-PreToolDecision 'allow' 'spec1000 guard: command allowed.'
}

Write-PreToolDecision 'allow' 'spec1000 guard: tool not restricted.'
