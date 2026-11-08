-- Patch 2026-10-28: enrich ClubInventory.Domain from Miguel Marketing (Fusion short list).
-- Cristiano: stick to Cars / Cruises / Hotels / Flights only.
-- Does NOT change Site or FusionPlatform. Does NOT insert NewFromMarketing rows.
-- Idempotent. Re-run SeedClubProductMaturityStatus.sql after this (adds maturity 0 for new links).

SET NOCOUNT ON;
GO

/* Ensure Domain contains each required product token (preserve existing order/extras). */
CREATE OR ALTER FUNCTION brand.fn_EnsureDomainProducts(@Domain NVARCHAR(200), @Required NVARCHAR(200))
RETURNS NVARCHAR(200)
AS
BEGIN
    DECLARE @out NVARCHAR(200) = LTRIM(RTRIM(ISNULL(@Domain, N'')));
    DECLARE @tok NVARCHAR(50);
    DECLARE @pos INT;

    WHILE LEN(@Required) > 0
    BEGIN
        SET @pos = CHARINDEX(N',', @Required);
        IF @pos > 0
        BEGIN
            SET @tok = LTRIM(RTRIM(LEFT(@Required, @pos - 1)));
            SET @Required = SUBSTRING(@Required, @pos + 1, 400);
        END
        ELSE
        BEGIN
            SET @tok = LTRIM(RTRIM(@Required));
            SET @Required = N'';
        END

        IF @tok <> N''
           AND (
                @out = N''
                OR NOT EXISTS (
                    SELECT 1
                    FROM STRING_SPLIT(@out, N',') s
                    WHERE LTRIM(RTRIM(s.value)) = @tok
                )
           )
        BEGIN
            SET @out = CASE WHEN @out = N'' THEN @tok ELSE @out + N', ' + @tok END;
        END
    END

    RETURN @out;
END;
GO

BEGIN TRAN;

-- Synchrony Consumer
UPDATE brand.ClubInventory
SET Domain = brand.fn_EnsureDomainProducts(Domain, N'Cars, Cruises, Hotels, Flights')
WHERE ClubId IN (15491, 15695, 15784);

-- Synchrony Employee (15524 not in ACTIVE inventory)
UPDATE brand.ClubInventory
SET Domain = brand.fn_EnsureDomainProducts(Domain, N'Cars, Cruises, Hotels, Flights')
WHERE ClubId IN (15039, 15499, 15525);

-- T-Mobile (only ACTIVE ClubIds from Marketing match)
UPDATE brand.ClubInventory
SET Domain = brand.fn_EnsureDomainProducts(Domain, N'Cars, Cruises, Hotels, Flights')
WHERE ClubId IN (15639, 15700);

-- Encompass Fusion → ensure the four Map products (Marketing match; was often empty/partial)
UPDATE brand.ClubInventory
SET Domain = brand.fn_EnsureDomainProducts(Domain, N'Cars, Cruises, Hotels, Flights')
WHERE FusionPlatform = N'Fusion'
  AND Site LIKE N'%encompassbyvida%';

COMMIT TRAN;
GO

-- Preview
SELECT ClubId, ClubName, Site, Domain
FROM brand.ClubInventory
WHERE ClubId IN (15491, 15695, 15784, 15039, 15499, 15525, 15639, 15700)
   OR (FusionPlatform = N'Fusion' AND Site LIKE N'%encompassbyvida%')
ORDER BY ClubName, ClubId;
GO
