-- Club and Fusion application map. One row per SaveOn VacationClub.ID.
-- brand.Information stays the short Club dropdown. This table is the full inventory.
-- There is no per-club server list, so Servers is not a column.
-- Owner and ValidationDate stay null until a later review fills them.
-- Docker init creates the empty table. Load the 2026-10-09 snapshot with SeedClubInventory.sql.

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
        IcePartnerId          NVARCHAR(100)  NULL,
        Site                  NVARCHAR(2048) NULL,
        FusionPlatform        NVARCHAR(50)   NULL,
        Members               INT            NOT NULL
            CONSTRAINT DF_brand_ClubInventory_Members DEFAULT (0),
        Owner                 NVARCHAR(200)  NULL,
        TelemetryLocation     NVARCHAR(400)  NULL,
        BusinessDataLocation  NVARCHAR(200)  NOT NULL,
        Environment           NVARCHAR(50)   NOT NULL,
        ValidationDate        DATE           NULL
    );
END
GO
