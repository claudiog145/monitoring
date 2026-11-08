<#
.SYNOPSIS
  DT0 go/no-go: runs every built-in platform KQL query against a throwaway Kustainer and writes
  Portal/demo/compat/compat-report.md.

.DESCRIPTION
  1. Starts a temporary Kustainer container (default jm-dt0-kusto on 127.0.0.1:8198) unless
     -KustoUrl points at an engine that is already running.
  2. Builds and runs CompatRunner: two volatile databases get demo/kusto/schema.kql and ~10k
     synthetic rows each; the platform's own renderers produce the queries; the demo app()
     rewriter points them at the databases; each one is executed, with and without a timespan.
  3. Removes the container it started (keep it with -KeepContainer).

  Exit code: 0 = GO (>= 95 % OK and harness self-checks pass), 3 = NO-GO, 2 = environment problem.
  Nothing here touches the real stack (jm-web, jm-functions, jm-sql...).

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File Portal/demo/compat/run-compat.ps1
#>
[CmdletBinding()]
param(
    [string]$KustoUrl,
    [string]$ContainerName = 'jm-dt0-kusto',
    [int]$Port = 8198,
    [string]$Image = 'mcr.microsoft.com/azuredataexplorer/kustainer-linux:latest',
    [string]$Memory = '4g',
    [switch]$KeepContainer
)

$ErrorActionPreference = 'Stop'
$portal = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$runner = Join-Path $PSScriptRoot 'CompatRunner\CompatRunner.csproj'
$report = Join-Path $PSScriptRoot 'compat-report.md'
$started = $false

function Test-Kusto([string]$url) {
    try {
        $body = '{"db":"NetDefaultDB","csl":".show version"}'
        $r = Invoke-WebRequest -UseBasicParsing -Method Post -Uri "$url/v1/rest/mgmt" -ContentType 'application/json' -Body $body -TimeoutSec 5
        return $r.StatusCode -eq 200
    } catch { return $false }
}

try {
    if (-not $KustoUrl) {
        & docker info *> $null
        if ($LASTEXITCODE -ne 0) { Write-Host 'Docker is not running.' -ForegroundColor Red; exit 2 }
        $KustoUrl = "http://127.0.0.1:$Port"
        $existing = & docker ps -a --filter "name=^/$ContainerName$" --format '{{.Names}}'
        if ($existing) { & docker rm -f $ContainerName | Out-Null }
        Write-Host "Starting $ContainerName on 127.0.0.1:$Port ..."
        & docker run -d --name $ContainerName -e ACCEPT_EULA=Y -p "127.0.0.1:${Port}:8080" -m $Memory $Image | Out-Null
        if ($LASTEXITCODE -ne 0) { Write-Host "Could not start $Image." -ForegroundColor Red; exit 2 }
        $started = $true
    }

    $deadline = (Get-Date).AddSeconds(120)
    while (-not (Test-Kusto $KustoUrl)) {
        if ((Get-Date) -gt $deadline) { Write-Host "Kustainer at $KustoUrl did not answer in 120 s." -ForegroundColor Red; exit 2 }
        Start-Sleep -Seconds 2
    }

    & dotnet build $runner -nologo -v q -clp:NoSummary
    if ($LASTEXITCODE -ne 0) { Write-Host 'CompatRunner build failed.' -ForegroundColor Red; exit 2 }

    & dotnet run --project $runner --no-build -- --kusto $KustoUrl --repo $portal --report $report
    $code = $LASTEXITCODE
    if ($code -eq 0) { Write-Host 'Compatibility: GO' -ForegroundColor Green }
    elseif ($code -eq 3) { Write-Host "Compatibility: NO-GO - see $report" -ForegroundColor Yellow }
    else { Write-Host "CompatRunner failed (exit $code)." -ForegroundColor Red; $code = 2 }
    exit $code
}
finally {
    if ($started -and -not $KeepContainer) { & docker rm -f $ContainerName *> $null }
}
