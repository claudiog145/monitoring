-- Specification 1000 - seeds the default KPI rules of the incumbent company profile (ProfileKey = N'arrivia').
--   * 24 domain-grain rules (6 KPIs x 4 domains, Stage = '', contract v1-v3). Only Application/Latency and
--     Application/Exceptions for hotels start enabled.
--   * Contract v4 amendment A3: domain-grain Latency and Exceptions for the 'shared' service (login, checkout,
--     booking, confirmation), enabled, same methods as the hotels rules.
--   * Contract v4 section 1 / SD6: entity-grain Latency P95 and Exceptions count for hotels, cars, cruises and
--     shared. Stage = '' aggregates every stage of the service per entity (amendment A1); bucket 1 h like the
--     daemon, RobustZScore {windowBuckets:24, zThreshold:3}, 2 of 2, cooldown 120 min, MinObservationsPerBucket
--     30 (latency) / 0 (exceptions), MaxEntitiesPerBucket 500, enabled (they only feed the Scout shadow).
-- Idempotent and non-destructive: inserts only the rules missing for (ProfileKey, Pillar, Kpi, Domain, Stage, Grain)
-- and never overwrites a rule someone already tuned from the Settings tab (no UPDATE branch).
-- docker/init-sql.sh runs this script after CreateMonitoringScoutShadow.sql (v4 Grain column), so a brand-new
-- database gets every rule on the first run. Run on its own against a pre-v4 database (no Grain column), it seeds
-- only the domain rules on the v3 key and the entity rules on the first run after the v4 migration.

IF OBJECT_ID(N'tempdb..#KpiSeed') IS NOT NULL DROP TABLE #KpiSeed;
GO

SELECT N'arrivia' AS ProfileKey, v.*
INTO #KpiSeed
FROM (VALUES
    (N'Application', N'Latency',          N'hotels',  N'', N'appinsights:latency',    N'Increase', N'P95',   300,  3600, 86400,  4000, 30, N'BaselinePercent', N'{"lookbackDays":7,"thresholdPercent":50}', 2, 120, 1, N'domain', 500),
    (N'Application', N'Latency',          N'cars',    N'', N'appinsights:latency',    N'Increase', N'P95',   300,  3600, 86400,  4000, 30, N'BaselinePercent', N'{"lookbackDays":7,"thresholdPercent":50}', 2, 120, 0, N'domain', 500),
    (N'Application', N'Latency',          N'cruises', N'', N'appinsights:latency',    N'Increase', N'P95',   300,  3600, 86400,  4000, 30, N'BaselinePercent', N'{"lookbackDays":7,"thresholdPercent":50}', 2, 120, 0, N'domain', 500),
    (N'Application', N'Latency',          N'air',     N'', N'appinsights:latency',    N'Increase', N'P95',   300,  3600, 86400,  4000, 30, N'BaselinePercent', N'{"lookbackDays":7,"thresholdPercent":50}', 2, 120, 0, N'domain', 500),
    (N'Application', N'Latency',          N'shared',  N'', N'appinsights:latency',    N'Increase', N'P95',   300,  3600, 86400,  4000, 30, N'BaselinePercent', N'{"lookbackDays":7,"thresholdPercent":50}', 2, 120, 1, N'domain', 500),

    (N'Application', N'Exceptions',       N'hotels',  N'', N'appinsights:exceptions', N'Increase', N'Count', 300,  3600, 86400,  0,    0,  N'RobustZScore',    N'{"windowBuckets":24,"zThreshold":3}',      2, 120, 1, N'domain', 500),
    (N'Application', N'Exceptions',       N'cars',    N'', N'appinsights:exceptions', N'Increase', N'Count', 300,  3600, 86400,  0,    0,  N'RobustZScore',    N'{"windowBuckets":24,"zThreshold":3}',      2, 120, 0, N'domain', 500),
    (N'Application', N'Exceptions',       N'cruises', N'', N'appinsights:exceptions', N'Increase', N'Count', 300,  3600, 86400,  0,    0,  N'RobustZScore',    N'{"windowBuckets":24,"zThreshold":3}',      2, 120, 0, N'domain', 500),
    (N'Application', N'Exceptions',       N'air',     N'', N'appinsights:exceptions', N'Increase', N'Count', 300,  3600, 86400,  0,    0,  N'RobustZScore',    N'{"windowBuckets":24,"zThreshold":3}',      2, 120, 0, N'domain', 500),
    (N'Application', N'Exceptions',       N'shared',  N'', N'appinsights:exceptions', N'Increase', N'Count', 300,  3600, 86400,  0,    0,  N'RobustZScore',    N'{"windowBuckets":24,"zThreshold":3}',      2, 120, 1, N'domain', 500),

    (N'Business',    N'Bookings',         N'hotels',  N'', N'fabric:bookings',        N'Decrease', N'Count', 3600, 3600, 604800, 0,    20, N'BaselinePercent', N'{"lookbackDays":7,"thresholdPercent":30}', 3, 240, 0, N'domain', 500),
    (N'Business',    N'Bookings',         N'cars',    N'', N'fabric:bookings',        N'Decrease', N'Count', 3600, 3600, 604800, 0,    20, N'BaselinePercent', N'{"lookbackDays":7,"thresholdPercent":30}', 3, 240, 0, N'domain', 500),
    (N'Business',    N'Bookings',         N'cruises', N'', N'fabric:bookings',        N'Decrease', N'Count', 3600, 3600, 604800, 0,    20, N'BaselinePercent', N'{"lookbackDays":7,"thresholdPercent":30}', 3, 240, 0, N'domain', 500),
    (N'Business',    N'Bookings',         N'air',     N'', N'fabric:bookings',        N'Decrease', N'Count', 3600, 3600, 604800, 0,    20, N'BaselinePercent', N'{"lookbackDays":7,"thresholdPercent":30}', 3, 240, 0, N'domain', 500),

    (N'Business',    N'Revenue',          N'hotels',  N'', N'fabric:revenue',         N'Decrease', N'Value', 3600, 3600, 604800, 0,    20, N'BaselinePercent', N'{"lookbackDays":7,"thresholdPercent":30}', 3, 240, 0, N'domain', 500),
    (N'Business',    N'Revenue',          N'cars',    N'', N'fabric:revenue',         N'Decrease', N'Value', 3600, 3600, 604800, 0,    20, N'BaselinePercent', N'{"lookbackDays":7,"thresholdPercent":30}', 3, 240, 0, N'domain', 500),
    (N'Business',    N'Revenue',          N'cruises', N'', N'fabric:revenue',         N'Decrease', N'Value', 3600, 3600, 604800, 0,    20, N'BaselinePercent', N'{"lookbackDays":7,"thresholdPercent":30}', 3, 240, 0, N'domain', 500),
    (N'Business',    N'Revenue',          N'air',     N'', N'fabric:revenue',         N'Decrease', N'Value', 3600, 3600, 604800, 0,    20, N'BaselinePercent', N'{"lookbackDays":7,"thresholdPercent":30}', 3, 240, 0, N'domain', 500),

    (N'Business',    N'ConversionRate',   N'hotels',  N'', N'fabric:conversion',      N'Decrease', N'Value', 3600, 3600, 604800, 0,    20, N'BaselinePercent', N'{"lookbackDays":7,"thresholdPercent":30}', 3, 240, 0, N'domain', 500),
    (N'Business',    N'ConversionRate',   N'cars',    N'', N'fabric:conversion',      N'Decrease', N'Value', 3600, 3600, 604800, 0,    20, N'BaselinePercent', N'{"lookbackDays":7,"thresholdPercent":30}', 3, 240, 0, N'domain', 500),
    (N'Business',    N'ConversionRate',   N'cruises', N'', N'fabric:conversion',      N'Decrease', N'Value', 3600, 3600, 604800, 0,    20, N'BaselinePercent', N'{"lookbackDays":7,"thresholdPercent":30}', 3, 240, 0, N'domain', 500),
    (N'Business',    N'ConversionRate',   N'air',     N'', N'fabric:conversion',      N'Decrease', N'Value', 3600, 3600, 604800, 0,    20, N'BaselinePercent', N'{"lookbackDays":7,"thresholdPercent":30}', 3, 240, 0, N'domain', 500),

    (N'Business',    N'CancellationRate', N'hotels',  N'', N'fabric:cancellation',    N'Increase', N'Value', 3600, 3600, 604800, 0,    20, N'BaselinePercent', N'{"lookbackDays":7,"thresholdPercent":30}', 3, 240, 0, N'domain', 500),
    (N'Business',    N'CancellationRate', N'cars',    N'', N'fabric:cancellation',    N'Increase', N'Value', 3600, 3600, 604800, 0,    20, N'BaselinePercent', N'{"lookbackDays":7,"thresholdPercent":30}', 3, 240, 0, N'domain', 500),
    (N'Business',    N'CancellationRate', N'cruises', N'', N'fabric:cancellation',    N'Increase', N'Value', 3600, 3600, 604800, 0,    20, N'BaselinePercent', N'{"lookbackDays":7,"thresholdPercent":30}', 3, 240, 0, N'domain', 500),
    (N'Business',    N'CancellationRate', N'air',     N'', N'fabric:cancellation',    N'Increase', N'Value', 3600, 3600, 604800, 0,    20, N'BaselinePercent', N'{"lookbackDays":7,"thresholdPercent":30}', 3, 240, 0, N'domain', 500),

    (N'Application', N'Latency',          N'hotels',  N'', N'appinsights:latency',    N'Increase', N'P95',   300,  3600, 86400,  0,    30, N'RobustZScore',    N'{"windowBuckets":24,"zThreshold":3}',      2, 120, 1, N'entity', 500),
    (N'Application', N'Latency',          N'cars',    N'', N'appinsights:latency',    N'Increase', N'P95',   300,  3600, 86400,  0,    30, N'RobustZScore',    N'{"windowBuckets":24,"zThreshold":3}',      2, 120, 1, N'entity', 500),
    (N'Application', N'Latency',          N'cruises', N'', N'appinsights:latency',    N'Increase', N'P95',   300,  3600, 86400,  0,    30, N'RobustZScore',    N'{"windowBuckets":24,"zThreshold":3}',      2, 120, 1, N'entity', 500),
    (N'Application', N'Latency',          N'shared',  N'', N'appinsights:latency',    N'Increase', N'P95',   300,  3600, 86400,  0,    30, N'RobustZScore',    N'{"windowBuckets":24,"zThreshold":3}',      2, 120, 1, N'entity', 500),

    (N'Application', N'Exceptions',       N'hotels',  N'', N'appinsights:exceptions', N'Increase', N'Count', 300,  3600, 86400,  0,    0,  N'RobustZScore',    N'{"windowBuckets":24,"zThreshold":3}',      2, 120, 1, N'entity', 500),
    (N'Application', N'Exceptions',       N'cars',    N'', N'appinsights:exceptions', N'Increase', N'Count', 300,  3600, 86400,  0,    0,  N'RobustZScore',    N'{"windowBuckets":24,"zThreshold":3}',      2, 120, 1, N'entity', 500),
    (N'Application', N'Exceptions',       N'cruises', N'', N'appinsights:exceptions', N'Increase', N'Count', 300,  3600, 86400,  0,    0,  N'RobustZScore',    N'{"windowBuckets":24,"zThreshold":3}',      2, 120, 1, N'entity', 500),
    (N'Application', N'Exceptions',       N'shared',  N'', N'appinsights:exceptions', N'Increase', N'Count', 300,  3600, 86400,  0,    0,  N'RobustZScore',    N'{"windowBuckets":24,"zThreshold":3}',      2, 120, 1, N'entity', 500)
) AS v (Pillar, Kpi, Domain, Stage, SourceKey, Direction, Aggregation, EvaluationFrequencySec, BucketSizeSec,
        LookbackWindowSec, SampleSize, MinObservationsPerBucket, DetectionMethod, MethodParamsJson,
        ConsecutiveCount, CooldownMinutes, Enabled, Grain, MaxEntitiesPerBucket);

-- Contract v1 CK_monitoring_KpiDefinition_Domain only accepts hotels/cars/cruises/air. While it is in place the
-- 'shared' rules (amendment A3) are skipped instead of failing the whole script; they are seeded by the first
-- run after the constraint is relaxed to the profile's services (contract v4 section 5).
DECLARE @domainCheck nvarchar(max) =
    (SELECT definition FROM sys.check_constraints
     WHERE parent_object_id = OBJECT_ID(N'monitoring.KpiDefinition') AND name = N'CK_monitoring_KpiDefinition_Domain');
DELETE FROM #KpiSeed WHERE @domainCheck IS NOT NULL AND @domainCheck NOT LIKE N'%''' + Domain + N'''%';
IF @@ROWCOUNT > 0
    PRINT N'SeedKpiDefinitions: rules for domains rejected by CK_monitoring_KpiDefinition_Domain skipped.';
GO

-- Pre-v4 database (Grain not migrated yet): domain rules only, on the v3 natural key.
IF COL_LENGTH(N'monitoring.KpiDefinition', N'Grain') IS NULL
BEGIN
    MERGE monitoring.KpiDefinition AS t
    USING (SELECT * FROM #KpiSeed WHERE Grain = N'domain') AS s
    ON t.ProfileKey = s.ProfileKey AND t.Pillar = s.Pillar AND t.Kpi = s.Kpi AND t.Domain = s.Domain AND t.Stage = s.Stage
    WHEN NOT MATCHED THEN
        INSERT (Pillar, Kpi, Domain, Stage, SourceKey, Direction, Aggregation, EvaluationFrequencySec, BucketSizeSec,
                LookbackWindowSec, SampleSize, MinObservationsPerBucket, DetectionMethod, MethodParamsJson,
                ConsecutiveCount, CooldownMinutes, Enabled, UpdatedBy, ProfileKey)
        VALUES (s.Pillar, s.Kpi, s.Domain, s.Stage, s.SourceKey, s.Direction, s.Aggregation, s.EvaluationFrequencySec,
                s.BucketSizeSec, s.LookbackWindowSec, s.SampleSize, s.MinObservationsPerBucket, s.DetectionMethod,
                s.MethodParamsJson, s.ConsecutiveCount, s.CooldownMinutes, s.Enabled, N'seed', s.ProfileKey);
    PRINT N'SeedKpiDefinitions: Grain column missing, entity-grain rules deferred to the next run.';
END
GO

-- v4 database: every rule on the v4 natural key (ProfileKey, Pillar, Kpi, Domain, Stage, Grain).
-- Dynamic SQL so this batch still compiles on a pre-v4 database.
IF COL_LENGTH(N'monitoring.KpiDefinition', N'Grain') IS NOT NULL
    EXEC (N'
    MERGE monitoring.KpiDefinition AS t
    USING #KpiSeed AS s
    ON t.ProfileKey = s.ProfileKey AND t.Pillar = s.Pillar AND t.Kpi = s.Kpi AND t.Domain = s.Domain
       AND t.Stage = s.Stage AND t.Grain = s.Grain
    WHEN NOT MATCHED THEN
        INSERT (Pillar, Kpi, Domain, Stage, SourceKey, Direction, Aggregation, EvaluationFrequencySec, BucketSizeSec,
                LookbackWindowSec, SampleSize, MinObservationsPerBucket, DetectionMethod, MethodParamsJson,
                ConsecutiveCount, CooldownMinutes, Enabled, UpdatedBy, ProfileKey, Grain, MaxEntitiesPerBucket)
        VALUES (s.Pillar, s.Kpi, s.Domain, s.Stage, s.SourceKey, s.Direction, s.Aggregation, s.EvaluationFrequencySec,
                s.BucketSizeSec, s.LookbackWindowSec, s.SampleSize, s.MinObservationsPerBucket, s.DetectionMethod,
                s.MethodParamsJson, s.ConsecutiveCount, s.CooldownMinutes, s.Enabled, N''seed'', s.ProfileKey,
                s.Grain, s.MaxEntitiesPerBucket);');
GO

DROP TABLE #KpiSeed;
GO