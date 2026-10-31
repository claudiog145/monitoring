<#
.SYNOPSIS
    Read-only rehearsal for the multi-company demo, run by a human against the ALREADY-RUNNING
    local Docker stack.

.DESCRIPTION
    Proves, in the order a demo tells it, that the platform is genuinely company-independent:

      1. every read endpoint the demo touches answers 200
      2. more than one company profile is configured, and they speak DIFFERENT vocabularies
      3. the write gates are closed (no secret -> 401, invalid document -> 400, never 500)
      4. telemetry discovery answers for a registered alias, or says exactly what is missing
      5. a per-company summary table: labels, service count, step count, identity sources

    This script is IDEMPOTENT and NON-DESTRUCTIVE. It writes nothing that survives it:
      * every call is a GET, except two deliberate PUT probes that are SUPPOSED to be rejected
      * those probes use a throwaway key (-ProbeKey) so that even if a gate were open, no real
        company profile could be overwritten
      * step 3c then re-reads the profile list and FAILS LOUDLY if the probe key appears

    It does NOT trigger an agent run. No Scout, Inspector, Director or Latency Doctor run is
    started, no model is called, no LLM quota is spent, and no production query is issued on an
    agent's behalf. The only outbound query is telemetry discovery in step 4, which is read-only
    measurement and can be skipped with -SkipDiscovery. What a human should click to actually run
    the agents is printed at the end instead.

.PARAMETER SkipDiscovery
    Skips step 4. Discovery is read-only but it does issue a real query against the registered
    Application Insights resource; skip it if the room should see zero outbound traffic.

.PARAMETER IncludeDraft
    Opt-in. Also exercises POST /api/onboarding/draft, which persists nothing and calls no model
    but runs the same outbound discovery query. Off by default because a POST in a demo script
    should be a decision, not a default.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\e2e-multicompany-smoke.ps1

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\e2e-multicompany-smoke.ps1 -SkipDiscovery

.NOTES
    Environment rules this repository has learned the hard way, and which this script obeys:
      * curl.exe only. Invoke-WebRequest/Invoke-RestMethod hang behind the corporate proxy
        ("schannel: remote party requests renegotiation").
      * JSON bodies are written with UTF8Encoding($false) and passed as --data-binary "@file".
        Out-File -Encoding utf8 prepends a BOM on Windows PowerShell 5.1 and strict JSON parsers
        reject it with a 400 that looks like a server bug.
      * Responses are read back with Get-Content -Raw -Encoding UTF8, or correct characters show
        up as fake mojibake.
#>
[CmdletBinding()]
param(
    [string] $FunctionsBaseUrl    = 'http://127.0.0.1:8081',
    [string] $PortalUrl           = 'http://127.0.0.1:5080',
    [string] $InternalApiSecret   = 'Jm_LocalDev_InternalApiSecret_1!',
    [string] $SettingsWriteSecret = 'Jm_LocalDev_SettingsSecret_1!',
    [string] $ProbeKey            = 'zz-smoke-probe-do-not-create',
    [int]    $DiscoveryHours      = 1,
    [int]    $TimeoutSeconds      = 60,
    [switch] $SkipDiscovery,
    [switch] $IncludeDraft
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$FunctionsBaseUrl = $FunctionsBaseUrl.TrimEnd('/')
$PortalUrl        = $PortalUrl.TrimEnd('/')

$script:TempFiles = New-Object System.Collections.ArrayList
$script:Passed    = 0
$script:Failed    = 0

# -------------------------------------------------------------------------------------------
# Output
# -------------------------------------------------------------------------------------------

function Write-Section {
    param([string] $Title)
    Write-Host ''
    Write-Host ('=' * 78) -ForegroundColor DarkGray
    Write-Host $Title -ForegroundColor Cyan
    Write-Host ('=' * 78) -ForegroundColor DarkGray
}

function Write-Result {
    param(
        [string]  $Name,
        [bool]    $Ok,
        [string]  $Detail = ''
    )

    if ($Ok) {
        $script:Passed++
        Write-Host ('[PASS] ' + $Name) -ForegroundColor Green
    } else {
        $script:Failed++
        Write-Host ('[FAIL] ' + $Name) -ForegroundColor Red
    }

    if ($Detail) {
        foreach ($line in ($Detail -split "`r?`n")) {
            Write-Host ('       ' + $line) -ForegroundColor DarkGray
        }
    }
}

function Write-Note {
    param([string] $Text)
    Write-Host ('       ' + $Text) -ForegroundColor DarkGray
}

# -------------------------------------------------------------------------------------------
# HTTP, via curl.exe only
# -------------------------------------------------------------------------------------------

function New-TempFile {
    param([string] $Suffix = '.tmp')
    $path = Join-Path ([System.IO.Path]::GetTempPath()) ('jm-smoke-' + [Guid]::NewGuid().ToString('N') + $Suffix)
    [void] $script:TempFiles.Add($path)
    return $path
}

<#
    Writes a JSON body WITHOUT a byte order mark. Out-File/Set-Content -Encoding utf8 on Windows
    PowerShell 5.1 emits a BOM, and a strict JSON parser rejects the request with a 400 that is
    indistinguishable from a real validation failure - which would make step 3 lie.
#>
function Write-JsonBody {
    param([Parameter(Mandatory)][string] $Json)

    $path = New-TempFile -Suffix '.json'
    [System.IO.File]::WriteAllText($path, $Json, (New-Object System.Text.UTF8Encoding $false))
    return $path
}

<#
    One HTTP call. Returns @{ Status = <int>; Body = <string>; Failed = <bool>; Error = <string> }.
    The status code comes back on stdout via -w while the body goes to a file via -o, so a body
    that happens to contain digits can never be mistaken for the status.
#>
function Invoke-Api {
    param(
        [string]    $Path,
        [string]    $Method   = 'GET',
        [hashtable] $Headers  = @{},
        [string]    $BodyFile = $null
    )

    $bodyPath = New-TempFile -Suffix '.body'
    $errPath  = New-TempFile -Suffix '.err'

    $curlArgs = @(
        '-s', '-S',
        '--max-time', $TimeoutSeconds.ToString(),
        '-o', $bodyPath,
        '-w', '%{http_code}',
        '-X', $Method
    )

    foreach ($key in $Headers.Keys) {
        $curlArgs += @('-H', ('{0}: {1}' -f $key, $Headers[$key]))
    }

    if ($BodyFile) {
        $curlArgs += @('-H', 'Content-Type: application/json', '--data-binary', ('@' + $BodyFile))
    }

    $curlArgs += ($FunctionsBaseUrl + $Path)

    $stdout = & curl.exe @curlArgs 2> $errPath
    $status = 0
    if ($stdout) { [void][int]::TryParse(($stdout | Select-Object -Last 1).ToString().Trim(), [ref] $status) }

    $body = ''
    if (Test-Path -LiteralPath $bodyPath) { $body = Get-Content -LiteralPath $bodyPath -Raw -Encoding UTF8 }

    $err = ''
    if (Test-Path -LiteralPath $errPath) { $err = (Get-Content -LiteralPath $errPath -Raw -Encoding UTF8) }
    if ($null -eq $err) { $err = '' }

    return @{
        Status = $status
        Body   = if ($null -eq $body) { '' } else { $body }
        Failed = ($status -eq 0)
        Error  = $err.Trim()
    }
}

function ConvertFrom-ApiJson {
    param([string] $Body)
    if ([string]::IsNullOrWhiteSpace($Body)) { return $null }
    try { return ($Body | ConvertFrom-Json) } catch { return $null }
}

<#
    Pulls the "error" message out of a failure body so a FAIL line says what the server actually
    complained about rather than just repeating the status code.
#>
function Get-ApiError {
    param([string] $Body)
    $parsed = ConvertFrom-ApiJson -Body $Body
    if ($parsed -and ($parsed.PSObject.Properties.Name -contains 'error')) { return [string] $parsed.error }
    if ([string]::IsNullOrWhiteSpace($Body)) { return '(empty response body)' }
    $flat = ($Body -replace "`r?`n", ' ').Trim()
    if ($flat.Length -gt 200) { return $flat.Substring(0, 200) + '...' }
    return $flat
}

function Get-Prop {
    param($Object, [string] $Name, $Default = $null)
    if ($null -eq $Object) { return $Default }
    if ($Object.PSObject.Properties.Name -contains $Name) { return $Object.$Name }
    return $Default
}

# -------------------------------------------------------------------------------------------

try {
    Write-Host ''
    Write-Host 'Multi-company demo smoke test (read-only)' -ForegroundColor White
    Write-Host ("  Functions host : {0}" -f $FunctionsBaseUrl)
    Write-Host ("  Portal         : {0}" -f $PortalUrl)
    Write-Host  '  Agent runs     : NONE. This script starts no monkey and calls no model.'

    $internal = @{ 'X-Internal-Api-Secret' = $InternalApiSecret }
    $write    = @{ 'X-Settings-Write-Secret' = $SettingsWriteSecret }

    # =======================================================================================
    # STEP 1 - the read surface the demo depends on
    # =======================================================================================
    Write-Section 'STEP 1  Read endpoints answer 200'

    $reads = @(
        @{ Name = 'company profiles'; Path = '/api/settings/company-profiles' },
        @{ Name = 'telemetry sources'; Path = '/api/settings/telemetry-sources' },
        @{ Name = 'monkeys';           Path = '/api/monkeys' },
        @{ Name = 'scout packages';    Path = '/api/scout/packages' },
        @{ Name = 'scout runs';        Path = '/api/scout/runs' },
        @{ Name = 'inspector cases';   Path = '/api/inspector/cases' }
    )

    $responses = @{}
    foreach ($read in $reads) {
        $res = Invoke-Api -Path $read.Path -Headers $internal
        $responses[$read.Name] = $res

        if ($res.Failed) {
            Write-Result -Name ("GET {0}" -f $read.Path) -Ok $false `
                -Detail ("curl could not reach the host. Is the stack up?`n" + $res.Error)
        } else {
            Write-Result -Name ("GET {0} -> {1}" -f $read.Path, $res.Status) -Ok ($res.Status -eq 200) `
                -Detail $(if ($res.Status -ne 200) { Get-ApiError -Body $res.Body } else { '' })
        }
    }

    # =======================================================================================
    # STEP 2 - more than one company, and they do NOT share a vocabulary
    # =======================================================================================
    Write-Section 'STEP 2  More than one company, with different vocabularies'

    $profilesBody = $responses['company profiles'].Body
    $profilesDoc  = ConvertFrom-ApiJson -Body $profilesBody
    $profiles     = @(Get-Prop $profilesDoc 'profiles' @())

    Write-Result -Name ("{0} company profile(s) configured" -f $profiles.Count) -Ok ($profiles.Count -gt 1) `
        -Detail $(if ($profiles.Count -le 1) {
            "The whole point of the demo is that the platform is not hardwired to one customer. Seed a second profile (CreateMonitoringCompanyProfile.sql ships 'arrivia' and 'demo')."
        } else {
            'keys: ' + (($profiles | ForEach-Object { Get-Prop $_ 'profileKey' '(no key)' }) -join ', ')
        })

    if ($profiles.Count -gt 1) {
        $labelSignatures = @()
        foreach ($p in $profiles) {
            $labels = Get-Prop $p 'labels'
            $labelSignatures += (('{0}|{1}|{2}|{3}' -f `
                (Get-Prop $labels 'service' '?'), (Get-Prop $labels 'flow' '?'),
                (Get-Prop $labels 'step' '?'), (Get-Prop $labels 'entity' '?')).ToLowerInvariant())
        }

        $distinct = @($labelSignatures | Select-Object -Unique)
        Write-Result -Name ("label sets are distinct ({0} distinct of {1})" -f $distinct.Count, $labelSignatures.Count) `
            -Ok ($distinct.Count -eq $labelSignatures.Count) `
            -Detail $(if ($distinct.Count -ne $labelSignatures.Count) {
                "Two companies render with identical labels. Nothing on screen will prove the vocabulary comes from the row rather than from the code."
            } else { ($distinct -join '   /   ') })
    }

    # =======================================================================================
    # STEP 3 - the gates are closed
    # =======================================================================================
    Write-Section 'STEP 3  Write gates are closed (nothing below is expected to succeed)'

    Write-Note ("probe key '{0}' is used so that even an open gate could not overwrite a real company" -f $ProbeKey)

    # 3a - no secret at all must be refused before anything is parsed.
    $validLooking = @'
{
  "profileKey": "PROBE",
  "displayName": "Smoke probe",
  "labels": { "service": "Area", "flow": "Journey", "step": "Step", "entity": "Tenant", "owner": "Owner" },
  "services": [],
  "entityIdentitySources": [],
  "glossary": { "serviceNoun": "area", "flowNoun": "journey", "stepNoun": "step", "entityNoun": "tenant", "ownerNoun": "owner", "description": "smoke probe" }
}
'@
    $validLooking = $validLooking.Replace('PROBE', $ProbeKey)
    $noSecretBody = Write-JsonBody -Json $validLooking

    $unauth = Invoke-Api -Path ('/api/settings/company-profiles/' + $ProbeKey) -Method 'PUT' -BodyFile $noSecretBody
    Write-Result -Name ("PUT without X-Settings-Write-Secret -> {0} (want 401)" -f $unauth.Status) `
        -Ok ($unauth.Status -eq 401) `
        -Detail $(if ($unauth.Status -ne 401) {
            "An unauthenticated write to a company profile rewrites how every telemetry row is classified and which team is paged. Server said: " + (Get-ApiError -Body $unauth.Body)
        } else { '' })

    # 3b - an invalid document must be REFUSED by name, not crash. A 500 here means a malformed
    # profile reaches code that assumes it is well formed.
    $invalidBody = Write-JsonBody -Json '{}'
    $invalid = Invoke-Api -Path ('/api/settings/company-profiles/' + $ProbeKey) -Method 'PUT' -Headers $write -BodyFile $invalidBody

    $invalidOk = ($invalid.Status -eq 400)
    Write-Result -Name ("PUT an invalid profile document -> {0} (want 400)" -f $invalid.Status) -Ok $invalidOk `
        -Detail $(
            if ($invalid.Status -eq 500) { 'A 500 means the empty document got past the boundary check. It must be refused by name at the edge.' }
            elseif ($invalid.Status -ge 200 -and $invalid.Status -lt 300) { 'WORSE: the invalid document was ACCEPTED. See the cleanup warning below.' }
            elseif ($invalidOk) { 'refused: ' + (Get-ApiError -Body $invalid.Body) }
            else { Get-ApiError -Body $invalid.Body })

    # 3c - prove the probes really were non-destructive.
    $after = Invoke-Api -Path '/api/settings/company-profiles' -Headers $internal
    $afterKeys = @((ConvertFrom-ApiJson -Body $after.Body | ForEach-Object { Get-Prop $_ 'profiles' @() }) | ForEach-Object { Get-Prop $_ 'profileKey' '' })
    $probeLeaked = $afterKeys -contains $ProbeKey

    Write-Result -Name 'no probe profile was created (script was non-destructive)' -Ok (-not $probeLeaked) `
        -Detail $(if ($probeLeaked) {
            "A profile keyed '$ProbeKey' now exists and this script created it. DELETE IT before the demo."
        } else {
            ("{0} profile(s) present, unchanged: {1}" -f $afterKeys.Count, ($afterKeys -join ', '))
        })

    # =======================================================================================
    # STEP 4 - discovery against a registered alias
    # =======================================================================================
    Write-Section 'STEP 4  Telemetry discovery answers for a registered alias'

    $sourcesDoc = ConvertFrom-ApiJson -Body $responses['telemetry sources'].Body
    $sources    = @(Get-Prop $sourcesDoc 'sources' @())
    $enabled    = @($sources | Where-Object { [bool](Get-Prop $_ 'enabled' $true) })

    if ($SkipDiscovery) {
        Write-Note '-SkipDiscovery was passed; no outbound telemetry query was issued.'
    }
    elseif ($enabled.Count -eq 0) {
        Write-Result -Name 'a telemetry source is registered to discover against' -Ok $false `
            -Detail 'No enabled rows in monitoring.TelemetrySource. Register one in the portal under Settings > Telemetry sources, then re-run.'
    }
    else {
        $target      = $enabled[0]
        $alias       = [string](Get-Prop $target 'alias' '')
        $environment = [string](Get-Prop $target 'environment' 'production')
        $appId       = [string](Get-Prop $target 'appId' '')

        $query = '/api/onboarding/discover?alias={0}&environment={1}&hours={2}' -f `
            [uri]::EscapeDataString($alias), [uri]::EscapeDataString($environment), $DiscoveryHours

        $disc = Invoke-Api -Path $query -Headers $internal

        if ($disc.Status -eq 200) {
            $report = ConvertFrom-ApiJson -Body $disc.Body
            $detail = 'status={0}  rowsSampled={1}  dimensions={2}' -f `
                (Get-Prop $report 'status' '?'),
                (Get-Prop $report 'sampledRowCount' (Get-Prop $report 'rowCount' '?')),
                (@(Get-Prop $report 'dimensionKeys' @()).Count)
            Write-Result -Name ("GET /api/onboarding/discover?alias={0} -> 200" -f $alias) -Ok $true -Detail $detail
        }
        elseif ($disc.Status -eq 400) {
            # A caller mistake or a source that cannot be queried. Acceptable ONLY if the message
            # tells the operator what to do; an opaque 400 is as useless as a 500.
            $message = Get-ApiError -Body $disc.Body
            $actionable = -not [string]::IsNullOrWhiteSpace($message)
            Write-Result -Name ("GET /api/onboarding/discover?alias={0} -> 400 with an actionable message" -f $alias) `
                -Ok $actionable -Detail $message
            if ([string]::IsNullOrWhiteSpace($appId)) {
                Write-Note ("source '{0}/{1}' has no appId registered, which is the usual cause. Set it under Settings > Telemetry sources." -f $alias, $environment)
            }
        }
        else {
            Write-Result -Name ("GET /api/onboarding/discover?alias={0} -> {1}" -f $alias, $disc.Status) -Ok $false `
                -Detail (Get-ApiError -Body $disc.Body)
        }

        if ($IncludeDraft) {
            $draftJson = ('{{ "profileKey": "{0}", "displayName": "Smoke draft", "alias": "{1}", "environment": "{2}", "hours": {3} }}' -f `
                $ProbeKey, $alias, $environment, $DiscoveryHours)
            $draftFile = Write-JsonBody -Json $draftJson
            $draft = Invoke-Api -Path '/api/onboarding/draft' -Method 'POST' -Headers $internal -BodyFile $draftFile

            # The draft endpoint persists nothing, so a 200 here still leaves the database untouched.
            Write-Result -Name ("POST /api/onboarding/draft -> {0} (persists nothing)" -f $draft.Status) `
                -Ok ($draft.Status -eq 200 -or $draft.Status -eq 400) `
                -Detail $(if ($draft.Status -ne 200) { Get-ApiError -Body $draft.Body } else { 'draft returned; nothing was saved' })
        } else {
            Write-Note 'POST /api/onboarding/draft not exercised. Pass -IncludeDraft to include it.'
        }
    }

    # =======================================================================================
    # STEP 5 - the summary a human reads out loud
    # =======================================================================================
    Write-Section 'STEP 5  Per-company summary'

    if ($profiles.Count -eq 0) {
        Write-Result -Name 'summary table' -Ok $false -Detail 'No profiles to summarise.'
    }
    else {
        $rows = foreach ($p in $profiles) {
            $labels   = Get-Prop $p 'labels'
            $services = @(Get-Prop $p 'services' @())

            $stepCount = 0
            foreach ($svc in $services) {
                foreach ($flow in @(Get-Prop $svc 'flows' @())) {
                    $stepCount += @(Get-Prop $flow 'steps' @()).Count
                }
            }

            [pscustomobject] @{
                Company   = Get-Prop $p 'profileKey' '(no key)'
                Enabled   = Get-Prop $p 'enabled' $true
                Service   = Get-Prop $labels 'service' '?'
                Flow      = Get-Prop $labels 'flow' '?'
                Step      = Get-Prop $labels 'step' '?'
                Entity    = Get-Prop $labels 'entity' '?'
                Services  = $services.Count
                Steps     = $stepCount
                Identity  = @(Get-Prop $p 'entityIdentitySources' @()).Count
            }
        }

        $rows | Format-Table -AutoSize | Out-String | Write-Host

        $empty = @($rows | Where-Object { $_.Services -eq 0 -or $_.Steps -eq 0 -or $_.Identity -eq 0 })
        Write-Result -Name 'every configured company maps services, steps and an identity source' `
            -Ok ($empty.Count -eq 0) `
            -Detail $(if ($empty.Count -gt 0) {
                'incomplete: ' + (($empty | ForEach-Object { $_.Company }) -join ', ') +
                ' - a profile with no steps classifies every row as unresolved, which looks identical to a customer whose traffic never arrived.'
            } else { '' })
    }

    # =======================================================================================
    Write-Section 'RESULT'

    Write-Host ("  PASS {0}    FAIL {1}" -f $script:Passed, $script:Failed) `
        -ForegroundColor $(if ($script:Failed -eq 0) { 'Green' } else { 'Red' })

    Write-Host ''
    Write-Host '  To actually run the agents (this script deliberately did not):' -ForegroundColor Yellow
    Write-Host ("    1. open {0}" -f $PortalUrl)
    Write-Host  '    2. Monkeys -> Scout Monkey -> Run now        (queries production, spends no LLM quota)'
    Write-Host  '    3. Scout packages -> pick a case -> Inspect  (THIS calls a model and spends quota)'
    Write-Host  '    4. Company switcher (top right) -> flip between companies to show the labels change'
    Write-Host ''
}
finally {
    foreach ($file in $script:TempFiles) {
        Remove-Item -LiteralPath $file -Force -ErrorAction SilentlyContinue
    }
}

if ($script:Failed -gt 0) { exit 1 }
exit 0
