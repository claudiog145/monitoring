# Roll the JourneyMonitor.Functions image already running on platform-reliability-ca-func-qa
# onto the staging Functions Container App (platform-rel-ca-func-qa-test).
#
# Manual order:
#   1. .\Portal\scripts\publish-func-qa.ps1        builds the image and updates the existing app
#   2. .\Portal\scripts\publish-func-qa-test.ps1   points the staging app at that same image
#   3. .\Portal\scripts\publish-web-qa.ps1         only when the portal itself changed
#
# This script does not build an image and does not change secrets or existing env vars. It only
# adds the Spec 1000 settings when they are missing (the image fails indexing without both
# spec1000_*_cron values); defaults are safe: HealthMonitoring__Enabled=false, Triage__Mode=Shadow.
# It does not change scaling: only platform-reliability-ca-func-qa is pinned to --min-replicas 1
# by publish-func-qa.ps1. The staging app must already exist, bound to platform-reliability-db-qa-test, with
# JourneySync__Environment=staging and its own AzureFunctionsWebHost__hostid.
#
# Prerequisites: az login in the ICE tenant, Contributor on the container app.
# Run from anywhere:  .\Portal\scripts\publish-func-qa-test.ps1

$ErrorActionPreference = "Stop"

$SubscriptionId = "89780439-fb9b-4f7b-bf37-115569216afe"
$ResourceGroup = "platform-reliability-rg-qa"
$SourceApp = "platform-reliability-ca-func-qa"
$ContainerApp = "platform-rel-ca-func-qa-test"

Write-Host "Subscription: $SubscriptionId"
Write-Host "Source app:   $SourceApp"
Write-Host "App:          $ContainerApp"

az account set --subscription $SubscriptionId
if ($LASTEXITCODE -ne 0) { throw "az account set failed with exit $LASTEXITCODE" }

$image = (az containerapp show `
  --name $SourceApp `
  --resource-group $ResourceGroup `
  --query "properties.template.containers[0].image" `
  --output tsv).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($image)) {
    throw "Could not read the image from $SourceApp. Run .\Portal\scripts\publish-func-qa.ps1 first."
}

Write-Host "Image:        $image"

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
  @envArgs `
  --query "{image:properties.template.containers[0].image, revision:properties.latestRevisionName, status:properties.runningStatus}" `
  --output json
if ($LASTEXITCODE -ne 0) { throw "az containerapp update failed with exit $LASTEXITCODE" }

Write-Host "Published $image to $ContainerApp"
