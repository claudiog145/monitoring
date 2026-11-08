-- LOCAL DEMO ONLY — sample maturity so Application Map shows 0/1/2/3 and
-- overview observed / partial / not observed across Fusion, Hybrid, SOR, NG.
-- Do not run in shared/prod. Safe to re-run (MERGE updates levels).
-- ProductId: 1 Cars, 2 Cruises, 3 Hotels, 4 Flights.

SET NOCOUNT ON;
GO

MERGE brand.ClubProductMaturityStatus WITH (HOLDLOCK) AS target
USING (VALUES
    -- ===== Fusion =====
    -- T-Mobile (15639): all Observed
    (15639, 1, 1), (15639, 2, 1), (15639, 3, 1),
    -- Smarter Getaways (15662): 0/1/2/3 mix → partial
    (15662, 1, 0), (15662, 2, 1), (15662, 3, 2), (15662, 4, 3),
    -- Synchrony (15491): all Alerting
    (15491, 1, 3), (15491, 2, 3), (15491, 3, 3), (15491, 4, 3),
    -- WeSalute-ish Fusion with Domain: 15827 if present — Observed
    (15827, 1, 1), (15827, 2, 1), (15827, 3, 1), (15827, 4, 1),
    -- Another Fusion partial
    (15811, 1, 2), (15811, 3, 0), (15811, 4, 1),

    -- ===== Hybrid =====
    -- NEAMB (15730): partial
    (15730, 1, 2), (15730, 2, 2), (15730, 3, 0), (15730, 4, 0),
    -- SEIUMB (15797): all Observed
    (15797, 1, 1), (15797, 2, 1), (15797, 3, 1), (15797, 4, 1),
    -- ULifestyle (13530): all Agentically
    (13530, 1, 2), (13530, 2, 2), (13530, 3, 2), (13530, 4, 2),
    -- Lifestyle Travel Club (15439): Alerting
    (15439, 1, 3), (15439, 3, 3), (15439, 4, 3),
    -- Xperiences (14109): partial
    (14109, 1, 1), (14109, 2, 0), (14109, 3, 1), (14109, 4, 0),

    -- ===== SOR (need maturity rows; Domain exists but seed normally skips SOR) =====
    -- i Go 4 Less (12715): all Observed
    (12715, 1, 1), (12715, 2, 1), (12715, 3, 1), (12715, 4, 1),
    -- My Global Reward (101): partial
    (101, 1, 0), (101, 2, 2), (101, 3, 1), (101, 4, 0),
    -- Bluegreen (12852): Alerting
    (12852, 1, 3), (12852, 2, 3), (12852, 3, 3), (12852, 4, 3),
    -- CURewards (14452): Observed
    (14452, 1, 1), (14452, 2, 1), (14452, 3, 1), (14452, 4, 1),
    -- tripsavr (11395): partial (Cars/Hotels only in Domain)
    (11395, 1, 2), (11395, 3, 0),

    -- ===== NG (ClubId = 1000000 + MemberClassId; no Domain — invent products for UI demo) =====
    -- Cruise Points (1000001): Observed
    (1000001, 1, 1), (1000001, 3, 1),
    -- Cruise Points CSA (1000002): partial
    (1000002, 1, 0), (1000002, 2, 2), (1000002, 3, 1),
    -- ICE Platinum (1000003): Alerting
    (1000003, 1, 3), (1000003, 2, 3), (1000003, 3, 3), (1000003, 4, 3),
    -- RCI Registry (1000004): Agentically
    (1000004, 2, 2), (1000004, 3, 2),
    -- Registry Vacations (1000005): partial
    (1000005, 1, 1), (1000005, 4, 0)
) AS source (ClubId, ProductId, MaturityLevelId)
ON target.ClubId = source.ClubId AND target.ProductId = source.ProductId
WHEN MATCHED THEN
    UPDATE SET MaturityLevelId = source.MaturityLevelId
WHEN NOT MATCHED BY TARGET THEN
    INSERT (ClubId, ProductId, MaturityLevelId)
    VALUES (source.ClubId, source.ProductId, source.MaturityLevelId);
GO

-- Summary by platform (demo clubs only)
SELECT c.FusionPlatform,
       SUM(CASE WHEN bucket = N'observed' THEN 1 ELSE 0 END) AS ObservedClubs,
       SUM(CASE WHEN bucket = N'partial' THEN 1 ELSE 0 END) AS PartialClubs,
       SUM(CASE WHEN bucket = N'not' THEN 1 ELSE 0 END) AS NotObservedClubs
FROM (
    SELECT c.ClubId, c.FusionPlatform,
           CASE
               WHEN COUNT(*) = 0 THEN N'not'
               WHEN MIN(m.MaturityLevelId) >= 1 THEN N'observed'
               WHEN MAX(m.MaturityLevelId) >= 1 THEN N'partial'
               ELSE N'not'
           END AS bucket
    FROM brand.ClubInventory AS c
    LEFT JOIN brand.ClubProductMaturityStatus AS m ON m.ClubId = c.ClubId
    WHERE c.ClubId IN (
        15639, 15662, 15491, 15827, 15811,
        15730, 15797, 13530, 15439, 14109,
        12715, 101, 12852, 14452, 11395,
        1000001, 1000002, 1000003, 1000004, 1000005
    )
    GROUP BY c.ClubId, c.FusionPlatform
) AS x
JOIN brand.ClubInventory AS c ON c.ClubId = x.ClubId
GROUP BY c.FusionPlatform
ORDER BY c.FusionPlatform;
GO
