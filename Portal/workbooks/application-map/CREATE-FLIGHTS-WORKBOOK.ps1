# Create/update Application Map Flights workbook (US 241135).
# Hosted on ICE sor-appinsights-rg-usw (write access). KQL targets Air UI (cross-resource).
# Air tenant workbook write was denied for alejandro.morales — do not put the ARM resource in Air-RG.
# Requires: az login on ICE tenant 60396ff5-e90f-4bc5-aced-a4f462f2636e
# To query Air data in portal you also need access to tenant b0298d22-1db0-4550-8fa6-a116a5ab4208.

$ErrorActionPreference = 'Stop'
$sub = '89780439-fb9b-4f7b-bf37-115569216afe'
$rg = 'sor-appinsights-rg-usw'
$sourceId = "/subscriptions/$sub/resourceGroups/$rg/providers/microsoft.insights/components/sor-frontend-ai-prod-usw"
$guid = 'e1f2a3b4-5c6d-7e8f-9012-3456789abcde'
$workbookId = "/subscriptions/$sub/resourceGroups/$rg/providers/Microsoft.Insights/workbooks/$guid"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$notebookPath = Join-Path $here 'application-map-flights-by-clubid.json'
$putPath = Join-Path $here '_arm-put-flights-workbook-ice.json'
$outPath = Join-Path $here '_create-flights-ice-result.txt'

az account set --subscription $sub | Out-Null
$serialized = [System.IO.File]::ReadAllText($notebookPath)
$obj = [ordered]@{
  location = 'westus'
  kind = 'shared'
  properties = [ordered]@{
    displayName = 'Application Map - Flights (by ClubId)'
    category = 'workbook'
    serializedData = $serialized
    sourceId = $sourceId
    description = 'US 241135 Flights. Hosted on ICE; KQL targets Air UI cross-resource.'
  }
}
[System.IO.File]::WriteAllText($putPath, ($obj | ConvertTo-Json -Depth 30 -Compress))

$api = "https://management.azure.com${workbookId}?api-version=2022-04-01"
Write-Host "Creating $workbookId ..."
$token = (az account get-access-token --resource https://management.azure.com --query accessToken -o tsv).Trim()
& curl.exe -sS -X PUT $api `
  -H "Authorization: Bearer $token" `
  -H "Content-Type: application/json" `
  --data-binary "@$putPath" `
  -o $outPath `
  --max-time 90
if ($LASTEXITCODE -ne 0) { throw "curl PUT failed: $LASTEXITCODE" }
$created = Get-Content $outPath -Raw | ConvertFrom-Json
if (-not $created.properties.displayName) { throw "Create failed. See $outPath" }
Write-Host "Done: $($created.properties.displayName)"
Write-Host "https://portal.azure.com/#@iceenterprise.com/resource$workbookId"
