<#
.SYNOPSIS
  Prepara la demo completa del Agentic Operations Portal en este equipo, sin pasos manuales.

.DESCRIPTION
  Deja funcionando el portal real (http://localhost:5080) leyendo un Azure falso con datos sintéticos
  de 4 empresas (Arrivia, Banco Andino, Kinectify, Mercado Libre):

    1. Verifica Docker y memoria.
    2. Escribe en Portal/.env el bloque de demo (pide el token del LLM si falta; nunca lo versiona).
    3. Levanta el stack: SQL, Azurite, Functions, Web + Kustainer, Azure falso y generador.
    4. Genera N días de historia sintética (determinística: misma semilla, mismos datos).
    5. Carga en la plataforma los perfiles, fuentes, KQL y KPIs de los 4 paquetes (Portal/demo/packs).
    6. Importa al Memory Bank los RCAs de cada paquete.
    7. Crea los KPIs "demo 1m" (detección en ~2 min) y calibra sus umbrales con la historia generada.
    8. Deja la compañía activa, monos y daemons listos y verifica todo.

  Se puede volver a ejecutar: lo que ya existe se reutiliza.

  Después:  .\demo-incidents.ps1 -All -Minutes 90     (ver docs en Portal/demo/DEMO-SETUP.md)

.EXAMPLE
  .\demo-setup.ps1                 # primera vez en un equipo nuevo
.EXAMPLE
  .\demo-setup.ps1 -Fresh          # borra los datos de demo (Kusto + platformDB) y prepara todo de cero
.EXAMPLE
  .\demo-setup.ps1 -SkipBuild -SkipReset   # re-sembrar la plataforma sin reconstruir ni regenerar
#>
[CmdletBinding()]
param(
    [switch]$Fresh,
    [switch]$Force,
    [switch]$SkipBuild,
    [switch]$SkipReset,
    [switch]$SkipCorpus,
    [switch]$IncludeArriviaCorpus,
    [int]$Days = 8,
    [ValidateSet('tiny', 'laptop', 'showcase')][string]$Scale = 'laptop',
    [ValidateSet('arrivia', 'banco', 'kinectify', 'mercadolibre')][string]$ActiveCompany = 'arrivia',
    [int]$CalibrationTimeoutMinutes = 45
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$portal = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$repo = (Resolve-Path (Join-Path $portal '..')).Path
$packsDir = Join-Path $portal 'demo\packs'
$envFile = Join-Path $portal '.env'
$corpusRoot = Join-Path $repo 'docs\RCAS-md'

$Fn = 'http://localhost:8081'
$Web = 'http://localhost:5080'
$Gen = 'http://localhost:8087'
$Fake = 'http://localhost:8086'
$Kusto = 'http://localhost:8085'
$writeSecret = 'Jm_LocalDev_SettingsSecret_1!'
$internalSecret = 'Jm_LocalDev_InternalApiSecret_1!'
$saPassword = 'Jm_LocalDev_1!'
$headers = @{ 'X-Settings-Write-Secret' = $writeSecret; 'X-Internal-Api-Secret' = $internalSecret }
$monkeys = 'latency-scout-monkey', 'exception-scout-monkey', 'inspector-monkey', 'rca-monkey', 'latency-spike-monkey', 'slo-burn-rate-monkey', 'doctor-monkey'
$started = Get-Date

function Step([string]$text) { Write-Host ''; Write-Host ("[{0}] {1}" -f (Get-Date).ToString('HH:mm:ss'), $text) -ForegroundColor Cyan }
function Ok([string]$text) { Write-Host "   OK  $text" -ForegroundColor Green }
function Warn([string]$text) { Write-Host "   !!  $text" -ForegroundColor Yellow }
function Fail([string]$text) { Write-Host "   XX  $text" -ForegroundColor Red; throw $text }

function Native([scriptblock]$Block) {
    # PowerShell 5.1 turns any stderr line of a native command into an error under 'Stop'; docker writes progress there.
    $previous = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try { & $Block } finally { $ErrorActionPreference = $previous }
}

function Invoke-Api([string]$method, [string]$url, $body = $null, [int]$timeout = 60) {
    $request = @{ Method = $method; Uri = $url; Headers = $headers; TimeoutSec = $timeout; ContentType = 'application/json; charset=utf-8' }
    if ($null -ne $body) {
        $json = if ($body -is [string]) { $body } else { $body | ConvertTo-Json -Depth 40 }
        $request.Body = [Text.Encoding]::UTF8.GetBytes($json)
    }
    Invoke-RestMethod @request
}

function Try-Api([string]$method, [string]$url, $body = $null) {
    try { Invoke-Api $method $url $body | Out-Null; return $null }
    catch {
        $code = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { 0 }
        $detail = $_.ErrorDetails.Message
        if (-not $detail -and $_.Exception.Response) {
            try { $detail = (New-Object IO.StreamReader($_.Exception.Response.GetResponseStream())).ReadToEnd() } catch { }
        }
        return [pscustomobject]@{ Code = $code; Message = "$method $url -> $code $detail" }
    }
}

function Invoke-Sql([string]$query, [switch]$Raw) {
    $file = Join-Path $env:TEMP ("demo-setup-{0}.sql" -f [guid]::NewGuid().ToString('N'))
    [IO.File]::WriteAllText($file, "SET NOCOUNT ON; SET QUOTED_IDENTIFIER ON;`n" + $query, (New-Object Text.UTF8Encoding($false)))
    Native { docker cp $file "jm-sql:/tmp/demo-setup.sql" } | Out-Null
    Remove-Item $file -Force
    $out = Native { docker exec jm-sql /opt/mssql-tools18/bin/sqlcmd -C -I -b -S localhost -U sa -P $saPassword -d platformDB -h -1 -W -s '|' -i /tmp/demo-setup.sql 2>&1 }
    if ($LASTEXITCODE -ne 0) { throw "SQL falló: $($out -join ' ')" }
    if ($Raw) { return $out }
    return @($out | Where-Object { "$_".Trim() -ne '' })
}

function Wait-Url([string]$url, [string]$name, [int]$seconds = 300) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        try { Invoke-WebRequest $url -UseBasicParsing -TimeoutSec 10 | Out-Null; Ok "$name responde"; return }
        catch { Start-Sleep 5 }
    }
    Fail "$name no respondió en $seconds s ($url)"
}

function Set-EnvValue([string]$key, [string]$value) {
    $lines = if (Test-Path $envFile) { [IO.File]::ReadAllLines($envFile) } else { @() }
    $found = $false
    $lines = @($lines | ForEach-Object {
        if ($_ -match "^\s*$([regex]::Escape($key))=") { $found = $true; "$key=$value" } else { $_ }
    })
    if (-not $found) { $lines += "$key=$value" }
    [IO.File]::WriteAllText($envFile, (($lines -join "`r`n") + "`r`n").TrimStart([char]0xFEFF), (New-Object Text.UTF8Encoding($false)))
}

function Get-EnvValue([string]$key) {
    if (-not (Test-Path $envFile)) { return $null }
    $line = [IO.File]::ReadAllLines($envFile) | Where-Object { $_ -match "^\s*$([regex]::Escape($key))=" } | Select-Object -Last 1
    if ($line) { return ($line -split '=', 2)[1] } else { return $null }
}

function Get-KustainerGb {
    $mem = Native { docker stats --no-stream --format '{{.MemUsage}}' jm-kustainer 2>$null }
    if ("$mem" -match '^([\d\.]+)\s*GiB') { return [double]$Matches[1] }
    if ("$mem" -match '^([\d\.]+)\s*MiB') { return [double]$Matches[1] / 1024 }
    return 0
}

function Restart-Kustainer {
    Write-Host '   Reiniciando Kustainer para liberar memoria (los datos persisten)...'
    Native { docker restart -t 20 jm-kustainer 2>&1 } | Out-Null
    $deadline = (Get-Date).AddMinutes(5)
    do { Start-Sleep 10; $health = Native { docker inspect jm-kustainer --format '{{.State.Health.Status}}' 2>$null } } while ($health -ne 'healthy' -and (Get-Date) -lt $deadline)
    if ($health -ne 'healthy') { Fail 'Kustainer no volvió a estar healthy después del reinicio.' }
    Ok ("Kustainer reiniciado ({0:N1} GB)" -f (Get-KustainerGb))
}

function Set-Monkeys([bool]$paused) {
    foreach ($m in $monkeys) {
        $e = Try-Api 'POST' "$Fn/api/monkeys/$m/pause" @{ paused = $paused; changedBy = 'demo-setup' }
        if ($e) { Warn $e.Message }
    }
}

# ---------------------------------------------------------------------------------------------
Step '1/9  Verificando Docker y memoria'
$info = Native { docker info --format '{{.MemTotal}}' 2>$null }
if (-not $info) { Fail 'Docker no responde. Abrí Docker Desktop y volvé a ejecutar.' }
$dockerGb = [math]::Round([double]$info / 1GB, 1)
Ok "Docker arriba, $dockerGb GB para contenedores"
$kustoMem = if ($dockerGb -ge 14) { '9g' } elseif ($dockerGb -ge 10) { '6g' } else { '4g' }
if ($dockerGb -lt 10) {
    Warn 'Menos de 10 GB para Docker: uso escala "tiny". Para la escala normal subí la memoria de Docker Desktop (Settings > Resources) a 14 GB o más.'
    if ($Scale -ne 'tiny') { $Scale = 'tiny' }
}

# ---------------------------------------------------------------------------------------------
Step '2/9  Configurando Portal/.env (bloque de demo)'
if (-not (Test-Path $envFile)) {
    Copy-Item (Join-Path $portal '.env.example') $envFile
    Ok 'Creado Portal/.env desde .env.example'
}
$token = Get-EnvValue 'Ai__GitHubToken'
if ([string]::IsNullOrWhiteSpace($token)) {
    Warn 'Falta Ai__GitHubToken (el LLM de Triage, Inspector y RCA; también las embeddings del Memory Bank).'
    $secure = Read-Host '   Pegá el GitHub token (Enter para seguir sin LLM)' -AsSecureString
    $plain = [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure))
    if ($plain) { Set-EnvValue 'Ai__GitHubToken' $plain; Ok 'Token guardado en Portal/.env (no se versiona)' }
    else { Warn 'Sin token: la detección funciona, pero no habrá investigaciones con LLM ni Memory Bank.' }
}
$demoEnv = [ordered]@{
    'Insights__QueryEndpoint'                 = 'http://fake-appinsights:8080'
    'Insights__QueryApiKeys'                  = '*:jm-demo-key'
    'Insights__Hotels__AppInsightsAppId'      = 'demo-tourism-sor-frontend'
    'Insights__Cruises__AppInsightsAppId'     = 'demo-tourism-cruise-core'
    'Insights__Air__AppInsightsAppId'         = 'demo-tourism-air-api'
    'Demo__Enabled'                           = 'true'
    'TokenRefresh__Enabled'                   = 'false'
    'HealthMonitoring__Enabled'               = 'true'
    'HealthMonitoring__Pillars__0'            = 'Application'
    'HealthMonitoring__Pillars__1'            = 'Business'
    'HealthMonitoring__Pillars__2'            = 'Infrastructure'
    'HealthMonitoring__Ingestion__LagMinutes' = '1'
    'HealthMonitoring__MaxKpisPerCycle'       = '300'
    # A fresh database backfills every KPI's history; 20k buckets per minute (the default) takes 30+ min.
    'HealthMonitoring__MaxRowsPerCycle'       = '50000'
    'Triage__Mode'                            = 'Shadow'
    'Scout__UseComposedBoard'                 = 'true'
    'ScoutLatency__DetectionMode'             = 'Method'
    'ScoutException__DetectionMode'           = 'Method'
    'ScoutLatency__IntervalMinutes'           = '2'
    'ScoutException__IntervalMinutes'         = '2'
    'ScoutLatency__WindowHours'               = '0.75'
    'ScoutException__WindowHours'             = '0.75'
    'ScoutLatency__MaxNewCasesPerRun'         = '5'
    'ScoutException__MaxNewCasesPerRun'       = '5'
    'Inspector__Concurrency'                  = '3'
    'Inspector__RunPackagesAfterTriage'       = 'true'
    'Inspector__MaxInvestigationsPerDay'      = '150'
    'Rca__AutoComposeMinConfidence'           = '0.30'
    'KUSTAINER_MEM'                           = $kustoMem
    'DEMO_SCALE'                              = $Scale
}
foreach ($k in $demoEnv.Keys) { Set-EnvValue $k $demoEnv[$k] }
Ok "$($demoEnv.Count) valores de demo escritos (Kustainer $kustoMem, escala $Scale)"

# ---------------------------------------------------------------------------------------------
Step '3/9  Levantando el stack'
Push-Location $portal
try {
    # Contenedor manual de versiones anteriores de esta demo: ocupa el puerto 8087 del generador.
    if (Native { docker ps -a --format '{{.Names}}' } | Where-Object { $_ -eq 'jm-demo-generator-preview' }) {
        Native { docker rm -f jm-demo-generator-preview 2>&1 } | Out-Null
        Ok 'Quitado el generador manual anterior (jm-demo-generator-preview)'
    }
    if ($Fresh) {
        if (-not $Force) {
            $answer = Read-Host '   -Fresh BORRA los datos de demo (telemetría sintética y la base platformDB). ¿Continuar? (s/N)'
            if ($answer -notmatch '^[sSyY]') { Fail 'Cancelado por el usuario.' }
        }
        $sqlClients = 'jm-functions', 'jm-functions-qa', 'jm-web', 'jm-functions-demo', 'jm-web-demo'
        $running = @(Native { docker ps --format '{{.Names}}' })
        $script:restartQa = $running -contains 'jm-functions-qa'
        $toStop = @($sqlClients | Where-Object { $running -contains $_ })
        if ($toStop.Count -gt 0) { Native { docker stop -t 20 @toStop 2>&1 } | Out-Null }
        Native { docker compose --profile demo stop demo-generator fake-appinsights kustainer 2>&1 } | Out-Null
        Native { docker compose --profile demo rm -f demo-generator fake-appinsights kustainer sql-init 2>&1 } | Out-Null
        Native { docker volume rm portal_kusto-demo-data 2>&1 } | Out-Null
        if (Native { docker volume ls --format '{{.Name}}' } | Where-Object { $_ -eq 'portal_kusto-demo-data' }) { Fail 'No se pudo borrar el volumen portal_kusto-demo-data.' }
        Ok 'Telemetría sintética borrada (volumen portal_kusto-demo-data)'
        if ($running -contains 'jm-sql') {
            $stamp = (Get-Date).ToString('yyyyMMdd-HHmm')
            Native { docker exec jm-sql /opt/mssql-tools18/bin/sqlcmd -C -b -S localhost -U sa -P $saPassword -Q "IF DB_ID('platformDB') IS NOT NULL BACKUP DATABASE platformDB TO DISK='/var/opt/mssql/data/platformDB-before-demo-setup-$stamp.bak' WITH COMPRESSION, INIT" 2>&1 } | Out-Null
            Native { docker exec jm-sql /opt/mssql-tools18/bin/sqlcmd -C -b -S localhost -U sa -P $saPassword -Q "IF DB_ID('platformDB') IS NOT NULL BEGIN ALTER DATABASE platformDB SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE platformDB; END" 2>&1 } | Out-Null
            $left = Native { docker exec jm-sql /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P $saPassword -h -1 -W -Q "SET NOCOUNT ON; SELECT COUNT(*) FROM sys.databases WHERE name='platformDB'" 2>&1 }
            if ("$(@($left) | Where-Object { "$_".Trim() -ne '' } | Select-Object -Last 1)".Trim() -ne '0') {
                Native { docker exec jm-sql /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P $saPassword -Q "ALTER DATABASE platformDB SET MULTI_USER WITH ROLLBACK IMMEDIATE" 2>&1 } | Out-Null
                Fail 'No se pudo borrar platformDB (otro proceso la estaba usando). Volvé a ejecutar con -Fresh.'
            }
            Ok "platformDB borrada (backup en jm-sql:/var/opt/mssql/data/platformDB-before-demo-setup-$stamp.bak)"
        }
    }
    $services = 'sql', 'sql-init', 'azurite', 'functions', 'web', 'kustainer', 'fake-appinsights', 'demo-generator'
    if ($SkipBuild) { Native { docker compose --profile demo up -d @services 2>&1 } | Out-Null }
    else {
        Write-Host '   Construyendo imágenes (la primera vez tarda 5-15 min)...'
        Native { docker compose --profile demo up -d --build @services 2>&1 } | Out-Null
    }
    if ($LASTEXITCODE -ne 0) { Fail 'docker compose up falló. Ejecutá "docker compose --profile demo up -d --build" en Portal para ver el detalle.' }
}
finally { Pop-Location }
Wait-Url "$Fn/api/settings/company-profiles-active" 'Functions' 420
Wait-Url "$Web/api/demo-mode" 'Portal' 300
Wait-Url "$Fake/health" 'Azure falso' 300
Wait-Url "$Gen/health" 'Generador' 300
Set-Monkeys $true
Ok 'Monos en pausa mientras se prepara todo'

# ---------------------------------------------------------------------------------------------
Step "4/9  Telemetría sintética ($Days días, escala $Scale)"
$hasData = $false
try {
    $r = Invoke-RestMethod -Method Post "$Kusto/v1/rest/query" -ContentType 'application/json' -Body (@{ db = '_demo_registry'; csl = '_company | count' } | ConvertTo-Json) -TimeoutSec 60
    $hasData = [long]$r.Tables[0].Rows[0][0] -gt 0
} catch { $hasData = $false }
if ($SkipReset -and $hasData) { Ok 'Ya hay telemetría: se reutiliza (-SkipReset)' }
elseif ($hasData -and -not $Fresh) { Ok 'Ya hay telemetría: se reutiliza (usá -Fresh para regenerarla)' }
else {
    Write-Host '   Generando historia (5-15 min según el equipo)...'
    $reset = Invoke-RestMethod -Method Post "$Gen/api/reset" -ContentType 'application/json' -Body (@{ days = $Days; scale = $Scale } | ConvertTo-Json) -TimeoutSec 3600
    $rows = 0
    foreach ($c in $reset.companies) { foreach ($p in $c.rows.PSObject.Properties) { $rows += [long]$p.Value } }
    Ok ("{0:N0} eventos generados en {1:N0} s" -f $rows, $reset.elapsedSeconds)
    # A full reset leaves Kustainer close to its memory limit; the daemons' first backfill on top of that saturates it.
    Restart-Kustainer
}

# ---------------------------------------------------------------------------------------------
Step '5/9  Cargando empresas, fuentes y KPIs desde Portal/demo/packs'
$packs = Get-ChildItem $packsDir -Directory | Where-Object { Test-Path (Join-Path $_.FullName 'profile.json') }
foreach ($dir in $packs) {
    $errors = @()
    $profileText = [IO.File]::ReadAllText((Join-Path $dir.FullName 'profile.json'))
    $profile = $profileText | ConvertFrom-Json
    $pk = $profile.profileKey
    # Orden de onboarding: perfil sin alias -> fuentes del perfil -> perfil completo.
    $draft = $profileText | ConvertFrom-Json
    foreach ($s in $draft.services) { $s.telemetrySourceAliases = @() }
    $e = Try-Api 'PUT' "$Fn/api/settings/company-profiles/$pk" $draft; if ($e) { $errors += "perfil (borrador): $($e.Message)" }
    foreach ($s in ([IO.File]::ReadAllText((Join-Path $dir.FullName 'sources.json')) | ConvertFrom-Json)) {
        $row = [ordered]@{ alias = $s.alias; environment = $s.environment; resourceId = $s.resourceId; appId = $s.appId; enabled = $true; notes = $s.notes; profileKey = $s.ownerProfileKey; changedBy = 'demo-setup' }
        $e = Try-Api 'PUT' "$Fn/api/settings/telemetry-sources" $row
        if ($e -and $e.Code -eq 409) { $row.Remove('profileKey'); $row['transferOwnerTo'] = $s.ownerProfileKey; $e = Try-Api 'PUT' "$Fn/api/settings/telemetry-sources" $row }
        if ($e) { $errors += $e.Message }
    }
    $e = Try-Api 'PUT' "$Fn/api/settings/company-profiles/$pk" $profile; if ($e) { $errors += "perfil: $($e.Message)" }
    $created = 0; $existing = 0; $retry = @()
    foreach ($k in ([IO.File]::ReadAllText((Join-Path $dir.FullName 'kpis.json')) | ConvertFrom-Json)) {
        if ($k.PSObject.Properties.Name -contains 'kqlText' -and $k.kqlText) {
            $key = $k.sourceKey -replace '^kql:', ''
            $e = Try-Api 'PUT' "$Fn/api/settings/kql/$key" ([ordered]@{ kqlText = $k.kqlText; description = $k.kqlDescription; enabled = $true; changedBy = 'demo-setup' })
            if ($e) { $errors += $e.Message }
            $k.PSObject.Properties.Remove('kqlText'); $k.PSObject.Properties.Remove('kqlDescription')
        }
        $k | Add-Member -NotePropertyName changedBy -NotePropertyValue 'demo-setup' -Force
        $e = Try-Api 'POST' "$Fn/api/settings/kpis" $k
        if (-not $e) { $created++ }
        elseif ($e.Code -eq 409 -or $e.Message -match 'already exists') { $existing++ }
        else { $retry += $k }
    }
    # The KPI save dry-runs its query; right after the KQL/source writes it can fail until the 30 s source cache expires.
    if ($retry.Count -gt 0) {
        Start-Sleep 40
        foreach ($k in $retry) {
            $e = Try-Api 'POST' "$Fn/api/settings/kpis" $k
            if (-not $e) { $created++ } elseif ($e.Message -match 'already exists') { $existing++ } else { $errors += "$($k.kpi)/$($k.domain): $($e.Message)" }
        }
    }
    if ($errors.Count -eq 0) { Ok ("{0,-14} perfil, fuentes y KPIs ({1} nuevos, {2} ya existían)" -f $pk, $created, $existing) }
    else { Warn ("{0}: {1} error(es). Primero: {2}" -f $pk, $errors.Count, $errors[0]) }
}

# ---------------------------------------------------------------------------------------------
Step '6/9  Memory Bank (RCAs de cada empresa)'
if ($SkipCorpus) { Ok 'Omitido (-SkipCorpus)' }
elseif ([string]::IsNullOrWhiteSpace((Get-EnvValue 'Ai__GitHubToken'))) { Warn 'Sin token del LLM no se pueden crear embeddings: Memory Bank omitido.' }
else {
    foreach ($dir in $packs) {
        $pk = ($dir | ForEach-Object { ([IO.File]::ReadAllText((Join-Path $_.FullName 'profile.json')) | ConvertFrom-Json).profileKey })
        $source = Join-Path $dir.FullName 'corpus'
        if (-not (Test-Path $source)) { continue }
        # Functions lee el corpus de docs/RCAS-md (montado en /corpus): se copia ahí, bajo demo-packs/<empresa>.
        $target = Join-Path $corpusRoot "demo-packs\$($dir.Name)"
        New-Item -ItemType Directory -Force -Path $target | Out-Null
        Copy-Item (Join-Path $source '*') $target -Recurse -Force
        try {
            $r = Invoke-Api 'POST' "$Fn/api/knowledge/import?profileKey=$pk&subPath=demo-packs/$($dir.Name)&max=500" $null 1800
            Ok ("{0,-14} {1}" -f $pk, (($r | ConvertTo-Json -Compress -Depth 3) -replace '(.{140}).+', '$1...'))
        } catch { Warn "$pk : import falló: $($_.Exception.Message) $($_.ErrorDetails.Message)" }
    }
    if ($IncludeArriviaCorpus) {
        $real = Get-ChildItem $corpusRoot -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -ne 'demo-packs' }
        foreach ($folder in $real) {
            try { Invoke-Api 'POST' "$Fn/api/knowledge/import?profileKey=arrivia&subPath=$([uri]::EscapeDataString($folder.Name))&max=500" $null 3600 | Out-Null; Ok "arrivia  corpus real: $($folder.Name)" }
            catch { Warn "arrivia corpus $($folder.Name): $($_.Exception.Message)" }
        }
    }
}

# ---------------------------------------------------------------------------------------------
Step '7/9  KPIs "demo 1m" (detección en ~2 min)'
$created = Invoke-Sql @"
WITH src AS (
  SELECT d.*, ROW_NUMBER() OVER (PARTITION BY d.ProfileKey, d.Kpi, d.Domain ORDER BY d.BucketSizeSec, d.KpiDefinitionId) rn
  FROM monitoring.KpiDefinition d
  WHERE d.Enabled=1 AND d.Pillar='Application' AND d.Kpi IN ('Latency','Exceptions') AND ISNULL(d.Stage,'')=''
    AND d.SourceKey IN ('appinsights:latency','appinsights:exceptions')
    AND d.ProfileKey IN ('arrivia','banco','kinectify','mercadolibre'))
INSERT INTO monitoring.KpiDefinition (Pillar, Kpi, Domain, Stage, SourceKey, Direction, Aggregation, EvaluationFrequencySec, BucketSizeSec, LookbackWindowSec, SampleSize, MinObservationsPerBucket, DetectionMethod, MethodParamsJson, ConsecutiveCount, CooldownMinutes, Enabled, UpdatedAt, UpdatedBy, ProfileKey, Grain, MaxEntitiesPerBucket)
SELECT s.Pillar, s.Kpi + N' · demo 1m', s.Domain, s.Stage, s.SourceKey, 'Increase',
  CASE WHEN s.Kpi='Latency' THEN 'P95' ELSE 'Count' END,
  60, 60, 43200, s.SampleSize, CASE WHEN s.Kpi='Latency' THEN 5 ELSE 0 END,
  'StaticThreshold', '{"threshold":1000000000}', 1, 10, 1, SYSUTCDATETIME(), 'demo-fast-lane', s.ProfileKey, 'domain', s.MaxEntitiesPerBucket
FROM src s
WHERE s.rn=1 AND NOT EXISTS (SELECT 1 FROM monitoring.KpiDefinition x WHERE x.ProfileKey=s.ProfileKey AND x.Kpi=s.Kpi + N' · demo 1m' AND x.Domain=s.Domain AND x.Grain='domain');
UPDATE monitoring.KpiDefinition SET LookbackWindowSec=43200 WHERE UpdatedBy='demo-fast-lane' AND LookbackWindowSec > 43200;
SELECT COUNT(*) FROM monitoring.KpiDefinition WHERE UpdatedBy='demo-fast-lane';
"@
$fastCount = [int]($created | Select-Object -Last 1)
if ($fastCount -eq 0) { Fail 'No se pudo crear ningún KPI "demo 1m" (¿se cargaron los KPIs de los paquetes?).' }
Ok "$fastCount KPIs 'demo 1m' presentes"

Write-Host "   Esperando que los daemons carguen 12 h de historia a 1 minuto para calibrar (hasta $CalibrationTimeoutMinutes min)..."
$deadline = (Get-Date).AddMinutes($CalibrationTimeoutMinutes)
do {
    Start-Sleep 30
    $pending = [int](Invoke-Sql @"
SELECT COUNT(*) FROM monitoring.KpiDefinition d
WHERE d.UpdatedBy='demo-fast-lane'
  AND (SELECT COUNT(*) FROM monitoring.KpiBucket b WHERE b.KpiDefinitionId=d.KpiDefinitionId) < 600;
"@ | Select-Object -Last 1)
    $kustoGb = Get-KustainerGb
    Write-Host ("   {0}  KPIs pendientes: {1} de {2}   (Kustainer {3:N1} GB)" -f (Get-Date).ToString('HH:mm:ss'), $pending, $fastCount, $kustoGb)
    if ($kustoGb -gt 8) { Restart-Kustainer }
} while ($pending -gt 0 -and (Get-Date) -lt $deadline)
if ($pending -gt 0) { Warn "$pending KPIs no terminaron su historia: se calibran con lo que hay. Volvé a ejecutar con -SkipBuild -SkipReset -SkipCorpus para recalibrar." }

# Umbral = máximo de las últimas 12 h (sin incidentes en vivo) x1,2 para latencia, +2 (mínimo 4) para excepciones.
$calibrated = Invoke-Sql @"
DECLARE @to datetimeoffset = DATEADD(minute, -3, SYSUTCDATETIME());
WITH v AS (
 SELECT d.KpiDefinitionId id, d.Aggregation agg, CASE WHEN d.Aggregation='P95' THEN b.P95 ELSE b.TotalCount END val
 FROM monitoring.KpiDefinition d JOIN monitoring.KpiBucket b ON b.KpiDefinitionId=d.KpiDefinitionId
 WHERE d.UpdatedBy='demo-fast-lane' AND b.BucketStart < @to AND b.BucketStart >= DATEADD(hour, -12, @to)
   AND (CASE WHEN d.Aggregation='P95' THEN b.P95 ELSE b.TotalCount END) IS NOT NULL),
t AS (SELECT id, CASE WHEN MAX(agg)='P95' THEN CEILING(MAX(val)*1.2) ELSE CASE WHEN MAX(val)+2 < 4 THEN 4 ELSE MAX(val)+2 END END thr FROM v GROUP BY id HAVING COUNT(*) > 60)
UPDATE d SET MethodParamsJson = '{"threshold":' + CAST(CAST(t.thr AS bigint) AS varchar(20)) + '}', UpdatedAt=SYSUTCDATETIME()
FROM monitoring.KpiDefinition d JOIN t ON t.id=d.KpiDefinitionId;
SELECT COUNT(*) FROM monitoring.KpiDefinition WHERE UpdatedBy='demo-fast-lane' AND MethodParamsJson <> '{"threshold":1000000000}';
"@
Ok "$($calibrated | Select-Object -Last 1) de $fastCount KPIs 'demo 1m' calibrados"

# ---------------------------------------------------------------------------------------------
Step '8/9  Compañía activa, monos y daemons'
Invoke-Api 'PUT' "$Fn/api/settings/company-profiles-active" @{ profileKey = $ActiveCompany; updatedBy = 'demo-setup' } | Out-Null
Ok "Compañía activa: $ActiveCompany"
Invoke-Sql "UPDATE monitoring.AgentWorkQueue SET Status='abandoned' WHERE Status='queued';" | Out-Null
Set-Monkeys $false
Ok 'Monos activos; cola de trabajo vacía'
if ($script:restartQa) { Native { docker start jm-functions-qa 2>&1 } | Out-Null; Ok 'jm-functions-qa vuelto a levantar (estaba corriendo antes)' }

# ---------------------------------------------------------------------------------------------
Step '9/9  Verificación'
& (Join-Path $PSScriptRoot 'demo-incidents.ps1') -Check | Out-Host
$summary = Invoke-Sql @"
SELECT p.ProfileKey + '|' + CAST((SELECT COUNT(*) FROM monitoring.KpiDefinition k WHERE k.ProfileKey=p.ProfileKey AND k.Enabled=1) AS varchar) + '|' +
       CAST((SELECT COUNT(DISTINCT DocumentTitle) FROM monitoring.KnowledgeChunk c WHERE c.ProfileKey=p.ProfileKey) AS varchar)
FROM (VALUES ('arrivia'),('banco'),('kinectify'),('mercadolibre')) p(ProfileKey);
"@
Write-Host ''
Write-Host '   Empresa         KPIs   Docs Memory Bank'
foreach ($line in $summary) { $c = "$line".Split('|'); if ($c.Count -ge 3) { Write-Host ("   {0,-15} {1,4}   {2,4}" -f $c[0], $c[1], $c[2]) } }
$minutes = [math]::Round(((Get-Date) - $started).TotalMinutes, 1)
Write-Host ''
Write-Host "Listo en $minutes min. Portal: $Web" -ForegroundColor Green
Write-Host 'Para la demo:' -ForegroundColor Green
Write-Host '  1. 25 min antes:   docker restart jm-kustainer   (libera memoria; los datos persisten)'
Write-Host '  2. 15 min antes:   .\demo-incidents.ps1 -All -Minutes 90'
Write-Host '  3. 10 min antes:   .\demo-incidents.ps1 -Detections   (las 4 empresas en [OK])'
Write-Host '  4. Al terminar:    .\demo-incidents.ps1 -Stop'
