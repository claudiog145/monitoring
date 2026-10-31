#!/usr/bin/env bash
# Creates platformDB and applies journey, dashboard, monitoring, and incident schemas. Idempotent.
# When MSSQL_DATABASE_QA is set (local docker-compose), the same scripts are applied to that
# second database on the same server. Unset for a single-database target such as Azure SQL.
set -euo pipefail

SQLCMD="${SQLCMD:-/opt/mssql-tools18/bin/sqlcmd}"
if [[ ! -x "$SQLCMD" ]]; then
  SQLCMD="/opt/mssql-tools/bin/sqlcmd"
fi

HOST="${MSSQL_HOST:-sql}"
SQL_USER="${MSSQL_USER:-sa}"
SA_PASSWORD="${MSSQL_SA_PASSWORD:?MSSQL_SA_PASSWORD is required}"
DB="${MSSQL_DATABASE:-platformDB}"
DB_QA="${MSSQL_DATABASE_QA:-}"
SCRIPTS="${SQL_SCRIPTS_DIR:-/scripts}"

run_sqlcmd() {
  "$SQLCMD" -S "$HOST" -U "$SQL_USER" -P "$SA_PASSWORD" -C -I -b "$@"
}

echo "Waiting for SQL Server at ${HOST}..."
for i in $(seq 1 60); do
  if run_sqlcmd -Q "SELECT 1" >/dev/null 2>&1; then
    break
  fi
  if [[ "$i" -eq 60 ]]; then
    echo "SQL Server did not become ready in time." >&2
    exit 1
  fi
  sleep 2
done

# Azure SQL Database connects directly to a single database (no cross-database catalog) - once
# $DB exists, connecting to it with -d succeeds and nothing else is needed. A fresh local
# containerized SQL Server has no $DB yet, so that connection fails and we fall back to creating
# it from the login's default database (master), which is where CREATE DATABASE/DB_ID both work.
provision_database() {
  local db="$1"
  echo "Ensuring database [${db}] exists..."
  if run_sqlcmd -d "$db" -Q "SELECT 1" >/dev/null 2>&1; then
    echo "Database [${db}] already exists - skipping CREATE DATABASE."
  else
    run_sqlcmd -Q "IF DB_ID(N'${db}') IS NULL CREATE DATABASE [${db}];"
  fi

  for script in \
    hotel/CreateHotelSchema.sql \
    car/CreateCarSchema.sql \
    cruise/CreateCruiseSchema.sql \
    air/CreateAirSchema.sql \
    CreateBrandTables.sql \
    CreateClubInventory.sql \
    CreateClubInventoryMaturity.sql \
    SeedClubInventory.sql \
    SeedClubInventoryFusionHybrid.sql \
    SeedClubProductMaturityStatus.sql \
    dashboard/CreateDashboardMetrics.sql \
    monitoring/CreateMonitoringSettings.sql \
    monitoring/CreateMonitoringFindings.sql \
    monitoring/CreateMonitoringLlmCallLog.sql \
    monitoring/CreateMonitoringLlmClientSetting.sql \
    monitoring/CreateMonitoringActions.sql \
    incident/CreateIncidentSchema.sql \
    monitoring/CreateMonitoringSloAlerting.sql \
    monitoring/SeedSloDefinitions.sql \
    monitoring/CreateMonitoringNotificationLog.sql \
    monitoring/CreateMonitoringScout.sql \
    monitoring/CreateMonitoringInspector.sql \
    monitoring/CreateMonitoringAgentPause.sql \
    monitoring/CreateMonitoringAgentBudget.sql \
    monitoring/CreateMonitoringServiceOwner.sql \
    monitoring/CreateMonitoringTelemetrySource.sql \
    monitoring/CreateMonitoringKqlQuery.sql \
    monitoring/CreateMonitoringRca.sql \
    monitoring/CreateMonitoringCompanyProfile.sql \
    monitoring/20241030_CreateAppInsightsTokensTable.sql
  do
    echo "Applying ${script} to [${db}]..."
    run_sqlcmd -d "$db" -i "${SCRIPTS}/${script}"
  done
}

provision_database "$DB"

if [[ -n "$DB_QA" && "$DB_QA" != "$DB" ]]; then
  provision_database "$DB_QA"
  echo "Applying staging hotel hosts to [${DB_QA}]..."
  run_sqlcmd -d "$DB_QA" -i "${SCRIPTS}/UpdateQaHotelHosts.sql"
fi

echo "SQL journey store is ready."
