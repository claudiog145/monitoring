# Create/update Application Map Hotels workbook (US 241135).
# Requires: az login on ICE tenant 60396ff5-e90f-4bc5-aced-a4f462f2636e
# Subscription: 89780439-fb9b-4f7b-bf37-115569216afe
#
# Note: az rest often drops Content-Type on Windows → UnsupportedMediaType.
# Use curl.exe (reliable). Idempotent PUT on fixed GUID.

$ErrorActionPreference = 'Stop'
$sub = '89780439-fb9b-4f7b-bf37-115569216afe'
$rg = 'sor-appinsights-rg-usw'
$sourceId = "/subscriptions/$sub/resourceGroups/$rg/providers/microsoft.insights/components/sor-frontend-ai-prod-usw"
$guid = '87e5efd7-2760-4487-96af-fe9b51077736'  # stable id used by Map deep-link config
$workbookId = "/subscriptions/$sub/resourceGroups/$rg/providers/Microsoft.Insights/workbooks/$guid"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$notebookPath = Join-Path $here 'application-map-hotels-by-clubid.json'
$putPath = Join-Path $here '_arm-put-hotels-workbook.json'
$outPath = Join-Path $here '_create-result.txt'

$serialized = [System.IO.File]::ReadAllText($notebookPath)
$obj = [ordered]@{
  location = 'westus'
  kind = 'shared'
  properties = [ordered]@{
    displayName = 'Application Map - Hotels (by ClubId)'
    category = 'workbook'
    serializedData = $serialized
    sourceId = $sourceId
    description = 'US 241135 Application Map Hotels badge. Params: ClubId, TimeRange (1h-30d + custom), RowLimit.'
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
Write-Host "Portal:"
Write-Host "https://portal.azure.com/#@iceenterprise.com/resource$workbookId"
Write-Host "Deep-link example with ClubId=15639:"
Write-Host "https://portal.azure.com/#@iceenterprise.com/resource$workbookId`?ClubId=15639"
