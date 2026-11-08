<#
.SYNOPSIS
  Lanza problemas (escenarios de incidente) en el Azure falso para la demo del Agentic Operations Portal.

.DESCRIPTION
  Sin parámetros abre un menú: elegís empresa, elegís problema, y el script
    1) cambia la compañía activa del portal (http://localhost:5080) a esa empresa,
    2) arranca el escenario en el generador de telemetría (http://localhost:8087),
    3) te dice qué mirar en el portal y en cuánto tiempo debería aparecer.

  También se puede usar sin menú:
    .\demo-incidents.ps1 -List
    .\demo-incidents.ps1 -Company retail -Scenario payment-gateway-outage
    .\demo-incidents.ps1 -Company bank -Scenario ach-outage -Minutes 30 -NoSwitch
    .\demo-incidents.ps1 -Status
    .\demo-incidents.ps1 -Stop                 # detiene todos los escenarios
    .\demo-incidents.ps1 -Stop -Company aml    # detiene los de una empresa
    .\demo-incidents.ps1 -Check                # verifica que todo esté arriba
    .\demo-incidents.ps1 -All                  # lanza un incidente destacado en LAS 4 empresas (180 min)
    .\demo-incidents.ps1 -All -Minutes 240     # idem, con otra duración
    .\demo-incidents.ps1 -Detections           # anomalías detectadas por empresa (últimas 3 h)

  Recomendado: correr -All 45-60 min ANTES de la demo. Así, al empezar, cada empresa ya tiene
  anomalías, señales y triages abiertos, y el problema sigue activo durante toda la demo.

  Empresas (clave del paquete -> compañía del portal):
    tourism -> arrivia       (Arrivia (demo))
    bank    -> banco         (Banco Andino (demo))
    aml     -> kinectify     (Kinectify (demo))
    retail  -> mercadolibre  (Mercado Libre (demo))

  Solo local. No toca el Azure real, no borra datos, no hace reset.
#>
[CmdletBinding()]
param(
    [ValidateSet('tourism', 'bank', 'aml', 'retail')][string]$Company,
    [string]$Scenario,
    [int]$Minutes = 0,
    [switch]$List,
    [switch]$Status,
    [switch]$Stop,
    [switch]$Check,
    [switch]$NoSwitch,
    [switch]$All,
    [switch]$Detections,
    [string]$GeneratorUrl = 'http://localhost:8087',
    [string]$FunctionsUrl = 'http://localhost:8081',
    [string]$PortalUrl = 'http://localhost:5080',
    [string]$FakeAzureUrl = 'http://localhost:8086'
)

$ErrorActionPreference = 'Stop'
$settingsSecret = $env:Monitoring__SettingsWriteSecret
if (-not $settingsSecret) { $settingsSecret = 'Jm_LocalDev_SettingsSecret_1!' }

$companies = [ordered]@{
    tourism = @{ Profile = 'arrivia';      Name = 'Arrivia (demo)';       Note = '' }
    bank    = @{ Profile = 'banco';        Name = 'Banco Andino (demo)';  Note = 'Mejor entre 09:00 y 20:30 hora local de la empresa (UTC-5): de noche hay poco tráfico.' }
    aml     = @{ Profile = 'kinectify';    Name = 'Kinectify (demo)';     Note = '' }
    retail  = @{ Profile = 'mercadolibre'; Name = 'Mercado Libre (demo)'; Note = '' }
}

# Tipo: Incidente (debe alertar), Negocio (caída de negocio sin error técnico), Pico (legítimo: NO debe alertar).
# Minutes = duración recomendada. Watch = qué mirar en el portal. Eta = cuándo debería aparecer.
$catalog = @{
    tourism = [ordered]@{
        'cruise-pool-exhaustion'   = @{ Type = 'Incidente'; Minutes = 30; Title = 'Pool de conexiones de Cruise Core agotado (Total Price > 100 s)'; Watch = 'Platform Health: Latency P95 y Exceptions de Cruises. Daemons: anomalía y señal a Triage. Inspector: causa = pool de conexiones.'; Eta = '~10-15 min' }
        'air-search-outage'        = @{ Type = 'Incidente'; Minutes = 30; Title = 'Caída de la búsqueda aérea (proveedor de tarifas caído)'; Watch = 'Latency y Exceptions de Air, Exceptions de Air por club, KPI de negocio Flight bookings.'; Eta = '~10-15 min (negocio ~15-25)' }
        'hotel-prebook-exceptions' = @{ Type = 'Incidente'; Minutes = 30; Title = 'Tormenta de excepciones en Hotel PreBook tras un deploy'; Watch = 'Exceptions de Hotels (etapa PreBook), Hotel pre-books.'; Eta = '~10 min' }
        'club-degraded'            = @{ Type = 'Incidente'; Minutes = 30; Title = 'Un solo club degradado (club 9002)'; Watch = 'Exceptions por club: solo nombra al club 9002, en al menos dos verticales. Scouts priorizan ese club.'; Eta = '~10-20 min' }
        'partner-bookings-failing' = @{ Type = 'Incidente'; Minutes = 30; Title = 'Falla el partner de distribución (reservas)'; Watch = 'Exceptions de Shared, Hotel and car bookings, Booking value.'; Eta = '~10-20 min' }
        'payment-declines'         = @{ Type = 'Incidente'; Minutes = 30; Title = 'Errores de autorización en la pasarela de pago'; Watch = 'Exceptions de Shared Booking Services.'; Eta = '~10-15 min' }
        'silent-bookings-drop'     = @{ Type = 'Negocio';   Minutes = 40; Title = 'Caída de reservas sin error técnico'; Watch = 'Solo los 4 KPIs de negocio (bookings) bajan; ningún KPI técnico dispara.'; Eta = '~15-25 min' }
        'cluster-node-loss'        = @{ Type = 'Incidente'; Minutes = 30; Title = 'Pérdida de un nodo del cluster de Cruise API'; Watch = 'Latency P95 y Exceptions de Cruises.'; Eta = '~10-25 min' }
        'holiday-sale-peak'        = @{ Type = 'Pico';      Minutes = 90; Title = 'Pico de temporada (tráfico x2, legítimo)'; Watch = 'NADA debe alertar: mostrá que el tráfico sube y la plataforma no genera falsas alarmas.'; Eta = 'no debe alertar' }
    }
    bank = [ordered]@{
        'core-gw-slow-deploy'        = @{ Type = 'Incidente'; Minutes = 30; Title = 'Core-gateway lento tras el despliegue 4.18'; Watch = 'Latency de core/web/móvil/pagos, Exceptions de core, latencia por sucursal.'; Eta = '~10 min (sucursal hasta ~55)' }
        'mobile-transfer-exceptions' = @{ Type = 'Incidente'; Minutes = 30; Title = 'Tormenta de excepciones en transferencias móviles'; Watch = 'Exceptions de banca-móvil.'; Eta = '~10 min' }
        'ach-outage'                 = @{ Type = 'Incidente'; Minutes = 40; Title = 'Caída de la cámara de compensación ACH'; Watch = 'Fallas del partner ach-clearing -> Triage, Exceptions de pagos, Rechazo interbancario (%).'; Eta = '~10-30 min' }
        'branch-degraded'            = @{ Type = 'Incidente'; Minutes = 40; Title = 'Sucursal B-003 degradada'; Watch = 'Latency y Exceptions por sucursal: solo B-003 (web y móvil). Ninguna otra sucursal dispara.'; Eta = '~10-55 min' }
        'card-network-degraded'      = @{ Type = 'Incidente'; Minutes = 30; Title = 'Red de tarjetas degradada'; Watch = 'Fallas del partner card-network -> Triage, Exceptions de banca-móvil.'; Eta = '~10-30 min' }
        'otp-delays'                 = @{ Type = 'Incidente'; Minutes = 30; Title = 'Demora del proveedor SMS/OTP'; Watch = 'Fallas del partner otp-sms, Latency y Exceptions de web y móvil, Logins exitosos baja.'; Eta = '~10-30 min' }
        'transfers-drop'             = @{ Type = 'Negocio';   Minutes = 40; Title = 'Caída silenciosa de transferencias'; Watch = 'Solo Transferencias completadas (negocio); ningún KPI técnico.'; Eta = '~15-30 min' }
        'payday-peak'                = @{ Type = 'Pico';      Minutes = 90; Title = 'Pico de quincena / fin de mes (legítimo)'; Watch = 'NADA debe alertar.'; Eta = 'no debe alertar' }
    }
    aml = [ordered]@{
        'sanctions-provider-outage' = @{ Type = 'Incidente'; Minutes = 30; Title = 'Caída del proveedor de listas de sanciones'; Watch = 'Latency y Exceptions de screening, Exceptions por cliente, Transactions screened baja.'; Eta = '<= ~15 min' }
        'scoring-model-deploy'      = @{ Type = 'Incidente'; Minutes = 30; Title = 'Latencia del scoring tras deploy del modelo'; Watch = 'Latency P95 de screening.'; Eta = '~10-15 min' }
        'alert-queue-backlog'       = @{ Type = 'Incidente'; Minutes = 30; Title = 'Cola de alertas atascada (tormenta de excepciones)'; Watch = 'Exceptions y Latency de alerts, Alerts raised baja.'; Eta = '~10-15 min' }
        'tenant-degraded'           = @{ Type = 'Incidente'; Minutes = 30; Title = 'Un cliente (tenant T-003) degradado'; Watch = 'Median latency por cliente: solo T-003. Ningún otro cliente dispara.'; Eta = 'usualmente 7-14 min (hasta ~30)' }
        'idv-provider-degraded'     = @{ Type = 'Incidente'; Minutes = 30; Title = 'Proveedor de verificación de identidad degradado'; Watch = 'Clients approved baja; usualmente Latency y Exceptions de onboarding. Mejor en horario hábil.'; Eta = '~10-20 min' }
        'screening-volume-drop'     = @{ Type = 'Negocio';   Minutes = 40; Title = 'Caída de transacciones analizadas'; Watch = 'Solo Transactions screened (negocio).'; Eta = '~15-25 min' }
        'month-end-regulatory-peak' = @{ Type = 'Pico';      Minutes = 90; Title = 'Re-screening regulatorio de fin de mes (legítimo)'; Watch = 'NADA debe alertar.'; Eta = 'no debe alertar' }
    }
    retail = [ordered]@{
        'search-latency-mlm'         = @{ Type = 'Incidente'; Minutes = 30; Title = 'Búsqueda lenta solo en México (MLM)'; Watch = 'Latency de search (dominio); el sitio afectado es MLM.'; Eta = '~10-15 min' }
        'payment-gateway-outage'     = @{ Type = 'Incidente'; Minutes = 20; Title = 'Caída de la pasarela de pago'; Watch = 'Exceptions de payments, Payments approved baja.'; Eta = '~10-15 min' }
        'checkout-deploy-exceptions' = @{ Type = 'Incidente'; Minutes = 25; Title = 'Tormenta de excepciones en checkout tras deploy'; Watch = 'Exceptions de checkout.'; Eta = '~10-15 min' }
        'carrier-b-timeouts'         = @{ Type = 'Incidente'; Minutes = 30; Title = 'Timeouts del transportista B'; Watch = 'Exceptions de shipping, Shipping labels baja.'; Eta = '~10-20 min' }
        'orders-drop'                = @{ Type = 'Negocio';   Minutes = 45; Title = 'Caída silenciosa de órdenes y GMV'; Watch = 'Orders placed, GMV, Add to cart, Payments approved, Shipping labels (negocio).'; Eta = '~15-25 min' }
        'hot-sale'                   = @{ Type = 'Pico';      Minutes = 120; Title = 'Hot Sale (pico de tráfico legítimo)'; Watch = 'NADA debe alertar.'; Eta = 'no debe alertar' }
    }
}

# Incidente destacado por empresa para -All: detección rápida y visible en varias pestañas.
$featured = [ordered]@{
    tourism = 'air-search-outage'
    bank    = 'core-gw-slow-deploy'
    aml     = 'sanctions-provider-outage'
    retail  = 'payment-gateway-outage'
}

function Write-Title([string]$text) { Write-Host ''; Write-Host $text -ForegroundColor Cyan }

function Invoke-Json([string]$method, [string]$url, $body = $null, [hashtable]$headers = @{}) {
    $request = @{ Method = $method; Uri = $url; Headers = $headers; TimeoutSec = 30 }
    if ($null -ne $body) { $request.ContentType = 'application/json'; $request.Body = ($body | ConvertTo-Json -Compress) }
    Invoke-RestMethod @request
}

function Test-Stack {
    $ok = $true
    $checks = @(
        @{ Name = 'Generador de telemetría'; Url = "$GeneratorUrl/health" },
        @{ Name = 'Azure falso (App Insights)'; Url = "$FakeAzureUrl/health" },
        @{ Name = 'Portal'; Url = "$PortalUrl/api/demo-mode" }
    )
    foreach ($c in $checks) {
        try { Invoke-RestMethod $c.Url -TimeoutSec 10 | Out-Null; Write-Host "  [OK]    $($c.Name)  ($($c.Url))" -ForegroundColor Green }
        catch { Write-Host "  [FALLA] $($c.Name)  ($($c.Url)) - $($_.Exception.Message)" -ForegroundColor Red; $ok = $false }
    }
    try {
        $h = Invoke-RestMethod "$GeneratorUrl/health" -TimeoutSec 10
        Write-Host "  Feeder: $($h.feeder)   último dato: $($h.lastFedAt)"
    } catch { }
    return $ok
}

function Show-List {
    foreach ($key in $companies.Keys) {
        Write-Title "$key -> $($companies[$key].Name)"
        if ($companies[$key].Note) { Write-Host "  Nota: $($companies[$key].Note)" -ForegroundColor DarkYellow }
        foreach ($s in $catalog[$key].Keys) {
            $e = $catalog[$key][$s]
            $color = if ($e.Type -eq 'Pico') { 'DarkGray' } elseif ($e.Type -eq 'Negocio') { 'Yellow' } else { 'White' }
            Write-Host ("  {0,-28} [{1,-9}] {2}" -f $s, $e.Type, $e.Title) -ForegroundColor $color
        }
    }
}

function Show-Status {
    Write-Title 'Escenarios activos'
    $active = @(Invoke-Json GET "$GeneratorUrl/api/scenarios/active" | ForEach-Object { $_ } | Where-Object { $null -ne $_ -and $_.scenarioKey })
    if ($active.Count -eq 0) { Write-Host '  (ninguno)' } else {
        foreach ($a in $active) {
            $ends = if ($a.endsAt) { ([datetimeoffset]$a.endsAt).LocalDateTime.ToString('HH:mm') } else { '-' }
            Write-Host ("  {0,-8} {1,-28} desde {2}  hasta {3}" -f $a.companyKey, $a.scenarioKey, ([datetimeoffset]$a.startedAt).LocalDateTime.ToString('HH:mm'), $ends)
        }
    }
    try {
        $act = Invoke-Json GET "$FunctionsUrl/api/settings/company-profiles-active"
        Write-Host "  Compañía activa en el portal: $($act.profileKey)"
    } catch { }
}

function Set-ActiveCompany([string]$profileKey) {
    try {
        Invoke-Json PUT "$FunctionsUrl/api/settings/company-profiles-active" @{ profileKey = $profileKey; updatedBy = 'demo-incidents' } @{ 'X-Settings-Write-Secret' = $settingsSecret } | Out-Null
        Write-Host "  Portal: compañía activa = $profileKey (recargá el navegador si ya estaba abierto)" -ForegroundColor Green
    } catch { Write-Host "  No pude cambiar la compañía activa: $($_.Exception.Message). Cambiala a mano en el selector Company." -ForegroundColor Yellow }
}

function Start-Incident([string]$companyKey, [string]$scenarioKey, [int]$minutes) {
    if (-not $catalog[$companyKey].Contains($scenarioKey)) { throw "La empresa '$companyKey' no tiene el escenario '$scenarioKey'. Usá -List." }
    $e = $catalog[$companyKey][$scenarioKey]
    if ($minutes -le 0) { $minutes = $e.Minutes }
    if (-not $NoSwitch) { Set-ActiveCompany $companies[$companyKey].Profile }
    if ($e.Type -eq 'Incidente') { Reset-FastLane @($companies[$companyKey].Profile) }
    Invoke-Json POST "$GeneratorUrl/api/scenarios/$companyKey/$scenarioKey/start" @{ durationMinutes = $minutes } | Out-Null
    $ends = (Get-Date).AddMinutes($minutes).ToString('HH:mm')
    Write-Title "LANZADO: $($companies[$companyKey].Name) -> $($e.Title)"
    Write-Host "  Escenario : $scenarioKey  [$($e.Type)]  durante $minutes min (hasta ~$ends)"
    Write-Host "  Mirá      : $($e.Watch)"
    if ($e.Type -eq 'Incidente') { Write-Host "  Aparece en: ~2-3 min (KPIs 'demo 1m'); KPIs calibrados: $($e.Eta)" }
    else { Write-Host "  Aparece en: $($e.Eta)" }
    if ($companies[$companyKey].Note) { Write-Host "  Nota      : $($companies[$companyKey].Note)" -ForegroundColor DarkYellow }
    Write-Host "  Portal    : $PortalUrl  (pestañas Daemons, Platform Health, Simian Army; Overview para el resumen)"
    Write-Host "  Detener   : .\demo-incidents.ps1 -Stop -Company $companyKey"
}

function Stop-Incidents([string]$companyKey) {
    $body = if ($companyKey) { @{ companyKey = $companyKey } } else { @{} }
    Invoke-Json POST "$GeneratorUrl/api/scenarios/stop" $body | Out-Null
    if ($companyKey) { Write-Host "Escenarios de '$companyKey' detenidos." -ForegroundColor Green } else { Write-Host 'Todos los escenarios detenidos.' -ForegroundColor Green }
}

# KPIs "· demo 1m" (UpdatedBy='demo-fast-lane'): buckets de 1 minuto, umbral fijo, 1 bucket basta.
# Limpiar su estado al lanzar quita el cooldown de un ensayo anterior: la anomalía aparece en ~2 min.
function Reset-FastLane([string[]]$profileKeys) {
    $list = ($profileKeys | ForEach-Object { "'$_'" }) -join ','
    $q = "SET NOCOUNT ON; DELETE s FROM monitoring.KpiDetectionState s JOIN monitoring.KpiDefinition d ON d.KpiDefinitionId = s.KpiDefinitionId WHERE d.UpdatedBy = 'demo-fast-lane' AND d.ProfileKey IN ($list)"
    try {
        docker exec jm-sql /opt/mssql-tools18/bin/sqlcmd -C -I -S localhost -U sa -P 'Jm_LocalDev_1!' -d platformDB -Q $q 2>$null | Out-Null
        Write-Host "  Detección rápida lista (KPIs 'demo 1m'): la anomalía aparece en ~2 min en Daemons y Simian Army." -ForegroundColor Green
    } catch { Write-Host "  No pude preparar la detección rápida (¿jm-sql arriba?). La detección normal tarda 10-25 min." -ForegroundColor Yellow }
}

function Start-All([int]$minutes) {
    if ($minutes -le 0) { $minutes = 180 }
    Write-Title "Lanzando un incidente en las 4 empresas durante $minutes min (hasta ~$((Get-Date).AddMinutes($minutes).ToString('HH:mm')))"
    Reset-FastLane @($featured.Keys | ForEach-Object { $companies[$_].Profile })
    foreach ($companyKey in $featured.Keys) {
        $scenarioKey = $featured[$companyKey]
        $e = $catalog[$companyKey][$scenarioKey]
        Invoke-Json POST "$GeneratorUrl/api/scenarios/$companyKey/$scenarioKey/start" @{ durationMinutes = $minutes } | Out-Null
        Write-Host ("  [OK] {0,-22} {1}" -f $companies[$companyKey].Name, $e.Title) -ForegroundColor Green
        Write-Host ("       Mirá: {0}" -f $e.Watch)
    }
    Write-Host ''
    Write-Host '  La compañía activa del portal NO se cambió: usá el selector Company durante la demo.'
    Write-Host '  En ~2-3 min: Daemons y Simian Army muestran las anomalías e investigaciones. Verificá con:  .\demo-incidents.ps1 -Detections'
}

function Show-Detections {
    Write-Title 'Anomalías detectadas por empresa (últimos 30 min)'
    $q = "SET NOCOUNT ON; SELECT d.ProfileKey, COUNT(*), CONVERT(varchar(19), MAX(a.DetectedAt), 126) FROM monitoring.KpiAnomaly a JOIN monitoring.KpiDefinition d ON d.KpiDefinitionId = a.KpiDefinitionId WHERE a.DetectedAt > DATEADD(minute, -30, SYSUTCDATETIME()) GROUP BY d.ProfileKey"
    try {
        $rows = docker exec jm-sql /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P 'Jm_LocalDev_1!' -d platformDB -h -1 -W -s '|' -Q $q 2>$null
        $found = @{}
        foreach ($r in $rows) { $c = "$r".Trim() -split '\|'; if ($c.Count -ge 3) { $found[$c[0]] = $c } }
        foreach ($k in $companies.Keys) {
            $profile = $companies[$k].Profile
            if ($found.ContainsKey($profile)) { Write-Host ("  [OK]        {0,-22} {1} anomalía(s), última {2}" -f $companies[$k].Name, $found[$profile][1], ([datetime]::SpecifyKind([datetime]$found[$profile][2], 'Utc')).ToLocalTime().ToString('HH:mm')) -ForegroundColor Green }
            else { Write-Host ("  [ESPERANDO] {0,-22} todavía sin anomalías" -f $companies[$k].Name) -ForegroundColor Yellow }
        }
    } catch { Write-Host "  No pude consultar la base (¿contenedor jm-sql arriba?): $($_.Exception.Message)" -ForegroundColor Yellow }
}

function Read-Choice([string]$prompt, [string[]]$options) {
    for ($i = 0; $i -lt $options.Count; $i++) { Write-Host ("  {0,2}) {1}" -f ($i + 1), $options[$i]) }
    while ($true) {
        $answer = Read-Host $prompt
        if ($answer -match '^[qQ]$') { return -1 }
        $n = 0
        if ([int]::TryParse($answer, [ref]$n) -and $n -ge 1 -and $n -le $options.Count) { return $n - 1 }
        Write-Host '  Opción inválida.' -ForegroundColor Yellow
    }
}

function Start-Menu {
    while ($true) {
        Write-Title '=== Demo: lanzar problemas ==='
        $mainOptions = @()
        foreach ($k in $companies.Keys) { $mainOptions += "$($companies[$k].Name)   [$k]" }
        $mainOptions += 'LANZAR EN LAS 4 EMPRESAS (un incidente destacado c/u, 180 min)'
        $mainOptions += 'Ver escenarios activos'
        $mainOptions += 'Ver anomalías detectadas por empresa'
        $mainOptions += 'Detener TODOS los escenarios'
        $mainOptions += 'Verificar que todo esté arriba'
        $choice = Read-Choice 'Elegí (número, q para salir)' $mainOptions
        if ($choice -lt 0) { return }
        $keys = @($companies.Keys)
        if ($choice -eq $keys.Count) { Start-All 0; continue }
        if ($choice -eq $keys.Count + 1) { Show-Status; continue }
        if ($choice -eq $keys.Count + 2) { Show-Detections; continue }
        if ($choice -eq $keys.Count + 3) { Stop-Incidents ''; continue }
        if ($choice -eq $keys.Count + 4) { Test-Stack | Out-Null; continue }

        $companyKey = $keys[$choice]
        Write-Title "$($companies[$companyKey].Name)"
        if ($companies[$companyKey].Note) { Write-Host "  Nota: $($companies[$companyKey].Note)" -ForegroundColor DarkYellow }
        $scenarioKeys = @($catalog[$companyKey].Keys)
        $labels = @($scenarioKeys | ForEach-Object { $e = $catalog[$companyKey][$_]; "[{0,-9}] {1}" -f $e.Type, $e.Title })
        $labels += 'Detener los escenarios de esta empresa'
        $s = Read-Choice 'Elegí el problema (número, q para volver)' $labels
        if ($s -lt 0) { continue }
        if ($s -eq $scenarioKeys.Count) { Stop-Incidents $companyKey; continue }
        $minutesText = Read-Host "Duración en minutos (Enter = $($catalog[$companyKey][$scenarioKeys[$s]].Minutes))"
        $m = 0; [void][int]::TryParse($minutesText, [ref]$m)
        Start-Incident $companyKey $scenarioKeys[$s] $m
    }
}

if ($Check) { if (Test-Stack) { exit 0 } else { exit 1 } }
if ($List) { Show-List; exit 0 }
if ($Status) { Show-Status; exit 0 }
if ($Detections) { Show-Detections; exit 0 }
if ($All) { Start-All $Minutes; Show-Status; exit 0 }
if ($Stop) { Stop-Incidents $Company; exit 0 }
if ($Company -and $Scenario) { Start-Incident $Company $Scenario $Minutes; exit 0 }
if ($Company -or $Scenario) { Write-Host 'Usá -Company y -Scenario juntos, o ningún parámetro para el menú. -List muestra los escenarios.' -ForegroundColor Yellow; exit 1 }
Start-Menu
