-- Populate brand.ClubProductMaturityStatus from ClubInventory.Domain (legacy CSV).
-- Only Fusion/Hybrid clubs. Only Products Cars/Cruises/Hotels/Flights. Default Status = 0.
-- Run after CreateClubInventory.sql, CreateClubInventoryMaturity.sql, and a ClubInventory seed.
-- Idempotent MERGE. Safe to re-run when new clubs are added to ClubInventory.

SET NOCOUNT ON;
GO

;WITH ClubProducts AS (
    SELECT DISTINCT
        c.ClubId,
        p.ProductId
    FROM brand.ClubInventory AS c
    CROSS APPLY STRING_SPLIT(c.Domain, N',') AS parts
    INNER JOIN brand.Product AS p
        ON LTRIM(RTRIM(parts.value)) = p.Name
    WHERE c.FusionPlatform IN (N'Fusion', N'Hybrid')
      AND c.Domain IS NOT NULL
)
MERGE brand.ClubProductMaturityStatus WITH (HOLDLOCK) AS target
USING ClubProducts AS source
ON target.ClubId = source.ClubId AND target.ProductId = source.ProductId
WHEN NOT MATCHED BY TARGET THEN
    INSERT (ClubId, ProductId, MaturityLevelId)
    VALUES (source.ClubId, source.ProductId, 0);
-- Do not delete unmatched rows: keep statuses already raised by monkeys.
GO
