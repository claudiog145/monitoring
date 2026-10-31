# Publish JourneyMonitor.Web to QA.
# Builds in Azure Container Registry, then updates the Container App with a unique
# image tag so a new revision is always created (reusing only :qa does not).
#
# Prerequisites: az login in the ICE tenant, Contributor on the registry + container app.
# Run from anywhere:  .\Portal\scripts\publish-web-qa.ps1

$ErrorActionPreference = "Stop"

$SubscriptionId = "89780439-fb9b-4f7b-bf37-115569216afe"
$Registry = "iceacrnonprod"
$ImageName = "journeymonitor-web"
$ContainerApp = "platform-reliability-ca-wa-qa"
$ResourceGroup = "platform-reliability-rg-qa"
$Dockerfile = "src/JourneyMonitor.Web/Dockerfile"

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
$staging = Join-Path $env:TEMP "jm-acr-web-$tag"
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
      .
    if ($LASTEXITCODE -ne 0) { throw "az acr build failed with exit $LASTEXITCODE" }
}
finally {
    Pop-Location
    Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
}

az containerapp update `
  --name $ContainerApp `
  --resource-group $ResourceGroup `
  --image $image `
  --query "{image:properties.template.containers[0].image, revision:properties.latestRevisionName, status:properties.runningStatus}" `
  --output json
if ($LASTEXITCODE -ne 0) { throw "az containerapp update failed with exit $LASTEXITCODE" }

Write-Host "Published $image"
