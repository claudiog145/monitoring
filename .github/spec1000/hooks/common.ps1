# Shared helpers for spec1000 hooks. Dot-source: . "$PSScriptRoot/common.ps1"
# Windows PowerShell 5.1 compatible.

function Read-HookInput {
    try { return ([Console]::In.ReadToEnd() | ConvertFrom-Json) } catch { return $null }
}

function Get-HookToolName($evt) {
    if (-not $evt) { return '' }
    foreach ($n in 'tool_name', 'toolName') { if ($evt.PSObject.Properties.Name -contains $n) { return [string]$evt.$n } }
    return ''
}

function Get-HookToolArgs($evt) {
    if (-not $evt) { return $null }
    foreach ($n in 'tool_input', 'toolArgs', 'toolInput') {
        if ($evt.PSObject.Properties.Name -contains $n) {
            $a = $evt.$n
            if ($a -is [string]) { try { $a = $a | ConvertFrom-Json } catch { } }
            return $a
        }
    }
    return $null
}

function Get-GitRoot([string]$dir) {
    if (-not $dir) { $dir = (Get-Location).Path }
    $r = & git -C $dir rev-parse --show-toplevel 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $r) { return $null }
    return ([IO.Path]::GetFullPath($r.Trim())).Replace('\', '/').TrimEnd('/')
}

# Main repository (common git dir parent), even when called from a worktree.
function Get-MainRoot([string]$anyRoot) {
    $common = & git -C $anyRoot rev-parse --git-common-dir 2>$null
    if (-not $common) { return $anyRoot }
    $common = $common.Trim()
    if (-not [IO.Path]::IsPathRooted($common)) { $common = Join-Path $anyRoot $common }
    return ([IO.Path]::GetFullPath((Split-Path $common -Parent))).Replace('\', '/').TrimEnd('/')
}

function Get-Manifest([string]$root) {
    $p = Join-Path $root '.github/spec1000/tasks.json'
    if (-not (Test-Path $p)) { return $null }
    return (Get-Content $p -Raw -Encoding UTF8 | ConvertFrom-Json)
}

function Get-CurrentTask([string]$root) {
    $p = Join-Path $root '.spec1000/current-task.json'
    if (-not (Test-Path $p)) { return $null }
    return (Get-Content $p -Raw | ConvertFrom-Json)
}

# Root of the git working tree that contains $path (worktrees included), or $null.
function Get-RootForPath([string]$path, [string]$fallbackRoot) {
    $full = $path
    if (-not [IO.Path]::IsPathRooted($full)) { $full = Join-Path $fallbackRoot $full }
    try { $full = [IO.Path]::GetFullPath($full) } catch { return $null }
    $dir = $full
    while ($dir -and -not (Test-Path $dir -PathType Container)) { $dir = Split-Path $dir -Parent }
    if (-not $dir) { return $null }
    return (Get-GitRoot $dir)
}

function Get-RelativeTo([string]$path, [string]$root) {
    $full = $path
    if (-not [IO.Path]::IsPathRooted($full)) { $full = Join-Path $root $full }
    $full = ([IO.Path]::GetFullPath($full)).Replace('\', '/')
    if ($full.StartsWith($root + '/', [StringComparison]::OrdinalIgnoreCase)) { return $full.Substring($root.Length + 1) }
    return $null
}

function Convert-GlobToRegex([string]$glob) {
    $e = [regex]::Escape($glob.Replace('\', '/'))
    $e = $e.Replace('\*\*/', '(.*/)?').Replace('\*\*', '.*').Replace('\*', '[^/]*').Replace('\?', '[^/]')
    return '^' + $e + '$'
}

function Test-Glob([string]$path, [object[]]$globs) {
    foreach ($g in $globs) { if ($g -and $path -match (Convert-GlobToRegex $g)) { return $true } }
    return $false
}

# Worktrees whose current-task.json says an implementer is working on them.
function Get-ImplementingWorktrees([string]$mainRoot) {
    $list = New-Object System.Collections.Generic.List[string]
    $lines = & git -C $mainRoot worktree list --porcelain 2>$null
    foreach ($l in $lines) {
        if ($l -like 'worktree *') {
            $wt = ([IO.Path]::GetFullPath($l.Substring(9).Trim())).Replace('\', '/')
            $ct = Get-CurrentTask $wt
            if ($ct -and $ct.status -eq 'implementing') { $list.Add($wt) }
        }
    }
    return $list
}

function Write-PreToolDecision([string]$decision, [string]$reason) {
    @{ hookSpecificOutput = @{ hookEventName = 'PreToolUse'; permissionDecision = $decision; permissionDecisionReason = $reason } } | ConvertTo-Json -Depth 4 -Compress
    exit 0
}
