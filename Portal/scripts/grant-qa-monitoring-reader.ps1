# Grants the QA Functions Container App's managed identity read access to every Application
# Insights component in TelemetrySourceDefaults.cs, so Scout/Inspector queries stop failing with
# App Insights 400 "InsufficientAccessToResource".
#
# WHO RUNS THIS: an Owner or User Access Administrator on subscription 89780439-... (the
# "Platform Engineering" AAD group holds both roles). A plain Contributor cannot - the action is
# Microsoft.Authorization/roleAssignments/write.
#
# Idempotent: existing assignments are reported and skipped.
#
# NOT COVERED HERE - the Air components live in subscription 819c105d-0328-4c63-a1a4-5284021e175f,
# which belongs to tenant b0298d22-1db0-4550-8fa6-a116a5ab4208 (see Insights__Air__TenantId).
# A system-assigned managed identity is single-tenant and cannot obtain a token there no matter
# what RBAC is granted, so Air needs a separate cross-tenant identity (multi-tenant app
# registration consented in the Air tenant, or a service principal secret).

$ErrorActionPreference = "Stop"

# System-assigned identity of platform-reliability-ca-func-qa (platform-reliability-rg-qa).
# Re-read it rather than trusting this default if the app was ever recreated:
#   az containerapp show -n platform-reliability-ca-func-qa -g platform-reliability-rg-qa --query identity.principalId -o tsv
$PrincipalId = "1d376fd3-a5df-4dd3-a9b5-8d863b5c58e4"
$SubscriptionId = "89780439-fb9b-4f7b-bf37-115569216afe"

$components = @(
    "/subscriptions/$SubscriptionId/resourceGroups/cruiseng-rg-prod-usc/providers/microsoft.insights/components/cruise-web-api-ai-prod"
    "/subscriptions/$SubscriptionId/resourceGroups/cruiseng-rg-uat-usc/providers/microsoft.insights/components/cruise-core-ovs-ai-uat"
    "/subscriptions/$SubscriptionId/resourceGroups/club-rg-uat/providers/microsoft.insights/components/club-ai-uat-usc"
    # Already granted as of 2026-10-20; listed so the catalog and this script stay comparable.
    "/subscriptions/$SubscriptionId/resourceGroups/cruiseng-rg-prod-usc/providers/microsoft.insights/components/cruise-core-ovs-ai-prod"
    "/subscriptions/$SubscriptionId/resourceGroups/ovs-club-dev/providers/microsoft.insights/components/appInsights-ovs-club"
    "/subscriptions/$SubscriptionId/resourceGroups/sor-appinsights-rg-usw/providers/microsoft.insights/components/sor-frontend-ai-prod-usw"
)

az account set --subscription $SubscriptionId

foreach ($scope in $components) {
    $name = $scope.Split("/")[-1]
    $existing = az role assignment list --assignee $PrincipalId --scope $scope `
        --query "[?roleDefinitionName=='Monitoring Reader'].id" -o tsv
    if ($existing) {
        Write-Host "SKIP  $name (already Monitoring Reader)"
        continue
    }

    az role assignment create `
        --assignee-object-id $PrincipalId `
        --assignee-principal-type ServicePrincipal `
        --role "Monitoring Reader" `
        --scope $scope `
        --output none
    if ($LASTEXITCODE -ne 0) { throw "Failed to grant Monitoring Reader on $name (exit $LASTEXITCODE)" }
    Write-Host "GRANT $name"
}

Write-Host ""
Write-Host "Done. RBAC takes a few minutes to propagate; no redeploy or restart is needed."
