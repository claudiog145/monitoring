-- Create schema if it does not exist
IF SCHEMA_ID('brand') IS NULL
    EXEC('CREATE SCHEMA brand');
GO

-- Table 1: brand.Information
IF OBJECT_ID(N'brand.Information', N'U') IS NULL
BEGIN
    CREATE TABLE brand.Information
    (
        BrandName     NVARCHAR(200)  NOT NULL,
        ClubId        INT            NOT NULL
            CONSTRAINT PK_brand_Information PRIMARY KEY,
        IcePartnerId  NVARCHAR(100)  NULL,
        Url           NVARCHAR(2048) NULL
    );
END
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.key_constraints
    WHERE [type] = N'PK'
      AND parent_object_id = OBJECT_ID(N'brand.Information', N'U')
)
BEGIN
    ;WITH DeduplicatedInformation AS
    (
        SELECT BrandName,
               ClubId,
               IcePartnerId,
               Url,
               ROW_NUMBER() OVER (
                   PARTITION BY ClubId
                   ORDER BY BrandName, IcePartnerId, Url, ClubId
               ) AS rn
        FROM brand.Information
    )
    DELETE FROM DeduplicatedInformation
    WHERE rn > 1;

    ALTER TABLE brand.Information
        ADD CONSTRAINT PK_brand_Information PRIMARY KEY (ClubId);
END
GO

MERGE brand.Information WITH (HOLDLOCK) AS target
USING
(
    VALUES
    (N'T-Mobile', 15639, N'308', N'stays.tmobiletravel.com'),
    (N'Smarter Getaways', 15662, N'239', N'members.smartergetaways.com'),
    (N'Smarter Getaways', 15666, N'239', N'members.smartergetaways.com'),
    (N'Smarter Getaways', 15667, N'239', N'members.smartergetaways.com'),
    (N'Smarter Getaways', 15668, N'239', N'members.smartergetaways.com'),
    (N'Smarter Getaways', 15669, N'239', N'members.smartergetaways.com'),
    (N'Smarter Getaways', 15694, N'239', N'members.smartergetaways.com'),
    (N'Journeys by Krystal', 15777, N'321', N'members.journeysbykrystal.com'),
    (N'Journeys by Krystal', 15791, N'321', N'members.journeysbykrystal.com'),
    (N'Journeys by Krystal', 15792, N'321', N'members.journeysbykrystal.com'),
    (N'Journeys by Krystal', 15793, N'321', N'members.journeysbykrystal.com'),
    (N'Journeys by Krystal', 15794, N'321', N'members.journeysbykrystal.com'),
    (N'Journeys by Krystal', 15795, N'321', N'members.journeysbykrystal.com'),
    (N'Journeys by Krystal', 15796, N'321', N'members.journeysbykrystal.com'),
    (N'Journeys by Krystal', 15798, N'321', N'members.journeysbykrystal.com'),
    (N'Journeys by Krystal', 15799, N'321', N'members.journeysbykrystal.com'),
    (N'Journeys by Krystal', 15800, N'321', N'members.journeysbykrystal.com'),
    (N'Journeys by Krystal', 15801, N'321', N'members.journeysbykrystal.com'),
    (N'Journeys by Krystal', 15849, N'321', N'members.journeysbykrystal.com'),
    (N'Synchrony Travel', 15525, N'306', N'travel.mysynchronytravel.com'),
    (N'Synchrony Travel', 15695, N'306', N'travel.mysynchronytravel.com'),
    (N'Synchrony Travel', 15784, N'306', N'travel.mysynchronytravel.com'),
    (N'Synchrony Travel', 15491, N'306', N'travel.mysynchronytravel.com'),
    (N'Synchrony Travel', 15039, N'306', N'travel.mysynchronytravel.com'),
    (N'Shopr Rewards', 15810, N'326', N'members.shoprrewards.com'),
    (N'Shopr Rewards', 15813, N'326', N'members.shoprrewards.com'),
    (N'WeSalute', 15811, N'323', N'book.wesalute.com'),
    (N'WeSalute', 15827, N'323', N'book.wesalute.com'),
    (N'AARP Go', 15893, N'333', N'members.aarparrivia.com'),
    (N'AARP Go', 16108, N'333', N'members.aarparrivia.com'),
    (N'AARP Go', 15914, N'333', N'members.aarparrivia.com'),
    (N'AARP Go', 15916, N'333', N'members.aarparrivia.com'),
    (N'AARP Go', 15915, N'333', N'members.aarparrivia.com'),
    (N'AARP Go', 16109, N'333', N'members.aarparrivia.com'),
    (N'AARP Go', 15894, N'333', N'members.aarparrivia.com'),
    (N'AARP Go', 15924, N'333', N'members.aarparrivia.com'),
    (N'Arrivia Fusion', 15820, N'314', N'members.arriviafusion.com'),
    (N'Arrivia Fusion', 15821, N'314', N'members.arriviafusion.com'),
    (N'Arrivia Fusion', 15889, N'314', N'members.arriviafusion.com'),
    (N'Arrivia Fusion', 15814, N'314', N'members.arriviafusion.com'),
    (N'Arrivia Fusion', 15815, N'314', N'members.arriviafusion.com'),
    (N'Arrivia Fusion', 15778, N'314', N'members.arriviafusion.com'),
    (N'Arrivia Fusion', 15809, N'314', N'members.arriviafusion.com')
) AS source (BrandName, ClubId, IcePartnerId, Url)
    ON target.ClubId = source.ClubId
WHEN MATCHED THEN
    UPDATE SET
        BrandName = source.BrandName,
        IcePartnerId = source.IcePartnerId,
        Url = source.Url
WHEN NOT MATCHED BY TARGET THEN
    INSERT (BrandName, ClubId, IcePartnerId, Url)
    VALUES (source.BrandName, source.ClubId, source.IcePartnerId, source.Url);
GO
