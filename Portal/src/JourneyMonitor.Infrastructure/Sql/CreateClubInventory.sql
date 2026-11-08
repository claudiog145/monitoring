-- Application Map inventory. One row per inventory key (PK = ClubId).
-- brand.Information stays the short Club dropdown. This table is the full inventory.
-- ClientId: SaveOn VacationClubClientID, or Members PartnerId for NG.
-- FusionPlatform (historical column name): platform family Fusion | Hybrid | SOR | NG.
-- NG rows use ClubId = 1000000 + MemberClassId so they do not collide with SaveOn IDs;
-- the Application Map UI subtracts that offset when showing Club ID.
-- Domain is a legacy CSV used only to seed brand.ClubProductMaturityStatus; the UI
-- reads Products + Status from CreateClubInventoryMaturity.sql tables.
-- Servers, Owner, and ValidationDate are omitted. Validation is the platform-owner review of the map.
-- Docker init: CreateClubInventory.sql → SeedClubInventory.sql →
-- SeedClubProductMaturityStatus.sql. Full seed is for local Docker; trim from
-- init-sql.sh if startup becomes too slow.
-- Observed (1) is NOT applied by init. Run PatchClubProductMaturity_Observed.sql
-- manually (see workbooks/application-map/README.md § Observed maturity).

IF SCHEMA_ID(N'brand') IS NULL
    EXEC(N'CREATE SCHEMA brand');
GO

IF OBJECT_ID(N'brand.ClubInventory', N'U') IS NULL
BEGIN
    CREATE TABLE brand.ClubInventory
    (
        Domain                NVARCHAR(200)  NULL,
        BrandName             NVARCHAR(200)  NOT NULL,
        ClubId                INT            NOT NULL
            CONSTRAINT PK_brand_ClubInventory PRIMARY KEY,
        ClubName              NVARCHAR(200)  NULL,
        ClientId              INT            NULL,
        IcePartnerId          NVARCHAR(100)  NULL,
        Site                  NVARCHAR(2048) NULL,
        FusionPlatform        NVARCHAR(50)   NULL,
        Members               INT            NOT NULL
            CONSTRAINT DF_brand_ClubInventory_Members DEFAULT (0),
        TelemetryLocation     NVARCHAR(400)  NULL,
        BusinessDataLocation  NVARCHAR(200)  NOT NULL,
        Environment           NVARCHAR(50)   NOT NULL
    );
END
GO
