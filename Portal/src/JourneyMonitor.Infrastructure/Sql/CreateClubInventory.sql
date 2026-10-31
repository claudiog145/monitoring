-- Club and Fusion application map. One row per SaveOn VacationClub.ID (PK = ClubId).
-- brand.Information stays the short Club dropdown. This table is the full inventory.
-- ClientId comes from SaveOn VacationClub.VacationClubClientID.
-- FusionPlatform is Fusion, Hybrid, or Unknown (Vera brand list + SaveOn match rules).
-- Domain is a legacy CSV used only to seed brand.ClubProductMaturityStatus; the Club Map UI
-- reads Products + Status from CreateClubInventoryMaturity.sql tables.
-- Servers, Owner, and ValidationDate are omitted. Validation is the platform-owner review of the map.
-- Docker init creates the table and loads data via SeedClubInventory.sql
-- (all clubs), SeedClubInventoryFusionHybrid.sql (Fusion/Hybrid upsert), then
-- SeedClubProductMaturityStatus.sql. Full seed is included for local Docker
-- (init only runs locally); remove from init-sql.sh if startup becomes too slow.

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
