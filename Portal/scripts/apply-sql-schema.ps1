# Applies every Sql/**/*.sql script (schemas + seeds, in the exact order init-sql.sh runs them
# locally) against ANY reachable SQL Server - QA, a teammate's machine, or a fresh environment -
# instead of the schema/seed drift only being fixed for `docker compose up` locally.
#
# Why this exists: docs/product/platform-reliability-strategy-v3-gap-analysis-2026-10-02.md and
# PR #66 review both flagged the same gap - publish-func-qa.ps1 only deploys the Functions image,
# it never applies SQL. There is no existing CI/CD step that runs schema/seed scripts against a
# deployed database, by design: nothing in this repo has az sql credentials at build time, and
# fabricating a CI workflow that writes to QA without a human ever validating it would be worse
# than the gap it claims to close. This script is the deliberately manual bridge instead - it
# reuses the already-tested journeymonitor-sql-init:local image (single source of truth stays
# init-sql.sh / Sql/**/*.sql; nothing is duplicated here) so running it against QA is the same
# code path already verified against the local database, just pointed at a different server.
#
# Usage (run by hand, with real QA credentials - never commit them):
#   .\Portal\scripts\apply-sql-schema.ps1 -SqlServer "<qa-server>.database.windows.net" `
#     -SqlUser "<qa-admin-login>" -SqlPassword (Read-Host -AsSecureString "SQL password")
#
# Azure SQL Database (unlike the containerized SQL Server used locally) has no "sa" login by
# default - pass the real server admin login via -SqlUser. MSSQL_DATABASE defaults to platformDB,
# matching every environment so far; override with -Database if that ever changes.

param(
    [Parameter(Mandatory = $true)][string]$SqlServer,
    [Parameter(Mandatory = $true)][string]$SqlUser,
    [Parameter(Mandatory = $true)][System.Security.SecureString]$SqlPassword,
    [string]$Database = "platformDB"
)

$ErrorActionPreference = "Stop"
$PortalRoot = Split-Path -Parent $PSScriptRoot
Set-Location $PortalRoot

$passwordPtr = [System.Runtime.InteropServices.Marshal]::SecureStringToGlobalAllocUnicode($SqlPassword)
$plainPassword = [System.Runtime.InteropServices.Marshal]::PtrToStringUni($passwordPtr)

try {
    Write-Host "Building journeymonitor-sql-init:local (same image docker compose uses locally)..."
    docker build -f docker/sql-init.Dockerfile -t journeymonitor-sql-init:local .
    if ($LASTEXITCODE -ne 0) { throw "docker build failed with exit $LASTEXITCODE" }

    Write-Host "Applying Sql/**/*.sql against $SqlServer/$Database..."
    docker run --rm `
        -e "MSSQL_HOST=$SqlServer" `
        -e "MSSQL_USER=$SqlUser" `
        -e "MSSQL_SA_PASSWORD=$plainPassword" `
        -e "MSSQL_DATABASE=$Database" `
        journeymonitor-sql-init:local
    if ($LASTEXITCODE -ne 0) { throw "schema/seed apply failed with exit $LASTEXITCODE" }

    Write-Host "Done."
}
finally {
    Remove-Variable plainPassword -ErrorAction SilentlyContinue
    [System.Runtime.InteropServices.Marshal]::ZeroFreeGlobalAllocUnicode($passwordPtr)
}
