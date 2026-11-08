-- Specification 1000 - configurable KPI rules (one row per Pillar/Kpi/Domain/Stage), their
-- append-only audit trail, ingested time buckets with raw samples, the per-KPI detection state
-- (consecutive counter + cooldown), recorded anomalies and the ingestion watermark per KPI.
-- Idempotent: tables are created only when missing, procedures use CREATE OR ALTER.
-- Contract v3: KpiDefinition.ProfileKey is added in place on existing databases (guarded ALTERs below).

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'monitoring')
    EXEC(N'CREATE SCHEMA monitoring');
GO

IF OBJECT_ID(N'monitoring.KpiDefinition', N'U') IS NULL
BEGIN
    CREATE TABLE monitoring.KpiDefinition
    (
        KpiDefinitionId          int               NOT NULL IDENTITY(1, 1) CONSTRAINT PK_monitoring_KpiDefinition PRIMARY KEY,
        Pillar                   nvarchar(16)      NOT NULL,
        Kpi                      nvarchar(64)      NOT NULL,
        Domain                   nvarchar(32)      NOT NULL,
        Stage                    nvarchar(64)      NOT NULL CONSTRAINT DF_monitoring_KpiDefinition_Stage DEFAULT N'',
        SourceKey                nvarchar(64)      NOT NULL,
        Direction                nvarchar(16)      NOT NULL,
        Aggregation              nvarchar(8)       NOT NULL,
        EvaluationFrequencySec   int               NOT NULL,
        BucketSizeSec            int               NOT NULL,
        LookbackWindowSec        int               NOT NULL,
        SampleSize               int               NOT NULL,
        MinObservationsPerBucket int               NOT NULL,
        DetectionMethod          nvarchar(32)      NOT NULL,
        MethodParamsJson         nvarchar(1000)    NOT NULL,
        ConsecutiveCount         int               NOT NULL,
        CooldownMinutes          int               NOT NULL,
        Enabled                  bit               NOT NULL CONSTRAINT DF_monitoring_KpiDefinition_Enabled DEFAULT 0,
        UpdatedAt                datetimeoffset(7) NOT NULL CONSTRAINT DF_monitoring_KpiDefinition_UpdatedAt DEFAULT SYSDATETIMEOFFSET(),
        UpdatedBy                nvarchar(128)     NOT NULL CONSTRAINT DF_monitoring_KpiDefinition_UpdatedBy DEFAULT N'',
        ProfileKey               nvarchar(128)     NOT NULL CONSTRAINT DF_monitoring_KpiDefinition_ProfileKey DEFAULT N'arrivia',
        CONSTRAINT UQ_monitoring_KpiDefinition_Key UNIQUE (ProfileKey, Pillar, Kpi, Domain, Stage),
        CONSTRAINT CK_monitoring_KpiDefinition_Pillar CHECK (Pillar IN (N'Business', N'Application', N'Infrastructure')),
        CONSTRAINT CK_monitoring_KpiDefinition_Domain CHECK (Domain IN (N'hotels', N'cars', N'cruises', N'air')),
        CONSTRAINT CK_monitoring_KpiDefinition_Direction CHECK (Direction IN (N'Increase', N'Decrease', N'Either')),
        CONSTRAINT CK_monitoring_KpiDefinition_Aggregation CHECK (Aggregation IN (N'P95', N'P50', N'Mean', N'Count', N'Value')),
        CONSTRAINT CK_monitoring_KpiDefinition_Method CHECK (DetectionMethod IN (N'StaticThreshold', N'BaselinePercent', N'RobustZScore')),
        CONSTRAINT CK_monitoring_KpiDefinition_Freq CHECK (EvaluationFrequencySec >= 60),
        CONSTRAINT CK_monitoring_KpiDefinition_Bucket CHECK (BucketSizeSec >= 60),
        CONSTRAINT CK_monitoring_KpiDefinition_Lookback CHECK (LookbackWindowSec >= BucketSizeSec),
        CONSTRAINT CK_monitoring_KpiDefinition_Sample CHECK (SampleSize BETWEEN 0 AND 100000),
        CONSTRAINT CK_monitoring_KpiDefinition_MinObs CHECK (MinObservationsPerBucket >= 0),
        CONSTRAINT CK_monitoring_KpiDefinition_Consecutive CHECK (ConsecutiveCount BETWEEN 1 AND 48),
        CONSTRAINT CK_monitoring_KpiDefinition_Cooldown CHECK (CooldownMinutes BETWEEN 0 AND 10080),
        CONSTRAINT CK_monitoring_KpiDefinition_Params CHECK (ISJSON(MethodParamsJson) = 1),
        CONSTRAINT CK_monitoring_KpiDefinition_StaticEither CHECK (NOT (DetectionMethod = N'StaticThreshold' AND Direction = N'Either'))
    );
END
GO

IF OBJECT_ID(N'monitoring.KpiDefinitionAudit', N'U') IS NULL
BEGIN
    CREATE TABLE monitoring.KpiDefinitionAudit
    (
        AuditId          bigint            NOT NULL IDENTITY(1, 1) CONSTRAINT PK_monitoring_KpiDefinitionAudit PRIMARY KEY,
        KpiDefinitionId  int               NOT NULL,
        SnapshotJson     nvarchar(max)     NOT NULL,
        ChangedAt        datetimeoffset(7) NOT NULL CONSTRAINT DF_monitoring_KpiDefinitionAudit_ChangedAt DEFAULT SYSDATETIMEOFFSET(),
        ChangedBy        nvarchar(128)     NOT NULL CONSTRAINT DF_monitoring_KpiDefinitionAudit_ChangedBy DEFAULT N''
    );
    CREATE INDEX IX_monitoring_KpiDefinitionAudit_Read ON monitoring.KpiDefinitionAudit (KpiDefinitionId, ChangedAt DESC);
END
GO

-- Contract v3: every KPI belongs to a company profile (monitoring.CompanyProfile.ProfileKey).
-- Rows created before profiles existed belong to the incumbent profile.
IF COL_LENGTH(N'monitoring.KpiDefinition', N'ProfileKey') IS NULL
    ALTER TABLE monitoring.KpiDefinition
        ADD ProfileKey nvarchar(128) NOT NULL CONSTRAINT DF_monitoring_KpiDefinition_ProfileKey DEFAULT N'arrivia';
GO

-- The natural key is per profile: recreate it only when it still lacks ProfileKey.
IF EXISTS (SELECT 1 FROM sys.key_constraints
           WHERE parent_object_id = OBJECT_ID(N'monitoring.KpiDefinition') AND name = N'UQ_monitoring_KpiDefinition_Key')
   AND NOT EXISTS (SELECT 1
                   FROM sys.key_constraints AS kc
                   INNER JOIN sys.index_columns AS ic ON ic.object_id = kc.parent_object_id AND ic.index_id = kc.unique_index_id
                   INNER JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                   WHERE kc.parent_object_id = OBJECT_ID(N'monitoring.KpiDefinition')
                     AND kc.name = N'UQ_monitoring_KpiDefinition_Key'
                     AND c.name = N'ProfileKey')
    ALTER TABLE monitoring.KpiDefinition DROP CONSTRAINT UQ_monitoring_KpiDefinition_Key;
GO

IF NOT EXISTS (SELECT 1 FROM sys.key_constraints
               WHERE parent_object_id = OBJECT_ID(N'monitoring.KpiDefinition') AND name = N'UQ_monitoring_KpiDefinition_Key')
    ALTER TABLE monitoring.KpiDefinition
        ADD CONSTRAINT UQ_monitoring_KpiDefinition_Key UNIQUE (ProfileKey, Pillar, Kpi, Domain, Stage);
GO

IF COL_LENGTH(N'monitoring.KpiDefinitionAudit', N'ProfileKey') IS NULL
    ALTER TABLE monitoring.KpiDefinitionAudit ADD ProfileKey nvarchar(128) NULL;
GO

IF OBJECT_ID(N'monitoring.KpiBucket', N'U') IS NULL
BEGIN
    CREATE TABLE monitoring.KpiBucket
    (
        KpiDefinitionId int               NOT NULL,
        BucketStart     datetimeoffset(7) NOT NULL,
        TotalCount      bigint            NOT NULL,
        SampledCount    int               NOT NULL,
        P50             float             NULL,
        P95             float             NULL,
        Mean            float             NULL,
        StdDev          float             NULL,
        Value           float             NULL,
        IngestedAt      datetimeoffset(7) NOT NULL CONSTRAINT DF_monitoring_KpiBucket_IngestedAt DEFAULT SYSDATETIMEOFFSET(),
        CONSTRAINT PK_monitoring_KpiBucket PRIMARY KEY (KpiDefinitionId, BucketStart)
    );
END
GO

IF OBJECT_ID(N'monitoring.KpiSample', N'U') IS NULL
BEGIN
    CREATE TABLE monitoring.KpiSample
    (
        SampleId        bigint            NOT NULL IDENTITY(1, 1) CONSTRAINT PK_monitoring_KpiSample PRIMARY KEY,
        KpiDefinitionId int               NOT NULL,
        BucketStart     datetimeoffset(7) NOT NULL,
        [Timestamp]     datetimeoffset(7) NOT NULL,
        Value           float             NOT NULL,
        OperationId     nvarchar(64)      NULL
    );
    CREATE INDEX IX_monitoring_KpiSample_Bucket ON monitoring.KpiSample (KpiDefinitionId, BucketStart);
    CREATE INDEX IX_monitoring_KpiSample_Purge ON monitoring.KpiSample (BucketStart);
END
GO

IF OBJECT_ID(N'monitoring.KpiDetectionState', N'U') IS NULL
BEGIN
    CREATE TABLE monitoring.KpiDetectionState
    (
        KpiDefinitionId     int               NOT NULL CONSTRAINT PK_monitoring_KpiDetectionState PRIMARY KEY,
        ConsecutiveCount    int               NOT NULL,
        LastBucketEvaluated datetimeoffset(7) NULL,
        CooldownUntil       datetimeoffset(7) NULL,
        LastEvaluatedAt     datetimeoffset(7) NULL,
        UpdatedAt           datetimeoffset(7) NOT NULL CONSTRAINT DF_monitoring_KpiDetectionState_UpdatedAt DEFAULT SYSDATETIMEOFFSET()
    );
END
GO

IF OBJECT_ID(N'monitoring.KpiAnomaly', N'U') IS NULL
BEGIN
    CREATE TABLE monitoring.KpiAnomaly
    (
        AnomalyId       bigint            NOT NULL IDENTITY(1, 1) CONSTRAINT PK_monitoring_KpiAnomaly PRIMARY KEY,
        KpiDefinitionId int               NOT NULL,
        DetectedAt      datetimeoffset(7) NOT NULL,
        BucketStart     datetimeoffset(7) NOT NULL,
        ObservedValue   float             NOT NULL,
        ReferenceValue  float             NOT NULL,
        Deviation       float             NOT NULL,
        Detection       nvarchar(400)     NOT NULL,
        InvestigationId uniqueidentifier  NULL
    );
    CREATE INDEX IX_monitoring_KpiAnomaly_Recent ON monitoring.KpiAnomaly (DetectedAt DESC) INCLUDE (KpiDefinitionId);
END
GO

IF OBJECT_ID(N'monitoring.IngestionWatermark', N'U') IS NULL
BEGIN
    CREATE TABLE monitoring.IngestionWatermark
    (
        KpiDefinitionId int               NOT NULL CONSTRAINT PK_monitoring_IngestionWatermark PRIMARY KEY,
        Watermark       datetimeoffset(7) NOT NULL,
        UpdatedAt       datetimeoffset(7) NOT NULL CONSTRAINT DF_monitoring_IngestionWatermark_UpdatedAt DEFAULT SYSDATETIMEOFFSET()
    );
END
GO

-- ------------------------------------------------------------------ KpiDefinition

CREATE OR ALTER PROCEDURE monitoring.usp_GetKpiDefinitions
    @Pillar      nvarchar(16) = NULL,
    @OnlyEnabled bit = 0,
    @ProfileKey  nvarchar(128) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    SELECT KpiDefinitionId, Pillar, Kpi, Domain, Stage, SourceKey, Direction, Aggregation,
           EvaluationFrequencySec, BucketSizeSec, LookbackWindowSec, SampleSize, MinObservationsPerBucket,
           DetectionMethod, MethodParamsJson, ConsecutiveCount, CooldownMinutes, Enabled, UpdatedAt, UpdatedBy, ProfileKey
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
           DetectionMethod, MethodParamsJson, ConsecutiveCount, CooldownMinutes, Enabled, UpdatedAt, UpdatedBy, ProfileKey
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
    @ProfileKey               nvarchar(128)
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
             DetectionMethod, MethodParamsJson, ConsecutiveCount, CooldownMinutes, Enabled, UpdatedAt, UpdatedBy, ProfileKey)
        VALUES
            (@Pillar, @Kpi, @Domain, ISNULL(@Stage, N''), @SourceKey, @Direction, @Aggregation,
             @EvaluationFrequencySec, @BucketSizeSec, @LookbackWindowSec, @SampleSize, @MinObservationsPerBucket,
             @DetectionMethod, @MethodParamsJson, @ConsecutiveCount, @CooldownMinutes, @Enabled, @Now, @ChangedBy, @ProfileKey);

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
            ProfileKey = @ProfileKey
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
           DetectionMethod, MethodParamsJson, ConsecutiveCount, CooldownMinutes, Enabled, UpdatedAt, UpdatedBy, ProfileKey
    FROM monitoring.KpiDefinition
    WHERE KpiDefinitionId = @Id;
END
GO

CREATE OR ALTER PROCEDURE monitoring.usp_GetKpiDefinitionAudit
    @KpiDefinitionId int,
    @Top             int = 200
AS
BEGIN
    SET NOCOUNT ON;

    SELECT TOP (@Top) AuditId, KpiDefinitionId, SnapshotJson, ChangedAt, ChangedBy
    FROM monitoring.KpiDefinitionAudit
    WHERE KpiDefinitionId = @KpiDefinitionId
    ORDER BY ChangedAt DESC, AuditId DESC;
END
GO

-- ------------------------------------------------------------------ KpiBucket

CREATE OR ALTER PROCEDURE monitoring.usp_UpsertKpiBuckets
    @BucketsJson nvarchar(max)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now datetimeoffset(7) = SYSDATETIMEOFFSET();

    MERGE monitoring.KpiBucket WITH (HOLDLOCK) AS target
    USING
    (
        SELECT KpiDefinitionId, BucketStart, TotalCount, SampledCount, P50, P95, Mean, StdDev, Value
        FROM OPENJSON(@BucketsJson)
        WITH
        (
            KpiDefinitionId int               '$.kpiDefinitionId',
            BucketStart     datetimeoffset(7) '$.bucketStart',
            TotalCount      bigint            '$.totalCount',
            SampledCount    int               '$.sampledCount',
            P50             float             '$.p50',
            P95             float             '$.p95',
            Mean            float             '$.mean',
            StdDev          float             '$.stdDev',
            Value           float             '$.value'
        )
    ) AS incoming
        ON target.KpiDefinitionId = incoming.KpiDefinitionId
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
            IngestedAt = @Now
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (KpiDefinitionId, BucketStart, TotalCount, SampledCount, P50, P95, Mean, StdDev, Value, IngestedAt)
        VALUES (incoming.KpiDefinitionId, incoming.BucketStart, incoming.TotalCount, incoming.SampledCount,
                incoming.P50, incoming.P95, incoming.Mean, incoming.StdDev, incoming.Value, @Now);
END
GO

CREATE OR ALTER PROCEDURE monitoring.usp_GetKpiBuckets
    @KpiDefinitionId int,
    @From            datetimeoffset(7),
    @To              datetimeoffset(7)
AS
BEGIN
    SET NOCOUNT ON;

    SELECT KpiDefinitionId, BucketStart, TotalCount, SampledCount, P50, P95, Mean, StdDev, Value
    FROM monitoring.KpiBucket
    WHERE KpiDefinitionId = @KpiDefinitionId
      AND BucketStart >= @From
      AND BucketStart < @To
    ORDER BY BucketStart;
END
GO

-- ------------------------------------------------------------------ KpiSample

CREATE OR ALTER PROCEDURE monitoring.usp_AddKpiSamples
    @SamplesJson nvarchar(max)
AS
BEGIN
    SET NOCOUNT ON;

    INSERT INTO monitoring.KpiSample (KpiDefinitionId, BucketStart, [Timestamp], Value, OperationId)
    SELECT KpiDefinitionId, BucketStart, [Timestamp], Value, OperationId
    FROM OPENJSON(@SamplesJson)
    WITH
    (
        KpiDefinitionId int               '$.kpiDefinitionId',
        BucketStart     datetimeoffset(7) '$.bucketStart',
        [Timestamp]     datetimeoffset(7) '$.timestamp',
        Value           float             '$.value',
        OperationId     nvarchar(64)      '$.operationId'
    );
END
GO

CREATE OR ALTER PROCEDURE monitoring.usp_GetKpiSamples
    @KpiDefinitionId int,
    @BucketStart     datetimeoffset(7),
    @Top             int
AS
BEGIN
    SET NOCOUNT ON;

    SELECT TOP (@Top) SampleId, KpiDefinitionId, BucketStart, [Timestamp], Value, OperationId
    FROM monitoring.KpiSample
    WHERE KpiDefinitionId = @KpiDefinitionId
      AND BucketStart = @BucketStart
    ORDER BY [Timestamp];
END
GO

CREATE OR ALTER PROCEDURE monitoring.usp_PurgeKpiSamples
    @Cutoff datetimeoffset(7)
AS
BEGIN
    SET NOCOUNT ON;

    DELETE FROM monitoring.KpiSample
    WHERE BucketStart < @Cutoff;

    SELECT @@ROWCOUNT AS Deleted;
END
GO

-- ------------------------------------------------------------------ KpiDetectionState

CREATE OR ALTER PROCEDURE monitoring.usp_GetKpiDetectionState
    @KpiDefinitionId int
AS
BEGIN
    SET NOCOUNT ON;

    SELECT KpiDefinitionId, ConsecutiveCount, LastBucketEvaluated, CooldownUntil, LastEvaluatedAt
    FROM monitoring.KpiDetectionState
    WHERE KpiDefinitionId = @KpiDefinitionId;
END
GO

CREATE OR ALTER PROCEDURE monitoring.usp_SaveKpiDetectionState
    @KpiDefinitionId     int,
    @ConsecutiveCount    int,
    @LastBucketEvaluated datetimeoffset(7) = NULL,
    @CooldownUntil       datetimeoffset(7) = NULL,
    @LastEvaluatedAt     datetimeoffset(7) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now datetimeoffset(7) = SYSDATETIMEOFFSET();

    MERGE monitoring.KpiDetectionState WITH (HOLDLOCK) AS target
    USING (SELECT @KpiDefinitionId AS KpiDefinitionId) AS incoming
        ON target.KpiDefinitionId = incoming.KpiDefinitionId
    WHEN MATCHED THEN
        UPDATE SET
            ConsecutiveCount = @ConsecutiveCount,
            LastBucketEvaluated = @LastBucketEvaluated,
            CooldownUntil = @CooldownUntil,
            LastEvaluatedAt = @LastEvaluatedAt,
            UpdatedAt = @Now
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (KpiDefinitionId, ConsecutiveCount, LastBucketEvaluated, CooldownUntil, LastEvaluatedAt, UpdatedAt)
        VALUES (@KpiDefinitionId, @ConsecutiveCount, @LastBucketEvaluated, @CooldownUntil, @LastEvaluatedAt, @Now);
END
GO

-- ------------------------------------------------------------------ KpiAnomaly

CREATE OR ALTER PROCEDURE monitoring.usp_AddKpiAnomaly
    @KpiDefinitionId int,
    @DetectedAt      datetimeoffset(7),
    @BucketStart     datetimeoffset(7),
    @ObservedValue   float,
    @ReferenceValue  float,
    @Deviation       float,
    @Detection       nvarchar(400),
    @InvestigationId uniqueidentifier = NULL
AS
BEGIN
    SET NOCOUNT ON;

    INSERT INTO monitoring.KpiAnomaly
        (KpiDefinitionId, DetectedAt, BucketStart, ObservedValue, ReferenceValue, Deviation, Detection, InvestigationId)
    VALUES
        (@KpiDefinitionId, @DetectedAt, @BucketStart, @ObservedValue, @ReferenceValue, @Deviation, @Detection, @InvestigationId);

    SELECT CAST(SCOPE_IDENTITY() AS bigint) AS AnomalyId;
END
GO

CREATE OR ALTER PROCEDURE monitoring.usp_AttachKpiAnomalyInvestigation
    @AnomalyId       bigint,
    @InvestigationId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;

    UPDATE monitoring.KpiAnomaly
    SET InvestigationId = @InvestigationId
    WHERE AnomalyId = @AnomalyId;
END
GO

CREATE OR ALTER PROCEDURE monitoring.usp_GetRecentKpiAnomalies
    @Pillar nvarchar(16) = NULL,
    @Domain     nvarchar(32) = NULL,
    @Top        int = 100,
    @ProfileKey nvarchar(128) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    SELECT TOP (@Top) a.AnomalyId, a.KpiDefinitionId, a.DetectedAt, a.BucketStart, a.ObservedValue,
           a.ReferenceValue, a.Deviation, a.Detection, a.InvestigationId
    FROM monitoring.KpiAnomaly AS a
    INNER JOIN monitoring.KpiDefinition AS d
        ON d.KpiDefinitionId = a.KpiDefinitionId
    WHERE (@Pillar IS NULL OR d.Pillar = @Pillar)
      AND (@Domain IS NULL OR d.Domain = @Domain)
      AND (@ProfileKey IS NULL OR d.ProfileKey = @ProfileKey)
    ORDER BY a.DetectedAt DESC, a.AnomalyId DESC;
END
GO

-- ------------------------------------------------------------------ IngestionWatermark

CREATE OR ALTER PROCEDURE monitoring.usp_GetIngestionWatermark
    @KpiDefinitionId int
AS
BEGIN
    SET NOCOUNT ON;

    SELECT Watermark
    FROM monitoring.IngestionWatermark
    WHERE KpiDefinitionId = @KpiDefinitionId;
END
GO

CREATE OR ALTER PROCEDURE monitoring.usp_SetIngestionWatermark
    @KpiDefinitionId int,
    @Watermark       datetimeoffset(7)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now datetimeoffset(7) = SYSDATETIMEOFFSET();

    MERGE monitoring.IngestionWatermark WITH (HOLDLOCK) AS target
    USING (SELECT @KpiDefinitionId AS KpiDefinitionId) AS incoming
        ON target.KpiDefinitionId = incoming.KpiDefinitionId
    WHEN MATCHED THEN
        UPDATE SET Watermark = @Watermark, UpdatedAt = @Now
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (KpiDefinitionId, Watermark, UpdatedAt)
        VALUES (@KpiDefinitionId, @Watermark, @Now);
END
GO
