# Create/update Application Map Cruises workbook (US 241135).
# Workbook resource stays on cruise-core. The KQL reads cruise-web-ai-prod,
# where partnerId and product=cruise are stored. cruise-core has neither.
# Requires: az login on ICE tenant 60396ff5-e90f-4bc5-aced-a4f462f2636e

$ErrorActionPreference = 'Stop'
$sub = '89780439-fb9b-4f7b-bf37-115569216afe'
$rg = 'cruiseng-rg-prod-usc'
$sourceId = "/subscriptions/$sub/resourceGroups/$rg/providers/microsoft.insights/components/cruise-core-ovs-ai-prod"
$guid = 'c7d8e9f0-2a3b-4c5d-9e6f-77a8b9c0d1e2'
$workbookId = "/subscriptions/$sub/resourceGroups/$rg/providers/Microsoft.Insights/workbooks/$guid"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$notebookPath = Join-Path $here 'application-map-cruises-by-clubid.json'
$putPath = Join-Path $here '_arm-put-cruises-workbook.json'
$outPath = Join-Path $here '_create-cruises-result.txt'

$serialized = [System.IO.File]::ReadAllText($notebookPath)
$obj = [ordered]@{
  location = 'westus2'
  kind = 'shared'
  properties = [ordered]@{
    displayName = 'Application Map - Cruises (by ClubId)'
    category = 'workbook'
    serializedData = $serialized
    sourceId = $sourceId
    description = 'US 241135 Cruises badge. ClubId from the map. PartnerId is sent when the club has one and is hidden. TimeRange (1h-30d + custom), RowLimit.'
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
