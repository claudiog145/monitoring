# Publish JourneyMonitor.Functions to QA.
# Builds in Azure Container Registry, then updates the Container App with a unique
# image tag so a new revision is always created (reusing only :qa does not).
#
# Prerequisites: az login in the ICE tenant, Contributor on the registry + container app.
# Run from anywhere:  .\Portal\scripts\publish-func-qa.ps1
#
# Manual order when a staging Functions app also exists:
#   1. this script
#   2. .\Portal\scripts\publish-func-qa-test.ps1
#   3. .\Portal\scripts\publish-web-qa.ps1   only when the portal itself changed
#
# The Doctor Monkey's GitHub Copilot token is NOT set by this script - as of 2026-09-23 it is not
# configured for QA at all, so the Doctor Monkey runs but always returns "unknown"/confidence 0
# (never fails, never invents a diagnosis) until one is set. PREFERRED: once this image is
# running, open the app's Settings tab -> "Doctor Monkey - LLM Client" and paste a `gho_...`
# token there (PUT /api/settings/llm-client) - it is stored in SQL (monitoring.LlmClientSetting)
# and needs no az cli access or redeploy, unlike the Container App env var below. That SQL value
# always wins over the env var when both are set (see LlmClientSettingsResolver).
#
# FALLBACK (only if SQL isn't reachable yet, e.g. a brand new environment): set the Container App
# env var directly. This is a one-time, manual step because the token is a secret and must never
# be pasted into a script, chat, or committed file. Run these yourself, replacing <TOKEN> with a
# real `gho_...` value from `gh auth login --scopes copilot && gh auth token`:
#   az containerapp secret set --name platform-reliability-ca-func-qa `
#     --resource-group platform-reliability-rg-qa --secrets "ai-github-token=<TOKEN>"
#   az containerapp update --name platform-reliability-ca-func-qa `
#     --resource-group platform-reliability-rg-qa --set-env-vars "Ai__GitHubToken=secretref:ai-github-token"
# The secret survives future `az containerapp update --image ...` calls from this script (image
# updates don't touch existing env vars/secrets), so this only needs to be done once per token.

$ErrorActionPreference = "Stop"

$SubscriptionId = "89780439-fb9b-4f7b-bf37-115569216afe"
$Registry = "iceacrnonprod"
$ImageName = "journeymonitor-functions"
$ContainerApp = "platform-reliability-ca-func-qa"
$ResourceGroup = "platform-reliability-rg-qa"
$Dockerfile = "src/JourneyMonitor.Functions/Dockerfile"

$PortalRoot = Split-Path -Parent $PSScriptRoot
Set-Location $PortalRoot

$tag = "{0}-{1}" -f (Get-Date -Format "yyyyMMddHHmmss"), ([guid]::NewGuid().ToString("N").Substring(0, 8))
$image = "${Registry}.azurecr.io/${ImageName}:$tag"

Write-Host "Subscription: $SubscriptionId"
Write-Host "Image:        $image"
Write-Host "App:          $ContainerApp"
Write-Host "Context:      $PortalRoot"

az account set --subscription $SubscriptionId

# az acr build tars the folder locally before .dockerignore applies. Visual Studio
# locks Portal/.vs files and that tar step fails with Permission denied.
$staging = Join-Path $env:TEMP "jm-acr-func-$tag"
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Path $staging | Out-Null
robocopy $PortalRoot $staging /E /XD .vs bin obj .git /XF *.user /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy failed with exit $LASTEXITCODE" }
$global:LASTEXITCODE = 0

Push-Location $staging
try {
    az acr build `
      --registry $Registry `
      --image "${ImageName}:$tag" `
      --image "${ImageName}:qa" `
      --file $Dockerfile `
      --target final `
      .
    if ($LASTEXITCODE -ne 0) { throw "az acr build failed with exit $LASTEXITCODE" }
}
finally {
    Pop-Location
    Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
}

# Spec 1000: the KpiIngestion and *HealthMonitor timers must fire every minute, so the app never
# scales to zero (--min-replicas 1). maxReplicas keeps its current value (1 when unset).
# The image fails indexing without both spec1000_*_cron settings, so they are added here, but only
# when missing: a value already changed in Azure (e.g. HealthMonitoring__Enabled=true to turn on
# shadow mode) is never overwritten by a publish. Safe defaults: Enabled=false, Triage__Mode=Shadow.
$Spec1000Defaults = [ordered]@{
    spec1000_ingestion_cron   = "0 */1 * * * *"
    spec1000_monitor_cron     = "30 */1 * * * *"
    spec1000_token_refresh_cron = "0 */5 * * * *"
    HealthMonitoring__Enabled = "false"
    Triage__Mode              = "Shadow"
}

$existingEnvNames = @(az containerapp show `
  --name $ContainerApp `
  --resource-group $ResourceGroup `
  --query "properties.template.containers[0].env[].name" `
  --output tsv)
if ($LASTEXITCODE -ne 0) { throw "az containerapp show (env) failed with exit $LASTEXITCODE" }

$maxReplicas = az containerapp show `
  --name $ContainerApp `
  --resource-group $ResourceGroup `
  --query "properties.template.scale.maxReplicas" `
  --output tsv
if ($LASTEXITCODE -ne 0) { throw "az containerapp show (scale) failed with exit $LASTEXITCODE" }
$parsedMax = 0
if (-not [int]::TryParse("$maxReplicas".Trim(), [ref]$parsedMax) -or $parsedMax -lt 1) { $parsedMax = 1 }

$missingEnvVars = @($Spec1000Defaults.Keys |
  Where-Object { $existingEnvNames -notcontains $_ } |
  ForEach-Object { "$_=$($Spec1000Defaults[$_])" })

$envArgs = @()
if ($missingEnvVars.Count -gt 0) {
    Write-Host "Adding Spec 1000 settings: $($missingEnvVars -join '; ')"
    $envArgs = @("--set-env-vars") + $missingEnvVars
}

az containerapp update `
  --name $ContainerApp `
  --resource-group $ResourceGroup `
  --image $image `
  --min-replicas 1 `
  --max-replicas $parsedMax `
  @envArgs `
  --query "{image:properties.template.containers[0].image, revision:properties.latestRevisionName, status:properties.runningStatus, scale:properties.template.scale}" `
  --output json
if ($LASTEXITCODE -ne 0) { throw "az containerapp update failed with exit $LASTEXITCODE" }

Write-Host "Published $image"
