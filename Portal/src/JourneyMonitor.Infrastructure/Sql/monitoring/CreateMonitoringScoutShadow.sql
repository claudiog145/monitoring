-- Specification 1000 contract v4 - entity grain (section 1) and Scout shadow comparison (section 4).
-- Runs after CreateMonitoringKpi.sql (see docker/init-sql.sh), which is frozen at contract v3 and creates
-- the v3 tables; this script migrates them in place and owns the v4 versions of the KPI procedures.
-- Idempotent and safe on databases with data, in guarded steps that never drop rows:
--   1. columns: KpiDefinition.Grain/MaxEntitiesPerBucket, EntityKey on KpiBucket/KpiDetectionState/
--      KpiAnomaly (DEFAULT N'' turns every existing row into the domain aggregate), KpiBucket.FailedCount;
--      the v3 fixed-domain CHECK on KpiDefinition.Domain is replaced by a non-empty CHECK (AG1);
--   2. keys: the KpiDefinition natural key and the PKs of KpiBucket/KpiDetectionState are recreated only
--      while they still lack Grain/EntityKey (rows unique on the old key are unique on the wider one);
--   3. indexes: IX_monitoring_KpiAnomaly_Entity;
--   4. v4 KPI procedures (CREATE OR ALTER): every v3 parameter list still works, new parameters default to
--      the domain aggregate (EntityKey N'') or, for KpiDefinition.Grain/MaxEntitiesPerBucket, keep the
--      stored value on update;
--   5. monitoring.ScoutShadowComparison and its procedures.
-- Entity buckets (EntityKey <> N'') are kept 8 days: usp_UpsertKpiBuckets purges older ones of the
-- definitions it writes. Domain buckets are never purged here.
-- After each Scout cycle one ScoutShadowComparison row is written per (EntityKey, Stage, BucketStart) flagged
-- by the fixed rules, by the entity detection method, or by both; rows where neither side flags anything are
-- never stored. One row per (Board, ProfileKey, EntityKey, Stage, BucketStart): overlapping windows update it.
-- Shadow rows are kept 8 days, like entity buckets.

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'monitoring')
    EXEC(N'CREATE SCHEMA monitoring');
GO

-- ------------------------------------------------------------------ 1-3. KPI entity grain migration

-- Contract v4 section 1, step 1 (columns) for KpiDefinition: the grain is part of the natural key.
IF COL_LENGTH(N'monitoring.KpiDefinition', N'Grain') IS NULL
    ALTER TABLE monitoring.KpiDefinition
        ADD Grain nvarchar(16) NOT NULL CONSTRAINT DF_monitoring_KpiDefinition_Grain DEFAULT N'domain';
GO

IF COL_LENGTH(N'monitoring.KpiDefinition', N'MaxEntitiesPerBucket') IS NULL
    ALTER TABLE monitoring.KpiDefinition
        ADD MaxEntitiesPerBucket int NOT NULL CONSTRAINT DF_monitoring_KpiDefinition_MaxEntities DEFAULT 500;
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints
               WHERE parent_object_id = OBJECT_ID(N'monitoring.KpiDefinition') AND name = N'CK_monitoring_KpiDefinition_Grain')
    ALTER TABLE monitoring.KpiDefinition
        ADD CONSTRAINT CK_monitoring_KpiDefinition_Grain CHECK (Grain IN (N'domain', N'entity'));
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints
               WHERE parent_object_id = OBJECT_ID(N'monitoring.KpiDefinition') AND name = N'CK_monitoring_KpiDefinition_MaxEntities')
    ALTER TABLE monitoring.KpiDefinition
        ADD CONSTRAINT CK_monitoring_KpiDefinition_MaxEntities CHECK (MaxEntitiesPerBucket BETWEEN 1 AND 5000);
GO

-- Contract v4 section 5 (AG1): a KPI domain is a service key of its company profile, validated by the API,
-- so the v3 fixed list of incumbent domains goes; the column only requires a non-empty value.
IF EXISTS (SELECT 1 FROM sys.check_constraints
           WHERE parent_object_id = OBJECT_ID(N'monitoring.KpiDefinition') AND name = N'CK_monitoring_KpiDefinition_Domain')
    ALTER TABLE monitoring.KpiDefinition DROP CONSTRAINT CK_monitoring_KpiDefinition_Domain;
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints
               WHERE parent_object_id = OBJECT_ID(N'monitoring.KpiDefinition') AND name = N'CK_monitoring_KpiDefinition_DomainNotEmpty')
    ALTER TABLE monitoring.KpiDefinition
        ADD CONSTRAINT CK_monitoring_KpiDefinition_DomainNotEmpty CHECK (LEN(Domain) >= 1);
GO

-- The natural key is per profile and per grain: recreate it only when it still lacks ProfileKey or Grain
-- (or is missing), drop and add in one transaction so a failure leaves the old key in place.
SET XACT_ABORT ON;
IF NOT EXISTS (SELECT 1 FROM sys.key_constraints
               WHERE parent_object_id = OBJECT_ID(N'monitoring.KpiDefinition') AND name = N'UQ_monitoring_KpiDefinition_Key')
   OR (SELECT COUNT(*)
       FROM sys.key_constraints AS kc
       INNER JOIN sys.index_columns AS ic ON ic.object_id = kc.parent_object_id AND ic.index_id = kc.unique_index_id
       INNER JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
       WHERE kc.parent_object_id = OBJECT_ID(N'monitoring.KpiDefinition')
         AND kc.name = N'UQ_monitoring_KpiDefinition_Key'
         AND c.name IN (N'ProfileKey', N'Grain')) < 2
BEGIN
    BEGIN TRANSACTION;
    IF EXISTS (SELECT 1 FROM sys.key_constraints
               WHERE parent_object_id = OBJECT_ID(N'monitoring.KpiDefinition') AND name = N'UQ_monitoring_KpiDefinition_Key')
        ALTER TABLE monitoring.KpiDefinition DROP CONSTRAINT UQ_monitoring_KpiDefinition_Key;
    ALTER TABLE monitoring.KpiDefinition
        ADD CONSTRAINT UQ_monitoring_KpiDefinition_Key UNIQUE (ProfileKey, Pillar, Kpi, Domain, Stage, Grain);
    COMMIT TRANSACTION;
END
GO

-- Step 1 (KpiBucket, KpiDetectionState, KpiAnomaly): columns. DEFAULT N'' turns every existing row into the domain aggregate.
IF COL_LENGTH(N'monitoring.KpiBucket', N'EntityKey') IS NULL
    ALTER TABLE monitoring.KpiBucket
        ADD EntityKey nvarchar(256) NOT NULL CONSTRAINT DF_monitoring_KpiBucket_EntityKey DEFAULT N'';
GO

IF COL_LENGTH(N'monitoring.KpiBucket', N'FailedCount') IS NULL
    ALTER TABLE monitoring.KpiBucket ADD FailedCount bigint NULL;
GO

IF COL_LENGTH(N'monitoring.KpiDetectionState', N'EntityKey') IS NULL
    ALTER TABLE monitoring.KpiDetectionState
        ADD EntityKey nvarchar(256) NOT NULL CONSTRAINT DF_monitoring_KpiDetectionState_EntityKey DEFAULT N'';
GO

IF COL_LENGTH(N'monitoring.KpiAnomaly', N'EntityKey') IS NULL
    ALTER TABLE monitoring.KpiAnomaly
        ADD EntityKey nvarchar(256) NOT NULL CONSTRAINT DF_monitoring_KpiAnomaly_EntityKey DEFAULT N'';
GO

-- Step 2: primary keys. Swapped only while the current PK lacks EntityKey (or is missing), inside one
-- transaction so a failure leaves the old key in place. Rows are untouched: the old key was unique and
-- every existing row has EntityKey = N'', so the wider key is unique too.
SET XACT_ABORT ON;
IF NOT EXISTS (SELECT 1
               FROM sys.key_constraints AS kc
               INNER JOIN sys.index_columns AS ic ON ic.object_id = kc.parent_object_id AND ic.index_id = kc.unique_index_id
               INNER JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
               WHERE kc.parent_object_id = OBJECT_ID(N'monitoring.KpiBucket') AND kc.type = N'PK' AND c.name = N'EntityKey')
BEGIN
    BEGIN TRANSACTION;
    DECLARE @BucketPk sysname = (SELECT name FROM sys.key_constraints
                                 WHERE parent_object_id = OBJECT_ID(N'monitoring.KpiBucket') AND type = N'PK');
    IF @BucketPk IS NOT NULL
    BEGIN
        DECLARE @DropBucketPk nvarchar(400) = N'ALTER TABLE monitoring.KpiBucket DROP CONSTRAINT ' + QUOTENAME(@BucketPk) + N';';
        EXEC sys.sp_executesql @DropBucketPk;
    END
    ALTER TABLE monitoring.KpiBucket
        ADD CONSTRAINT PK_monitoring_KpiBucket PRIMARY KEY (KpiDefinitionId, EntityKey, BucketStart);
    COMMIT TRANSACTION;
END
GO

SET XACT_ABORT ON;
IF NOT EXISTS (SELECT 1
               FROM sys.key_constraints AS kc
               INNER JOIN sys.index_columns AS ic ON ic.object_id = kc.parent_object_id AND ic.index_id = kc.unique_index_id
               INNER JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
               WHERE kc.parent_object_id = OBJECT_ID(N'monitoring.KpiDetectionState') AND kc.type = N'PK' AND c.name = N'EntityKey')
BEGIN
    BEGIN TRANSACTION;
    DECLARE @StatePk sysname = (SELECT name FROM sys.key_constraints
                                WHERE parent_object_id = OBJECT_ID(N'monitoring.KpiDetectionState') AND type = N'PK');
    IF @StatePk IS NOT NULL
    BEGIN
        DECLARE @DropStatePk nvarchar(400) = N'ALTER TABLE monitoring.KpiDetectionState DROP CONSTRAINT ' + QUOTENAME(@StatePk) + N';';
        EXEC sys.sp_executesql @DropStatePk;
    END
    ALTER TABLE monitoring.KpiDetectionState
        ADD CONSTRAINT PK_monitoring_KpiDetectionState PRIMARY KEY (KpiDefinitionId, EntityKey);
    COMMIT TRANSACTION;
END
GO

-- Step 3: indexes.
SET XACT_ABORT OFF;
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE object_id = OBJECT_ID(N'monitoring.KpiAnomaly') AND name = N'IX_monitoring_KpiAnomaly_Entity')
    CREATE INDEX IX_monitoring_KpiAnomaly_Entity ON monitoring.KpiAnomaly (KpiDefinitionId, EntityKey, DetectedAt DESC);
GO

-- SD4: EntityDetectionMonitor reads every entity of a definition by time range (usp_GetKpiEntityBuckets); the PK
-- (KpiDefinitionId, EntityKey, BucketStart) cannot seek on BucketStart without EntityKey.
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE object_id = OBJECT_ID(N'monitoring.KpiBucket') AND name = N'IX_monitoring_KpiBucket_DefinitionTime')
    CREATE INDEX IX_monitoring_KpiBucket_DefinitionTime ON monitoring.KpiBucket (KpiDefinitionId, BucketStart)
        INCLUDE (EntityKey, TotalCount, P50, P95, Mean, Value, FailedCount);
GO

-- ------------------------------------------------------------------ 4. KPI procedures (v4)

CREATE OR ALTER PROCEDURE monitoring.usp_GetKpiDefinitions
    @Pillar      nvarchar(16) = NULL,
    @OnlyEnabled bit = 0,
    @ProfileKey  nvarchar(128) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    SELECT KpiDefinitionId, Pillar, Kpi, Domain, Stage, SourceKey, Direction, Aggregation,
           EvaluationFrequencySec, BucketSizeSec, LookbackWindowSec, SampleSize, MinObservationsPerBucket,
           DetectionMethod, MethodParamsJson, ConsecutiveCount, CooldownMinutes, Enabled, UpdatedAt, UpdatedBy, ProfileKey,
           Grain, MaxEntitiesPerBucket
    FROM monitoring.KpiDefinition
    WHERE (@Pillar IS NULL OR Pillar = @Pillar)
      AND (@OnlyEnabled = 0 OR Enabled = 1)
      AND (@ProfileKey IS NULL OR ProfileKey = @ProfileKey)
    ORDER BY KpiDefinitionId;
END
GO

CREATE OR ALTER PROCEDURE monitoring.usp_GetKpiDefinition
    @KpiDefinitionId int
AS
BEGIN
    SET NOCOUNT ON;

    SELECT KpiDefinitionId, Pillar, Kpi, Domain, Stage, SourceKey, Direction, Aggregation,
           EvaluationFrequencySec, BucketSizeSec, LookbackWindowSec, SampleSize, MinObservationsPerBucket,
           DetectionMethod, MethodParamsJson, ConsecutiveCount, CooldownMinutes, Enabled, UpdatedAt, UpdatedBy, ProfileKey,
           Grain, MaxEntitiesPerBucket
    FROM monitoring.KpiDefinition
    WHERE KpiDefinitionId = @KpiDefinitionId;
END
GO

CREATE OR ALTER PROCEDURE monitoring.usp_UpsertKpiDefinition
    @KpiDefinitionId          int = 0,
    @Pillar                   nvarchar(16),
    @Kpi                      nvarchar(64),
    @Domain                   nvarchar(32),
    @Stage                    nvarchar(64) = N'',
    @SourceKey                nvarchar(64),
    @Direction                nvarchar(16),
    @Aggregation              nvarchar(8),
    @EvaluationFrequencySec   int,
    @BucketSizeSec            int,
    @LookbackWindowSec        int,
    @SampleSize               int,
    @MinObservationsPerBucket int,
    @DetectionMethod          nvarchar(32),
    @MethodParamsJson         nvarchar(1000),
    @ConsecutiveCount         int,
    @CooldownMinutes          int,
    @Enabled                  bit,
    @ChangedBy                nvarchar(128),
    @ProfileKey               nvarchar(128),
    -- NULL keeps the stored value on update (callers written before contract v4) and means the
    -- column default on insert.
    @Grain                    nvarchar(16) = NULL,
    @MaxEntitiesPerBucket     int = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now datetimeoffset(7) = SYSDATETIMEOFFSET();
    DECLARE @Id int = @KpiDefinitionId;

    BEGIN TRANSACTION;

    IF @Id = 0
    BEGIN
        INSERT INTO monitoring.KpiDefinition
            (Pillar, Kpi, Domain, Stage, SourceKey, Direction, Aggregation,
             EvaluationFrequencySec, BucketSizeSec, LookbackWindowSec, SampleSize, MinObservationsPerBucket,
             DetectionMethod, MethodParamsJson, ConsecutiveCount, CooldownMinutes, Enabled, UpdatedAt, UpdatedBy, ProfileKey,
             Grain, MaxEntitiesPerBucket)
        VALUES
            (@Pillar, @Kpi, @Domain, ISNULL(@Stage, N''), @SourceKey, @Direction, @Aggregation,
             @EvaluationFrequencySec, @BucketSizeSec, @LookbackWindowSec, @SampleSize, @MinObservationsPerBucket,
             @DetectionMethod, @MethodParamsJson, @ConsecutiveCount, @CooldownMinutes, @Enabled, @Now, @ChangedBy, @ProfileKey,
             ISNULL(@Grain, N'domain'), ISNULL(@MaxEntitiesPerBucket, 500));

        SET @Id = CAST(SCOPE_IDENTITY() AS int);
    END
    ELSE
    BEGIN
        UPDATE monitoring.KpiDefinition
        SET Pillar = @Pillar,
            Kpi = @Kpi,
            Domain = @Domain,
            Stage = ISNULL(@Stage, N''),
            SourceKey = @SourceKey,
            Direction = @Direction,
            Aggregation = @Aggregation,
            EvaluationFrequencySec = @EvaluationFrequencySec,
            BucketSizeSec = @BucketSizeSec,
            LookbackWindowSec = @LookbackWindowSec,
            SampleSize = @SampleSize,
            MinObservationsPerBucket = @MinObservationsPerBucket,
            DetectionMethod = @DetectionMethod,
            MethodParamsJson = @MethodParamsJson,
            ConsecutiveCount = @ConsecutiveCount,
            CooldownMinutes = @CooldownMinutes,
            Enabled = @Enabled,
            UpdatedAt = @Now,
            UpdatedBy = @ChangedBy,
            ProfileKey = @ProfileKey,
            Grain = ISNULL(@Grain, Grain),
            MaxEntitiesPerBucket = ISNULL(@MaxEntitiesPerBucket, MaxEntitiesPerBucket)
        WHERE KpiDefinitionId = @Id;

        IF @@ROWCOUNT = 0
        BEGIN
            ROLLBACK TRANSACTION;
            THROW 50001, 'KpiDefinition not found', 1;
        END
    END

    INSERT INTO monitoring.KpiDefinitionAudit (KpiDefinitionId, SnapshotJson, ChangedAt, ChangedBy, ProfileKey)
    VALUES
        (@Id,
         (SELECT * FROM monitoring.KpiDefinition WHERE KpiDefinitionId = @Id FOR JSON PATH, WITHOUT_ARRAY_WRAPPER),
         @Now,
         @ChangedBy,
         @ProfileKey);

    COMMIT TRANSACTION;

    SELECT KpiDefinitionId, Pillar, Kpi, Domain, Stage, SourceKey, Direction, Aggregation,
           EvaluationFrequencySec, BucketSizeSec, LookbackWindowSec, SampleSize, MinObservationsPerBucket,
           DetectionMethod, MethodParamsJson, ConsecutiveCount, CooldownMinutes, Enabled, UpdatedAt, UpdatedBy, ProfileKey,
           Grain, MaxEntitiesPerBucket
    FROM monitoring.KpiDefinition
    WHERE KpiDefinitionId = @Id;
END
GO

CREATE OR ALTER PROCEDURE monitoring.usp_UpsertKpiBuckets
    @BucketsJson nvarchar(max)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now datetimeoffset(7) = SYSDATETIMEOFFSET();
    -- Entity buckets are kept 8 days (contract v4 section 1); domain buckets are not purged here.
    DECLARE @EntityCutoff datetimeoffset(7) = DATEADD(DAY, -8, @Now);

    DECLARE @Incoming TABLE
    (
        KpiDefinitionId int               NOT NULL,
        EntityKey       nvarchar(256)     NOT NULL,
        BucketStart     datetimeoffset(7) NOT NULL,
        TotalCount      bigint            NOT NULL,
        SampledCount    int               NOT NULL,
        P50             float             NULL,
        P95             float             NULL,
        Mean            float             NULL,
        StdDev          float             NULL,
        Value           float             NULL,
        FailedCount     bigint            NULL
    );

    INSERT INTO @Incoming (KpiDefinitionId, EntityKey, BucketStart, TotalCount, SampledCount, P50, P95, Mean, StdDev, Value, FailedCount)
    SELECT KpiDefinitionId, ISNULL(EntityKey, N''), BucketStart, TotalCount, SampledCount, P50, P95, Mean, StdDev, Value, FailedCount
    FROM OPENJSON(@BucketsJson)
    WITH
    (
        KpiDefinitionId int               '$.kpiDefinitionId',
        EntityKey       nvarchar(256)     '$.entityKey',
        BucketStart     datetimeoffset(7) '$.bucketStart',
        TotalCount      bigint            '$.totalCount',
        SampledCount    int               '$.sampledCount',
        P50             float             '$.p50',
        P95             float             '$.p95',
        Mean            float             '$.mean',
        StdDev          float             '$.stdDev',
        Value           float             '$.value',
        FailedCount     bigint            '$.failedCount'
    );

    BEGIN TRANSACTION;

    MERGE monitoring.KpiBucket WITH (HOLDLOCK) AS target
    USING @Incoming AS incoming
        ON target.KpiDefinitionId = incoming.KpiDefinitionId
       AND target.EntityKey = incoming.EntityKey
       AND target.BucketStart = incoming.BucketStart
    WHEN MATCHED THEN
        UPDATE SET
            TotalCount = incoming.TotalCount,
            SampledCount = incoming.SampledCount,
            P50 = incoming.P50,
            P95 = incoming.P95,
            Mean = incoming.Mean,
            StdDev = incoming.StdDev,
            Value = incoming.Value,
            FailedCount = incoming.FailedCount,
            IngestedAt = @Now
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (KpiDefinitionId, EntityKey, BucketStart, TotalCount, SampledCount, P50, P95, Mean, StdDev, Value, FailedCount, IngestedAt)
        VALUES (incoming.KpiDefinitionId, incoming.EntityKey, incoming.BucketStart, incoming.TotalCount, incoming.SampledCount,
                incoming.P50, incoming.P95, incoming.Mean, incoming.StdDev, incoming.Value, incoming.FailedCount, @Now);

    DELETE b
    FROM monitoring.KpiBucket AS b
    WHERE b.KpiDefinitionId IN (SELECT DISTINCT KpiDefinitionId FROM @Incoming WHERE EntityKey <> N'')
      AND b.EntityKey <> N''
      AND b.BucketStart < @EntityCutoff;

    COMMIT TRANSACTION;
END
GO

CREATE OR ALTER PROCEDURE monitoring.usp_GetKpiBuckets
    @KpiDefinitionId int,
    @From            datetimeoffset(7),
    @To              datetimeoffset(7),
    @EntityKey       nvarchar(256) = N''
AS
BEGIN
    SET NOCOUNT ON;

    SELECT KpiDefinitionId, BucketStart, TotalCount, SampledCount, P50, P95, Mean, StdDev, Value, EntityKey, FailedCount
    FROM monitoring.KpiBucket
    WHERE KpiDefinitionId = @KpiDefinitionId
      AND EntityKey = ISNULL(@EntityKey, N'')
      AND BucketStart >= @From
      AND BucketStart < @To
    ORDER BY BucketStart;
END
GO

CREATE OR ALTER PROCEDURE monitoring.usp_GetKpiEntityBuckets
    @KpiDefinitionId int,
    @From            datetimeoffset(7),
    @To              datetimeoffset(7)
AS
BEGIN
    SET NOCOUNT ON;

    SELECT KpiDefinitionId, BucketStart, TotalCount, SampledCount, P50, P95, Mean, StdDev, Value, EntityKey, FailedCount
    FROM monitoring.KpiBucket
    WHERE KpiDefinitionId = @KpiDefinitionId
      AND EntityKey <> N''
      AND BucketStart >= @From
      AND BucketStart < @To
    ORDER BY EntityKey, BucketStart;
END
GO

CREATE OR ALTER PROCEDURE monitoring.usp_GetKpiDetectionState
    @KpiDefinitionId int,
    @EntityKey       nvarchar(256) = N''
AS
BEGIN
    SET NOCOUNT ON;

    SELECT KpiDefinitionId, ConsecutiveCount, LastBucketEvaluated, CooldownUntil, LastEvaluatedAt, EntityKey
    FROM monitoring.KpiDetectionState
    WHERE KpiDefinitionId = @KpiDefinitionId
      AND EntityKey = ISNULL(@EntityKey, N'');
END
GO

CREATE OR ALTER PROCEDURE monitoring.usp_GetKpiEntityDetectionStates
    @KpiDefinitionId int
AS
BEGIN
    SET NOCOUNT ON;

    SELECT KpiDefinitionId, ConsecutiveCount, LastBucketEvaluated, CooldownUntil, LastEvaluatedAt, EntityKey
    FROM monitoring.KpiDetectionState
    WHERE KpiDefinitionId = @KpiDefinitionId
      AND EntityKey <> N''
    ORDER BY EntityKey;
END
GO

CREATE OR ALTER PROCEDURE monitoring.usp_SaveKpiDetectionState
    @KpiDefinitionId     int,
    @ConsecutiveCount    int,
    @LastBucketEvaluated datetimeoffset(7) = NULL,
    @CooldownUntil       datetimeoffset(7) = NULL,
    @LastEvaluatedAt     datetimeoffset(7) = NULL,
    @EntityKey           nvarchar(256) = N''
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now datetimeoffset(7) = SYSDATETIMEOFFSET();
    DECLARE @Key nvarchar(256) = ISNULL(@EntityKey, N'');

    MERGE monitoring.KpiDetectionState WITH (HOLDLOCK) AS target
    USING (SELECT @KpiDefinitionId AS KpiDefinitionId, @Key AS EntityKey) AS incoming
        ON target.KpiDefinitionId = incoming.KpiDefinitionId
       AND target.EntityKey = incoming.EntityKey
    WHEN MATCHED THEN
        UPDATE SET
            ConsecutiveCount = @ConsecutiveCount,
            LastBucketEvaluated = @LastBucketEvaluated,
            CooldownUntil = @CooldownUntil,
            LastEvaluatedAt = @LastEvaluatedAt,
            UpdatedAt = @Now
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (KpiDefinitionId, EntityKey, ConsecutiveCount, LastBucketEvaluated, CooldownUntil, LastEvaluatedAt, UpdatedAt)
        VALUES (@KpiDefinitionId, @Key, @ConsecutiveCount, @LastBucketEvaluated, @CooldownUntil, @LastEvaluatedAt, @Now);
END
GO

CREATE OR ALTER PROCEDURE monitoring.usp_AddKpiAnomaly
    @KpiDefinitionId int,
    @DetectedAt      datetimeoffset(7),
    @BucketStart     datetimeoffset(7),
    @ObservedValue   float,
    @ReferenceValue  float,
    @Deviation       float,
    @Detection       nvarchar(400),
    @InvestigationId uniqueidentifier = NULL,
    @EntityKey       nvarchar(256) = N''
AS
BEGIN
    SET NOCOUNT ON;

    INSERT INTO monitoring.KpiAnomaly
        (KpiDefinitionId, DetectedAt, BucketStart, ObservedValue, ReferenceValue, Deviation, Detection, InvestigationId, EntityKey)
    VALUES
        (@KpiDefinitionId, @DetectedAt, @BucketStart, @ObservedValue, @ReferenceValue, @Deviation, @Detection, @InvestigationId,
         ISNULL(@EntityKey, N''));

    SELECT CAST(SCOPE_IDENTITY() AS bigint) AS AnomalyId;
END
GO

CREATE OR ALTER PROCEDURE monitoring.usp_GetRecentKpiAnomalies
    @Pillar nvarchar(16) = NULL,
    @Domain     nvarchar(32) = NULL,
    @Top        int = 100,
    @ProfileKey nvarchar(128) = NULL,
    -- One series only; N'' (the default, also for callers written before contract v4) is the domain aggregate.
    @EntityKey  nvarchar(256) = N''
AS
BEGIN
    SET NOCOUNT ON;

    SELECT TOP (@Top) a.AnomalyId, a.KpiDefinitionId, a.DetectedAt, a.BucketStart, a.ObservedValue,
           a.ReferenceValue, a.Deviation, a.Detection, a.InvestigationId, a.EntityKey
    FROM monitoring.KpiAnomaly AS a
    INNER JOIN monitoring.KpiDefinition AS d
        ON d.KpiDefinitionId = a.KpiDefinitionId
    WHERE (@Pillar IS NULL OR d.Pillar = @Pillar)
      AND (@Domain IS NULL OR d.Domain = @Domain)
      AND (@ProfileKey IS NULL OR d.ProfileKey = @ProfileKey)
      AND a.EntityKey = ISNULL(@EntityKey, N'')
    ORDER BY a.DetectedAt DESC, a.AnomalyId DESC;
END
GO

-- ------------------------------------------------------------------ 5. Scout shadow comparison

IF OBJECT_ID(N'monitoring.ScoutShadowComparison', N'U') IS NULL
BEGIN
    CREATE TABLE monitoring.ScoutShadowComparison
    (
        ShadowId      bigint            NOT NULL IDENTITY(1, 1) CONSTRAINT PK_monitoring_ScoutShadowComparison PRIMARY KEY,
        RunId         uniqueidentifier  NOT NULL,
        Board         nvarchar(16)      NOT NULL,
        ProfileKey    nvarchar(128)     NOT NULL,
        EntityKey     nvarchar(256)     NOT NULL,
        Stage         nvarchar(64)      NOT NULL CONSTRAINT DF_monitoring_ScoutShadowComparison_Stage DEFAULT N'',
        BucketStart   datetimeoffset(7) NOT NULL,
        RuleRank      int               NULL,
        MethodVerdict nvarchar(16)      NOT NULL,
        KpiAnomalyId  bigint            NULL,
        Agreement     nvarchar(16)      NOT NULL,
        CreatedAt     datetimeoffset(7) NOT NULL CONSTRAINT DF_monitoring_ScoutShadowComparison_CreatedAt DEFAULT SYSDATETIMEOFFSET(),
        CONSTRAINT CK_monitoring_ScoutShadowComparison_Verdict CHECK (MethodVerdict IN (N'anomalous', N'normal', N'not-evaluated')),
        CONSTRAINT CK_monitoring_ScoutShadowComparison_Agreement CHECK (Agreement IN (N'both', N'rules-only', N'method-only'))
    );
END
GO

-- One row per (Board, ProfileKey, EntityKey, Stage, BucketStart): a later cycle whose window overlaps an
-- earlier one updates the row (latest RunId wins) instead of adding a second one, so window counts never
-- count the same bucket twice. Databases that got the first, per-RunId key are migrated in one
-- transaction: duplicates of the new key keep only their latest row, then the key is swapped.
SET XACT_ABORT ON;
IF EXISTS (SELECT 1 FROM sys.indexes
           WHERE object_id = OBJECT_ID(N'monitoring.ScoutShadowComparison') AND name = N'UQ_monitoring_ScoutShadowComparison_Row')
BEGIN
    BEGIN TRANSACTION;
    WITH ranked AS
    (
        SELECT ROW_NUMBER() OVER (PARTITION BY Board, ProfileKey, EntityKey, Stage, BucketStart
                                  ORDER BY CreatedAt DESC, ShadowId DESC) AS rn
        FROM monitoring.ScoutShadowComparison
    )
    DELETE FROM ranked WHERE rn > 1;
    DROP INDEX UQ_monitoring_ScoutShadowComparison_Row ON monitoring.ScoutShadowComparison;
    COMMIT TRANSACTION;
END
GO

SET XACT_ABORT OFF;
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE object_id = OBJECT_ID(N'monitoring.ScoutShadowComparison') AND name = N'UQ_monitoring_ScoutShadowComparison_Key')
    CREATE UNIQUE INDEX UQ_monitoring_ScoutShadowComparison_Key
        ON monitoring.ScoutShadowComparison (Board, ProfileKey, EntityKey, Stage, BucketStart);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE object_id = OBJECT_ID(N'monitoring.ScoutShadowComparison') AND name = N'IX_monitoring_ScoutShadowComparison_Board')
    CREATE INDEX IX_monitoring_ScoutShadowComparison_Board
        ON monitoring.ScoutShadowComparison (Board, BucketStart DESC) INCLUDE (ProfileKey, Agreement);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE object_id = OBJECT_ID(N'monitoring.ScoutShadowComparison') AND name = N'IX_monitoring_ScoutShadowComparison_Retention')
    CREATE INDEX IX_monitoring_ScoutShadowComparison_Retention
        ON monitoring.ScoutShadowComparison (BucketStart);
GO

-- Upsert by (Board, ProfileKey, EntityKey, Stage, BucketStart), then purge rows older than 8 days (same
-- retention as entity buckets).
CREATE OR ALTER PROCEDURE monitoring.usp_AddScoutShadowComparisons
    @RowsJson nvarchar(max)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now datetimeoffset(7) = SYSDATETIMEOFFSET();
    DECLARE @Cutoff datetimeoffset(7) = DATEADD(DAY, -8, @Now);

    BEGIN TRANSACTION;

    MERGE monitoring.ScoutShadowComparison WITH (HOLDLOCK) AS target
    USING
    (
        SELECT RunId, Board, ProfileKey, EntityKey, ISNULL(Stage, N'') AS Stage, BucketStart,
               RuleRank, MethodVerdict, KpiAnomalyId, Agreement
        FROM OPENJSON(@RowsJson)
        WITH
        (
            RunId         uniqueidentifier  '$.runId',
            Board         nvarchar(16)      '$.board',
            ProfileKey    nvarchar(128)     '$.profileKey',
            EntityKey     nvarchar(256)     '$.entityKey',
            Stage         nvarchar(64)      '$.stage',
            BucketStart   datetimeoffset(7) '$.bucketStart',
            RuleRank      int               '$.ruleRank',
            MethodVerdict nvarchar(16)      '$.methodVerdict',
            KpiAnomalyId  bigint            '$.kpiAnomalyId',
            Agreement     nvarchar(16)      '$.agreement'
        )
    ) AS incoming
        ON target.Board = incoming.Board
       AND target.ProfileKey = incoming.ProfileKey
       AND target.EntityKey = incoming.EntityKey
       AND target.Stage = incoming.Stage
       AND target.BucketStart = incoming.BucketStart
    -- Never downgrade: once both sides agreed on a key, a later cycle that only sees one side of it (the window
    -- moved past the rules interval, or the board re-ranked it) leaves the row as it is.
    WHEN MATCHED AND (target.Agreement <> N'both' OR incoming.Agreement = N'both') THEN
        UPDATE SET
            RunId = incoming.RunId,
            RuleRank = incoming.RuleRank,
            MethodVerdict = incoming.MethodVerdict,
            KpiAnomalyId = incoming.KpiAnomalyId,
            Agreement = incoming.Agreement,
            CreatedAt = @Now
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (RunId, Board, ProfileKey, EntityKey, Stage, BucketStart, RuleRank, MethodVerdict, KpiAnomalyId, Agreement, CreatedAt)
        VALUES (incoming.RunId, incoming.Board, incoming.ProfileKey, incoming.EntityKey, incoming.Stage, incoming.BucketStart,
                incoming.RuleRank, incoming.MethodVerdict, incoming.KpiAnomalyId, incoming.Agreement, @Now);

    DELETE FROM monitoring.ScoutShadowComparison WHERE BucketStart < @Cutoff;

    COMMIT TRANSACTION;
END
GO

CREATE OR ALTER PROCEDURE monitoring.usp_GetScoutShadowComparisons
    @Board      nvarchar(16),
    @Since      datetimeoffset(7),
    @ProfileKey nvarchar(128) = NULL,
    @Top        int = 500
AS
BEGIN
    SET NOCOUNT ON;

    SELECT TOP (@Top) RunId, Board, ProfileKey, EntityKey, Stage, BucketStart, RuleRank, MethodVerdict, KpiAnomalyId, Agreement
    FROM monitoring.ScoutShadowComparison
    WHERE Board = @Board
      AND BucketStart >= @Since
      AND (@ProfileKey IS NULL OR ProfileKey = @ProfileKey)
    ORDER BY BucketStart DESC, ShadowId DESC;
END
GO

-- Counting unit (contract v4 section 4, SD5): the entity-hour incident, not the row. A group is
-- (ProfileKey, EntityKey, method stage, hour of BucketStart in UTC): the method stage is the row's Stage for a
-- method-only row and '' (the all-stages entity KPI) for any row with a rules side, so the 5-min rules intervals
-- and the hourly anomaly row of the same incident fall into one group. Precedence: a group with a 'both' row, or
-- with rows from both sides, is 'both'; otherwise 'rules-only' or 'method-only'. Detail rows stay at 5 min.
-- Mirrored by ScoutShadowRows.Count (in-memory store).
CREATE OR ALTER PROCEDURE monitoring.usp_GetScoutShadowCounts
    @Board      nvarchar(16),
    @Since      datetimeoffset(7),
    @ProfileKey nvarchar(128) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    WITH Keyed AS
    (
        SELECT ProfileKey, EntityKey, Agreement,
               CASE WHEN Agreement = N'method-only' THEN Stage ELSE N'' END AS CountStage,
               DATEADD(HOUR, DATEDIFF(HOUR, CAST('2000-01-01' AS datetime2), CAST(SWITCHOFFSET(BucketStart, '+00:00') AS datetime2)),
                       CAST('2000-01-01' AS datetime2)) AS HourStart
        FROM monitoring.ScoutShadowComparison
        WHERE Board = @Board
          AND BucketStart >= @Since
          AND (@ProfileKey IS NULL OR ProfileKey = @ProfileKey)
    ),
    Incidents AS
    (
        SELECT MAX(CASE WHEN Agreement = N'both' THEN 1 ELSE 0 END)        AS HasBoth,
               MAX(CASE WHEN Agreement = N'rules-only' THEN 1 ELSE 0 END)  AS HasRules,
               MAX(CASE WHEN Agreement = N'method-only' THEN 1 ELSE 0 END) AS HasMethod
        FROM Keyed
        GROUP BY ProfileKey, EntityKey, CountStage, HourStart
    )
    SELECT ISNULL(SUM(CASE WHEN HasBoth = 1 OR (HasRules = 1 AND HasMethod = 1) THEN 1 ELSE 0 END), 0) AS [Both],
           ISNULL(SUM(CASE WHEN HasBoth = 0 AND HasRules = 1 AND HasMethod = 0 THEN 1 ELSE 0 END), 0)   AS RulesOnly,
           ISNULL(SUM(CASE WHEN HasBoth = 0 AND HasRules = 0 AND HasMethod = 1 THEN 1 ELSE 0 END), 0)   AS MethodOnly
    FROM Incidents;
END
GO
