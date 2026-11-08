-- =============================================================================
-- US 241624 — PatchClubProductMaturity_Observed.sql
-- Application Map: mark products that already have centralized telemetry as
-- Observed (MaturityLevelId = 1) on brand.ClubProductMaturityStatus.
-- =============================================================================
--
-- PURPOSE
--   Seed / inventory scripts leave product maturity at 0 (Not Observed).
--   This patch syncs maturity to Observed (1) for clubs that are already in
--   brand.Information (same set as the portal Domain/Club filter) — i.e. clubs
--   we are actively watching today.
--   It does NOT create Map product links. If the UI shows "—", this script
--   skips that product. Flights are out of scope. Levels 2/3 are not set here.
--   Existing levels 2/3 are never demoted (only 0 → 1). Local demo 2/3 cleanup
--   belongs in a separate, explicitly scoped script if needed.
--
-- MATCH RULES
--   Hotels / Cars  → ClubId in brand.Information
--   Cruises        → ClubInventory.IcePartnerId present in brand.Information
--   Flights        → not updated
--
-- WHEN TO RUN
--   - Once after deploying the UI that reads live maturity (US 241624).
--   - Again whenever observed coverage changes: new club in Information,
--     partner id filled in, club moved platform, etc.
--   Idempotent: safe to re-run; rows already at 1+ are left alone.
--
-- NOT auto-run
--   Not listed in docker/init-sql.sh. Portal start / docker compose / app
--   deploy will NOT apply this. Someone must execute it deliberately.
--
-- ---------------------------------------------------------------------------
-- LOCAL (Docker platformDB)
-- ---------------------------------------------------------------------------
--   Server:  127.0.0.1,1433   (prefer 127.0.0.1 over localhost on Windows)
--   Database: platformDB
--   Auth:    sa / value of MSSQL_SA_PASSWORD in Portal/.env
--            (compose default often Jm_LocalDev_1!)
--
--   SSMS: open this file → connect to platformDB → keep ApplyUpdates = 0 →
--         Execute → review Messages + result grids → set ApplyUpdates = 1 →
--         Execute again.
--
--   sqlcmd example:
--     sqlcmd -S 127.0.0.1,1433 -U sa -P "<MSSQL_SA_PASSWORD>" -C -d platformDB
--            -i PatchClubProductMaturity_Observed.sql
--     (edit ApplyUpdates in the file between preview and apply runs)
--
--   UI: rebuild/redeploy jm-web so CLUB_MAP_USE_LIVE_MATURITY=true is served;
--       hard-refresh Application Map; filter Product Status = 1 - Observed.
--
-- ---------------------------------------------------------------------------
-- QA / PROD (shared Map database)
-- ---------------------------------------------------------------------------
--   1) Confirm you are on the database the Application Map API reads.
--   2) Preview: ApplyUpdates = 0 → Execute → share PREVIEW counts with the
--      Map owner if needed.
--   3) Apply:  ApplyUpdates = 1 → Execute once.
--   4) Spot-check a known club (e.g. WeSalute ClubId 15811 → Hotels/Cars/Cruises
--      = 1 if maturity rows exist). SUMMARY Level1 should rise; Level2Plus is
--      informational only and must not be forced to 0 by this patch.
--   5) Deploy/refresh the web app that has live maturity enabled so badges show.
--
-- =============================================================================

SET NOCOUNT ON;

IF OBJECT_ID(N'tempdb..#Patch241624') IS NOT NULL DROP TABLE #Patch241624;
CREATE TABLE #Patch241624 (ApplyUpdates bit NOT NULL);
-- Preview first: 0 = no UPDATEs. Set to 1 only after reviewing PREVIEW results.
INSERT INTO #Patch241624 (ApplyUpdates) VALUES (0);
GO


PRINT N'=== CURRENT STATE (before) ===';
SELECT p.Name AS Product,
       SUM(CASE WHEN cps.MaturityLevelId = 0 THEN 1 ELSE 0 END) AS Level0,
       SUM(CASE WHEN cps.MaturityLevelId = 1 THEN 1 ELSE 0 END) AS Level1,
       SUM(CASE WHEN cps.MaturityLevelId >= 2 THEN 1 ELSE 0 END) AS Level2Plus
FROM brand.ClubProductMaturityStatus AS cps
INNER JOIN brand.Product AS p ON p.ProductId = cps.ProductId
GROUP BY p.Name, p.SortOrder
ORDER BY p.SortOrder;

PRINT N'=== PREVIEW: Hotels (ClubId in brand.Information, currently 0) ===';
SELECT ci.ClubId, ci.ClubName, ci.FusionPlatform, cps.MaturityLevelId AS CurrentLevel
FROM brand.ClubProductMaturityStatus AS cps
INNER JOIN brand.Product AS p ON p.ProductId = cps.ProductId AND p.Name = N'Hotels'
INNER JOIN brand.Information AS i ON i.ClubId = cps.ClubId
INNER JOIN brand.ClubInventory AS ci ON ci.ClubId = cps.ClubId
WHERE cps.MaturityLevelId = 0
ORDER BY ci.ClubName, ci.ClubId;

PRINT N'=== PREVIEW: Cars (ClubId in brand.Information, currently 0) ===';
SELECT ci.ClubId, ci.ClubName, ci.FusionPlatform, cps.MaturityLevelId AS CurrentLevel
FROM brand.ClubProductMaturityStatus AS cps
INNER JOIN brand.Product AS p ON p.ProductId = cps.ProductId AND p.Name = N'Cars'
INNER JOIN brand.Information AS i ON i.ClubId = cps.ClubId
INNER JOIN brand.ClubInventory AS ci ON ci.ClubId = cps.ClubId
WHERE cps.MaturityLevelId = 0
ORDER BY ci.ClubName, ci.ClubId;

PRINT N'=== PREVIEW: Cruises (IcePartnerId in brand.Information, currently 0) ===';
SELECT ci.ClubId, ci.ClubName, ci.IcePartnerId, ci.FusionPlatform, cps.MaturityLevelId AS CurrentLevel
FROM brand.ClubProductMaturityStatus AS cps
INNER JOIN brand.Product AS p ON p.ProductId = cps.ProductId AND p.Name = N'Cruises'
INNER JOIN brand.ClubInventory AS ci ON ci.ClubId = cps.ClubId
WHERE cps.MaturityLevelId = 0
  AND NULLIF(LTRIM(RTRIM(ci.IcePartnerId)), N'') IS NOT NULL
  AND EXISTS (
        SELECT 1
        FROM brand.Information AS i
        WHERE NULLIF(LTRIM(RTRIM(i.IcePartnerId)), N'') IS NOT NULL
          AND LTRIM(RTRIM(i.IcePartnerId)) = LTRIM(RTRIM(ci.IcePartnerId))
      )
ORDER BY ci.ClubName, ci.ClubId;
GO

-- Only raise eligible Hotels/Cars/Cruises from 0 → 1. Never demote 2/3.
IF EXISTS (SELECT 1 FROM #Patch241624 WHERE ApplyUpdates = 1)
BEGIN
    PRINT N'=== APPLY: Hotels 0 → 1 ===';
    UPDATE cps
    SET MaturityLevelId = 1
    FROM brand.ClubProductMaturityStatus AS cps
    INNER JOIN brand.Product AS p ON p.ProductId = cps.ProductId AND p.Name = N'Hotels'
    INNER JOIN brand.Information AS i ON i.ClubId = cps.ClubId
    WHERE cps.MaturityLevelId = 0;
    PRINT CONCAT(N'Hotels rows updated: ', @@ROWCOUNT);

    PRINT N'=== APPLY: Cars 0 → 1 ===';
    UPDATE cps
    SET MaturityLevelId = 1
    FROM brand.ClubProductMaturityStatus AS cps
    INNER JOIN brand.Product AS p ON p.ProductId = cps.ProductId AND p.Name = N'Cars'
    INNER JOIN brand.Information AS i ON i.ClubId = cps.ClubId
    WHERE cps.MaturityLevelId = 0;
    PRINT CONCAT(N'Cars rows updated: ', @@ROWCOUNT);

    PRINT N'=== APPLY: Cruises 0 → 1 ===';
    UPDATE cps
    SET MaturityLevelId = 1
    FROM brand.ClubProductMaturityStatus AS cps
    INNER JOIN brand.Product AS p ON p.ProductId = cps.ProductId AND p.Name = N'Cruises'
    INNER JOIN brand.ClubInventory AS ci ON ci.ClubId = cps.ClubId
    WHERE cps.MaturityLevelId = 0
      AND NULLIF(LTRIM(RTRIM(ci.IcePartnerId)), N'') IS NOT NULL
      AND EXISTS (
            SELECT 1
            FROM brand.Information AS i
            WHERE NULLIF(LTRIM(RTRIM(i.IcePartnerId)), N'') IS NOT NULL
              AND LTRIM(RTRIM(i.IcePartnerId)) = LTRIM(RTRIM(ci.IcePartnerId))
          );
    PRINT CONCAT(N'Cruises rows updated: ', @@ROWCOUNT);
END
ELSE
BEGIN
    PRINT N'=== DRY RUN: ApplyUpdates=0 — no UPDATEs applied. Review PREVIEW, then set ApplyUpdates=1. ===';
END
GO

PRINT N'=== SUMMARY (current state) ===';
SELECT p.Name AS Product,
       SUM(CASE WHEN cps.MaturityLevelId = 0 THEN 1 ELSE 0 END) AS Level0,
       SUM(CASE WHEN cps.MaturityLevelId = 1 THEN 1 ELSE 0 END) AS Level1,
       SUM(CASE WHEN cps.MaturityLevelId >= 2 THEN 1 ELSE 0 END) AS Level2Plus
FROM brand.ClubProductMaturityStatus AS cps
INNER JOIN brand.Product AS p ON p.ProductId = cps.ProductId
GROUP BY p.Name, p.SortOrder
ORDER BY p.SortOrder;
GO
