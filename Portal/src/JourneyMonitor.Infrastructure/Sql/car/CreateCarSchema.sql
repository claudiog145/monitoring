-- Car journey store for KQL rows from TMobile_Cars_Query_KQL_v6.txt.
-- Grain: one car.Journey row per CorrelationId, carrying that journey's current state.
-- UserId is reported, not used to group (same as the KQL comment).
-- Nested Journey[] / calls[] are shredded into car.JourneyStep and car.JourneyCall.
-- JourneyJson keeps the original Kusto bag so the portal can still round-trip raw rows.
--
-- The KQL job may run many times a day and its window overlaps, so consecutive runs re-report
-- the same journeys. Ingestion upserts on CorrelationId: a journey seen again only has its row
-- rewritten when LastSeen advanced, otherwise the run just re-stamps QueryRunId to record
-- "still seen". car.Journey.QueryRunId therefore means "the last run that saw this journey",
-- which is what makes the read below return exactly the journeys present in the newest run.
--
-- Do not load Hotel, Cruise, or Air KQL rows into these tables. Each vertical upserts on
-- its own CorrelationId session key (rawId-seq). CompletePurchase Verticals is ["Car"].
--
-- Auth when calling from Azure: Managed Identity + Azure SQL (not SQL passwords in source).
-- Connection strings belong in Key Vault / Function app settings.

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'car')
    EXEC(N'CREATE SCHEMA car');
GO

IF OBJECT_ID(N'car.QueryRun', N'U') IS NULL
BEGIN
    CREATE TABLE car.QueryRun
    (
        QueryRunId   uniqueidentifier NOT NULL
            CONSTRAINT PK_car_QueryRun PRIMARY KEY
            CONSTRAINT DF_car_QueryRun_Id DEFAULT NEWSEQUENTIALID(),
        Domain       nvarchar(32)     NOT NULL
            CONSTRAINT DF_car_QueryRun_Domain DEFAULT N'car',
        Window       nvarchar(16)     NOT NULL,
        Environment  nvarchar(32)     NULL,
        ClubId       nvarchar(64)     NULL,
        Host         nvarchar(256)    NULL,
        FetchedAt    datetimeoffset(7) NOT NULL
            CONSTRAINT DF_car_QueryRun_FetchedAt DEFAULT SYSDATETIMEOFFSET(),
        [RowCount]   int              NOT NULL
            CONSTRAINT DF_car_QueryRun_RowCount DEFAULT 0,
        Source       nvarchar(32)     NOT NULL
            CONSTRAINT DF_car_QueryRun_Source DEFAULT N'live',
        KqlFile      nvarchar(256)    NULL
            CONSTRAINT DF_car_QueryRun_KqlFile DEFAULT N'TMobile_Cars_Query_KQL_v6.txt',
        CONSTRAINT CK_car_QueryRun_Domain CHECK (Domain = N'car')
    );

    CREATE INDEX IX_car_QueryRun_FetchedAt
        ON car.QueryRun (FetchedAt DESC);
END
GO

-- One row per club. RecordCount is the fetch size for car.Journey rows whose
-- QueryRunId is that club's CurrentSnapshot.QueryRunId.
IF OBJECT_ID(N'car.CurrentSnapshot', N'U') IS NULL
BEGIN
    CREATE TABLE car.CurrentSnapshot
    (
        Domain       nvarchar(32)      NOT NULL
            CONSTRAINT DF_car_CurrentSnapshot_Domain DEFAULT N'car',
        QueryRunId   uniqueidentifier  NOT NULL
            CONSTRAINT FK_car_CurrentSnapshot_QueryRun
                REFERENCES car.QueryRun (QueryRunId),
        FetchedAt    datetimeoffset(7) NOT NULL,
        Window       nvarchar(16)      NOT NULL,
        Environment  nvarchar(32)      NULL,
        ClubId       nvarchar(64)      NOT NULL,
        RecordCount  int               NOT NULL
            CONSTRAINT DF_car_CurrentSnapshot_RecordCount DEFAULT 0,
        StepCount    int               NOT NULL
            CONSTRAINT DF_car_CurrentSnapshot_StepCount DEFAULT 0,
        CallCount    int               NOT NULL
            CONSTRAINT DF_car_CurrentSnapshot_CallCount DEFAULT 0,
        CONSTRAINT PK_car_CurrentSnapshot PRIMARY KEY (Domain, ClubId),
        CONSTRAINT CK_car_CurrentSnapshot_Domain CHECK (Domain = N'car'),
        CONSTRAINT CK_car_CurrentSnapshot_RecordCount CHECK (RecordCount >= 0)
    );
END
GO

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
IF OBJECT_ID(N'car.CurrentSnapshot', N'U') IS NOT NULL
   AND NOT EXISTS (
        SELECT 1
        FROM sys.indexes AS i
        INNER JOIN sys.index_columns AS ic
            ON ic.object_id = i.object_id AND ic.index_id = i.index_id
        INNER JOIN sys.columns AS c
            ON c.object_id = ic.object_id AND c.column_id = ic.column_id
        WHERE i.object_id = OBJECT_ID(N'car.CurrentSnapshot')
          AND i.is_primary_key = 1
          AND c.name = N'ClubId'
   )
BEGIN
    DECLARE @pk sysname;
    SELECT @pk = name
    FROM sys.key_constraints
    WHERE parent_object_id = OBJECT_ID(N'car.CurrentSnapshot')
      AND [type] = N'PK';

    IF @pk IS NOT NULL
    BEGIN
        DECLARE @drop nvarchar(400) = N'ALTER TABLE car.CurrentSnapshot DROP CONSTRAINT ' + QUOTENAME(@pk);
        EXEC(@drop);
    END

    DELETE FROM car.CurrentSnapshot WHERE ClubId IS NULL;

    ALTER TABLE car.CurrentSnapshot ALTER COLUMN ClubId nvarchar(64) NOT NULL;
    ALTER TABLE car.CurrentSnapshot
        ADD CONSTRAINT PK_car_CurrentSnapshot PRIMARY KEY (Domain, ClubId);
END
GO

IF OBJECT_ID(N'car.Journey', N'U') IS NULL
BEGIN
    CREATE TABLE car.Journey
    (
        JourneyId              bigint            NOT NULL IDENTITY(1, 1)
            CONSTRAINT PK_car_Journey PRIMARY KEY,
        QueryRunId             uniqueidentifier  NOT NULL
            CONSTRAINT FK_car_Journey_QueryRun
                REFERENCES car.QueryRun (QueryRunId) ON DELETE CASCADE,
        -- Club of the ingest that last wrote this journey. KQL rows do not carry it;
        -- usp_IngestKqlRows stamps @ClubId. NOT NULL so a MERGE cannot leave it blank.
        ClubId                 nvarchar(64)      NOT NULL,
        CorrelationId          nvarchar(128)     NOT NULL,
        UserId                 nvarchar(128)     NULL,
        Outcome                nvarchar(256)     NOT NULL,
        LastStep               int               NOT NULL
            CONSTRAINT DF_car_Journey_LastStep DEFAULT 0,
        FailReason             nvarchar(1024)    NULL,
        BookOk                 int               NOT NULL
            CONSTRAINT DF_car_Journey_BookOk DEFAULT 0,
        BookFail               int               NOT NULL
            CONSTRAINT DF_car_Journey_BookFail DEFAULT 0,
        DistinctOkRes          int               NOT NULL
            CONSTRAINT DF_car_Journey_DistinctOkRes DEFAULT 0,
        BookedReservations     nvarchar(max)     NULL,
        FailedCalls            int               NOT NULL
            CONSTRAINT DF_car_Journey_FailedCalls DEFAULT 0,
        IsBookSuccessful       bit               NOT NULL
            CONSTRAINT DF_car_Journey_IsBookSuccessful DEFAULT 0,
        PassedSearch           bit               NOT NULL
            CONSTRAINT DF_car_Journey_PassedSearch DEFAULT 0,
        Exceptions             int               NOT NULL
            CONSTRAINT DF_car_Journey_Exceptions DEFAULT 0,
        JourneyTotalMs         decimal(18, 2)    NULL,
        SlowestCallMs          decimal(18, 2)    NULL,
        OverallP95             decimal(18, 2)    NULL,
        SearchToConfirmSeconds int               NULL,
        FirstSeen              datetimeoffset(7) NULL,
        LastSeen               datetimeoffset(7) NULL,
        StepsJson              nvarchar(max)     NULL,
        JourneyJson            nvarchar(max)     NULL,
        CONSTRAINT UQ_car_Journey_Run_Correlation UNIQUE (QueryRunId, CorrelationId),
        CONSTRAINT CK_car_Journey_BookedReservations CHECK (BookedReservations IS NULL OR ISJSON(BookedReservations) = 1),
        CONSTRAINT CK_car_Journey_StepsJson   CHECK (StepsJson   IS NULL OR ISJSON(StepsJson)   = 1),
        CONSTRAINT CK_car_Journey_JourneyJson CHECK (JourneyJson IS NULL OR ISJSON(JourneyJson) = 1)
    );

    CREATE INDEX IX_car_Journey_LastSeen
        ON car.Journey (LastSeen DESC)
        INCLUDE (CorrelationId, Outcome, UserId);

    CREATE INDEX IX_car_Journey_UserId_LastSeen
        ON car.Journey (UserId, LastSeen DESC);

    CREATE INDEX IX_car_Journey_Outcome_LastSeen
        ON car.Journey (Outcome, LastSeen DESC);
END
GO

-- Databases written by the previous insert-per-run ingestion hold one row per journey per run.
-- Collapse them to the newest row per journey so the unique index below can exist. Steps and
-- calls of the discarded rows go with them through their cascading foreign keys.
IF OBJECT_ID(N'car.Journey', N'U') IS NOT NULL
   AND EXISTS (SELECT 1 FROM car.Journey GROUP BY CorrelationId HAVING COUNT(*) > 1)
BEGIN
    WITH ranked AS
    (
        SELECT
            JourneyId,
            ROW_NUMBER() OVER (PARTITION BY CorrelationId ORDER BY LastSeen DESC, JourneyId DESC) AS rn
        FROM car.Journey
    )
    DELETE FROM car.Journey
    WHERE JourneyId IN (SELECT JourneyId FROM ranked WHERE rn > 1);
END
GO

-- The grain is the journey, so CorrelationId is the identity ingestion upserts on.
IF OBJECT_ID(N'car.Journey', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE name = N'UQ_car_Journey_CorrelationId'
                     AND object_id = OBJECT_ID(N'car.Journey'))
    CREATE UNIQUE INDEX UQ_car_Journey_CorrelationId ON car.Journey (CorrelationId);
GO

-- Session keys (rawId-seq) replace unsuffixed correlation ids. Clear the old rows once so a
-- 7-day read does not count both. The extended property is the guard against a later wipe.
IF OBJECT_ID(N'car.Journey', N'U') IS NOT NULL
   AND NOT EXISTS (
        SELECT 1 FROM sys.extended_properties
        WHERE name = N'SessionGrain'
          AND major_id = OBJECT_ID(N'car.Journey')
          AND minor_id = 0)
BEGIN
    DELETE FROM car.Journey;
    EXEC sys.sp_addextendedproperty
        @name = N'SessionGrain',
        @value = N'1',
        @level0type = N'SCHEMA', @level0name = N'car',
        @level1type = N'TABLE', @level1name = N'Journey';
END
GO

-- Databases created before ClubId existed on the journey row. The ingest parameter is the
-- club; backfill from the query run that last saw the journey, then require a value so the
-- MERGE below cannot write NULL (SQL Server reports that as "UPDATE fails").
-- The backfill runs through EXEC on purpose: column names are resolved when the batch is
-- compiled, so a literal UPDATE on the column the line above adds fails the whole batch with
-- "Invalid column name 'ClubId'" and the ALTER never runs.
IF OBJECT_ID(N'car.Journey', N'U') IS NOT NULL
   AND COL_LENGTH(N'car.Journey', N'ClubId') IS NULL
BEGIN
    ALTER TABLE car.Journey ADD ClubId nvarchar(64) NULL;

    EXEC(N'
        UPDATE j
        SET ClubId = r.ClubId
        FROM car.Journey AS j
        INNER JOIN car.QueryRun AS r
            ON r.QueryRunId = j.QueryRunId
        WHERE j.ClubId IS NULL
          AND NULLIF(r.ClubId, N'''') IS NOT NULL;');
END
GO

IF OBJECT_ID(N'car.Journey', N'U') IS NOT NULL
   AND COL_LENGTH(N'car.Journey', N'ClubId') IS NOT NULL
   AND EXISTS (
        SELECT 1
        FROM sys.columns
        WHERE object_id = OBJECT_ID(N'car.Journey')
          AND name = N'ClubId'
          AND is_nullable = 1
   )
   AND NOT EXISTS (SELECT 1 FROM car.Journey WHERE ClubId IS NULL)
BEGIN
    ALTER TABLE car.Journey ALTER COLUMN ClubId nvarchar(64) NOT NULL;
END
GO

-- QueryRunId now points at the last run that saw the journey, not at the run that created the row,
-- so cascading from QueryRun would delete journeys that are still current the moment old runs are
-- purged. Fail loudly on such a delete instead.
IF EXISTS (SELECT 1 FROM sys.foreign_keys
           WHERE name = N'FK_car_Journey_QueryRun' AND delete_referential_action = 1)
BEGIN
    ALTER TABLE car.Journey DROP CONSTRAINT FK_car_Journey_QueryRun;
    ALTER TABLE car.Journey ADD CONSTRAINT FK_car_Journey_QueryRun
        FOREIGN KEY (QueryRunId) REFERENCES car.QueryRun (QueryRunId);
END
GO

IF OBJECT_ID(N'car.JourneyStep', N'U') IS NULL
BEGIN
    CREATE TABLE car.JourneyStep
    (
        JourneyStepId    bigint            NOT NULL IDENTITY(1, 1)
            CONSTRAINT PK_car_JourneyStep PRIMARY KEY,
        JourneyId        bigint            NOT NULL
            CONSTRAINT FK_car_JourneyStep_Journey
                REFERENCES car.Journey (JourneyId) ON DELETE CASCADE,
        StageNo          int               NOT NULL,
        Stage            nvarchar(128)     NOT NULL,
        StepStart        datetimeoffset(7) NULL,
        CallCount        int               NOT NULL
            CONSTRAINT DF_car_JourneyStep_CallCount DEFAULT 0,
        StageTotalMs     decimal(18, 2)    NULL,
        P50Ms            decimal(18, 2)    NULL,
        P90Ms            decimal(18, 2)    NULL,
        P95Ms            decimal(18, 2)    NULL,
        P99Ms            decimal(18, 2)    NULL,
        MaxMs            decimal(18, 2)    NULL,
        FailureRatePct   decimal(9, 2)     NULL,
        Status           nvarchar(16)      NULL,
        CONSTRAINT UQ_car_JourneyStep UNIQUE (JourneyId, StageNo)
    );

    CREATE INDEX IX_car_JourneyStep_StageNo
        ON car.JourneyStep (StageNo)
        INCLUDE (Stage, Status, StageTotalMs);
END
GO

IF OBJECT_ID(N'car.JourneyCall', N'U') IS NULL
BEGIN
    CREATE TABLE car.JourneyCall
    (
        JourneyCallId    bigint         NOT NULL IDENTITY(1, 1)
            CONSTRAINT PK_car_JourneyCall PRIMARY KEY,
        JourneyStepId    bigint         NOT NULL
            CONSTRAINT FK_car_JourneyCall_JourneyStep
                REFERENCES car.JourneyStep (JourneyStepId) ON DELETE CASCADE,
        Kind             nvarchar(32)   NULL,
        CallName         nvarchar(1024) NOT NULL,
        CorrelationId    nvarchar(128)  NULL,
        N                int            NULL,
        CallTotalMs      decimal(18, 2) NULL,
        P50Ms            decimal(18, 2) NULL,
        P90Ms            decimal(18, 2) NULL,
        P95Ms            decimal(18, 2) NULL,
        P99Ms            decimal(18, 2) NULL,
        MaxMs            decimal(18, 2) NULL,
        FailureRatePct   decimal(9, 2)  NULL,
        Status           nvarchar(16)   NULL,
        Detail           nvarchar(1024) NULL,
        ErrorsJson       nvarchar(max)  NULL,
        CONSTRAINT CK_car_JourneyCall_ErrorsJson CHECK (ErrorsJson IS NULL OR ISJSON(ErrorsJson) = 1)
    );

    CREATE INDEX IX_car_JourneyCall_Kind
        ON car.JourneyCall (Kind);

    CREATE INDEX IX_car_JourneyCall_JourneyStepId
        ON car.JourneyCall (JourneyStepId);

    CREATE INDEX IX_car_JourneyCall_Failed
        ON car.JourneyCall (FailureRatePct)
        WHERE FailureRatePct > 0;
END
GO

CREATE OR ALTER VIEW car.v_LatestQueryRun
AS
    SELECT
        r.QueryRunId,
        r.Domain,
        r.Window,
        r.Environment,
        r.ClubId,
        r.Host,
        r.FetchedAt,
        s.RecordCount,
        r.[RowCount],
        s.StepCount,
        s.CallCount,
        r.Source,
        r.KqlFile
    FROM car.CurrentSnapshot AS s
    INNER JOIN car.QueryRun AS r
        ON r.QueryRunId = s.QueryRunId;
GO

CREATE OR ALTER VIEW car.v_LatestJourney
AS
    SELECT j.*
    FROM car.Journey AS j
    INNER JOIN car.CurrentSnapshot AS s
        ON s.QueryRunId = j.QueryRunId;
GO

-- Ingest named-property JSON: an array of Cars KQL rows (the portal /api/journeys rows shape),
-- not the Application Insights tables[].rows[] matrix. Parameterize from the app; do not concatenate.
CREATE OR ALTER PROCEDURE car.usp_IngestKqlRows
    @Window       nvarchar(16),
    @Environment  nvarchar(32)     = NULL,
    @ClubId       nvarchar(64)     = NULL,
    @Host         nvarchar(256)    = NULL,
    @Source       nvarchar(32)     = N'live',
    @KqlFile      nvarchar(256)    = N'TMobile_Cars_Query_KQL_v6.txt',
    @RowsJson     nvarchar(max)     = NULL,
    @QueryRunId   uniqueidentifier = NULL OUTPUT,
    @RecordCount  int              = NULL OUTPUT,
    -- How the payload split across new journeys, journeys that advanced, and journeys
    -- re-reported unchanged by the overlapping window. Callers log these to see dedup working.
    @InsertedCount  int            = NULL OUTPUT,
    @UpdatedCount   int            = NULL OUTPUT,
    @UnchangedCount int            = NULL OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @RowsJson IS NULL OR ISJSON(@RowsJson) <> 1
    BEGIN
        THROW 50001, N'@RowsJson must be a JSON array of Cars KQL rows.', 1;
    END

    IF NULLIF(@ClubId, N'') IS NULL
        THROW 50002, N'@ClubId is required. Each club keeps its own current snapshot.', 1;

    IF @QueryRunId IS NULL
        SET @QueryRunId = NEWID();

    DECLARE @Now datetimeoffset(7) = SYSDATETIMEOFFSET();

    BEGIN TRANSACTION;

    INSERT INTO car.QueryRun
        (QueryRunId, Domain, Window, Environment, ClubId, Host, FetchedAt, [RowCount], Source, KqlFile)
    VALUES
        (@QueryRunId, N'car', @Window, @Environment, @ClubId, @Host, @Now, 0, @Source, @KqlFile);

    -- Which journeys this run touched, and whether they actually moved. Steps and calls
    -- are rebuilt only for the ones that moved.
    DECLARE @Touched TABLE
    (
        JourneyId bigint       NOT NULL PRIMARY KEY,
        Action    nvarchar(10) NOT NULL,
        Changed   bit          NOT NULL
    );

    -- Parsed once into a temp table because both the upsert and the "still seen" pass below read
    -- it. One row per journey: MERGE cannot match the same target row twice, and a payload that
    -- repeated a CorrelationId would abort the whole run.
    SELECT d.*
    INTO #Src
    FROM
    (
        SELECT
            r.CorrelationId,
            NULLIF(r.UserId, N'')                           AS UserId,
            r.Outcome                                       AS Outcome,
            ISNULL(r.LastStep, 0)                           AS LastStep,
            NULLIF(r.FailReason, N'')                       AS FailReason,
            ISNULL(r.BookOk, 0)                             AS BookOk,
            ISNULL(r.BookFail, 0)                           AS BookFail,
            ISNULL(r.DistinctOkRes, 0)                      AS DistinctOkRes,
            r.BookedReservations                            AS BookedReservations,
            ISNULL(r.FailedCalls, 0)                        AS FailedCalls,
            ISNULL(r.IsBookSuccessful, 0)                   AS IsBookSuccessful,
            ISNULL(r.PassedSearch, 0)                       AS PassedSearch,
            ISNULL(r.Exceptions, ISNULL(r.FailedCalls, 0))  AS Exceptions,
            r.JourneyTotalMs,
            r.SlowestCallMs,
            r.OverallP95,
            r.SearchToConfirmSeconds,
            r.FirstSeen,
            r.LastSeen,
            r.StepsJson,
            r.JourneyJson,
            ROW_NUMBER() OVER (PARTITION BY r.CorrelationId ORDER BY r.LastSeen DESC) AS rn
        FROM OPENJSON(@RowsJson)
        WITH
        (
            CorrelationId            nvarchar(128)     N'$.CorrelationId',
            UserId                   nvarchar(128)     N'$.UserId',
            Outcome                  nvarchar(256)     N'$.Outcome',
            LastStep                 int               N'$.LastStep',
            FailReason               nvarchar(1024)    N'$.FailReason',
            BookOk                   int               N'$.BookOk',
            BookFail                 int               N'$.BookFail',
            DistinctOkRes            int               N'$.DistinctOkRes',
            BookedReservations       nvarchar(max)     N'$.BookedReservations' AS JSON,
            FailedCalls              int               N'$.FailedCalls',
            IsBookSuccessful         bit               N'$.IsBookSuccessful',
            PassedSearch             bit               N'$.PassedSearch',
            Exceptions               int               N'$.Exceptions',
            JourneyTotalMs           decimal(18, 2)    N'$.JourneyTotalMs',
            SlowestCallMs            decimal(18, 2)    N'$.SlowestCallMs',
            OverallP95               decimal(18, 2)    N'$.OverallP95',
            SearchToConfirmSeconds   int               N'$.SearchToConfirm_s',
            FirstSeen                datetimeoffset(7) N'$.FirstSeen',
            LastSeen                 datetimeoffset(7) N'$.LastSeen',
            StepsJson                nvarchar(max)     N'$.Steps' AS JSON,
            JourneyJson              nvarchar(max)     N'$.Journey' AS JSON
        ) AS r
        WHERE NULLIF(r.CorrelationId, N'') IS NOT NULL
          AND r.Outcome IS NOT NULL
    ) AS d
    WHERE d.rn = 1;

    -- KQL session suffixes are window-relative. Reconcile every incoming row against
    -- persisted activity boundaries so a clipped continuation keeps its stored key.
    -- The same raw id after an inactivity gap gets a new stored suffix below.
    SELECT
        s.CorrelationId AS IncomingId,
        prior.CorrelationId AS StoredId,
        ROW_NUMBER() OVER (PARTITION BY prior.CorrelationId ORDER BY s.LastSeen DESC, s.CorrelationId) AS rn
    INTO #SessionStitch
    FROM #Src AS s
    CROSS APPLY (
        SELECT TOP (1) j.CorrelationId
        FROM car.Journey AS j
        WHERE (j.UserId = s.UserId OR (j.UserId IS NULL AND s.UserId IS NULL))
          AND (@ClubId IS NULL OR j.ClubId = @ClubId OR j.ClubId IS NULL)
          AND s.FirstSeen IS NOT NULL
          AND s.CorrelationId LIKE N'%-[0-9]%'
          AND j.CorrelationId LIKE N'%-[0-9]%'
          AND RIGHT(s.CorrelationId, CHARINDEX(N'-', REVERSE(s.CorrelationId)) - 1) NOT LIKE N'%[^0-9]%'
          AND RIGHT(j.CorrelationId, CHARINDEX(N'-', REVERSE(j.CorrelationId)) - 1) NOT LIKE N'%[^0-9]%'
          AND LEN(RIGHT(s.CorrelationId, CHARINDEX(N'-', REVERSE(s.CorrelationId)) - 1)) BETWEEN 1 AND 6
          AND LEN(RIGHT(j.CorrelationId, CHARINDEX(N'-', REVERSE(j.CorrelationId)) - 1)) BETWEEN 1 AND 6
          AND LEFT(j.CorrelationId, LEN(j.CorrelationId) - CHARINDEX(N'-', REVERSE(j.CorrelationId)))
              = LEFT(s.CorrelationId, LEN(s.CorrelationId) - CHARINDEX(N'-', REVERSE(s.CorrelationId)))
          AND j.LastSeen IS NOT NULL
          AND s.FirstSeen <= DATEADD(minute, 30, j.LastSeen)
          AND s.LastSeen >= DATEADD(minute, -30, j.FirstSeen)
        ORDER BY ABS(DATEDIFF(second, s.FirstSeen, j.LastSeen)), j.FirstSeen
    ) AS prior
    WHERE NOT EXISTS (
        SELECT 1 FROM #Src AS other WHERE other.CorrelationId = prior.CorrelationId
    );

    UPDATE s
    SET CorrelationId = stitch.StoredId
    FROM #Src AS s
    INNER JOIN #SessionStitch AS stitch
        ON stitch.IncomingId = s.CorrelationId
       AND stitch.rn = 1;

    -- A window-relative suffix can collide with an older, separate visit (for example,
    -- two 15-minute runs both report rawId-1). Allocate the next persisted suffix
    -- when that matching key is outside the inactivity boundary.
    ;WITH collisions AS
    (
        SELECT
            s.CorrelationId AS IncomingId,
            CONCAT(
                LEFT(s.CorrelationId, LEN(s.CorrelationId) - CHARINDEX(N'-', REVERSE(s.CorrelationId))),
                N'-',
                (
                    SELECT ISNULL(MAX(sequence.No), 0)
                    FROM
                    (
                        SELECT TRY_CONVERT(int, RIGHT(j.CorrelationId, CHARINDEX(N'-', REVERSE(j.CorrelationId)) - 1)) AS No
                        FROM car.Journey AS j
                        WHERE LEFT(j.CorrelationId, LEN(j.CorrelationId) - CHARINDEX(N'-', REVERSE(j.CorrelationId)))
                            = LEFT(s.CorrelationId, LEN(s.CorrelationId) - CHARINDEX(N'-', REVERSE(s.CorrelationId)))
                        UNION ALL
                        SELECT TRY_CONVERT(int, RIGHT(other.CorrelationId, CHARINDEX(N'-', REVERSE(other.CorrelationId)) - 1))
                        FROM #Src AS other
                        WHERE LEFT(other.CorrelationId, LEN(other.CorrelationId) - CHARINDEX(N'-', REVERSE(other.CorrelationId)))
                            = LEFT(s.CorrelationId, LEN(s.CorrelationId) - CHARINDEX(N'-', REVERSE(s.CorrelationId)))
                    ) AS sequence
                ) + ROW_NUMBER() OVER (
                    PARTITION BY LEFT(s.CorrelationId, LEN(s.CorrelationId) - CHARINDEX(N'-', REVERSE(s.CorrelationId)))
                    ORDER BY s.FirstSeen, s.CorrelationId)
            ) AS StoredId
        FROM #Src AS s
        WHERE EXISTS
        (
            SELECT 1
            FROM car.Journey AS j
            WHERE j.CorrelationId = s.CorrelationId
              AND NOT
              (
                  s.FirstSeen <= DATEADD(minute, 30, j.LastSeen)
                  AND s.LastSeen >= DATEADD(minute, -30, j.FirstSeen)
              )
        )
    )
    UPDATE s
    SET CorrelationId = collisions.StoredId
    FROM #Src AS s
    INNER JOIN collisions ON collisions.IncomingId = s.CorrelationId;

    DROP TABLE #SessionStitch;

    MERGE car.Journey WITH (HOLDLOCK) AS target
    USING #Src AS src
        ON target.CorrelationId = src.CorrelationId
    -- The journey moved on, so the stored row is stale: rewrite it. Journeys matched but not
    -- advanced deliberately fall through to the pass below; SQL Server allows only one
    -- WHEN MATCHED ... UPDATE per MERGE.
    WHEN MATCHED AND src.LastSeen IS NOT NULL
                 AND (target.LastSeen IS NULL OR src.LastSeen > target.LastSeen) THEN
        UPDATE SET
            QueryRunId             = @QueryRunId,
            ClubId                 = @ClubId,
            UserId                 = src.UserId,
            Outcome                = CASE
                                         WHEN target.FirstSeen IS NOT NULL
                                          AND src.FirstSeen > target.FirstSeen
                                          AND target.IsBookSuccessful = 1
                                          AND src.IsBookSuccessful = 0
                                             THEN target.Outcome
                                         ELSE src.Outcome
                                     END,
            LastStep               = CASE
                                         WHEN target.FirstSeen IS NOT NULL
                                          AND src.FirstSeen > target.FirstSeen
                                          AND target.LastStep > src.LastStep
                                             THEN target.LastStep
                                         ELSE src.LastStep
                                     END,
            FailReason             = CASE
                                         WHEN target.FirstSeen IS NOT NULL
                                          AND src.FirstSeen > target.FirstSeen
                                          AND target.IsBookSuccessful = 1
                                          AND src.IsBookSuccessful = 0
                                             THEN target.FailReason
                                         ELSE src.FailReason
                                     END,
            BookOk                 = CASE
                                         WHEN target.FirstSeen IS NOT NULL
                                          AND src.FirstSeen > target.FirstSeen
                                          AND target.BookOk > src.BookOk
                                             THEN target.BookOk
                                         ELSE src.BookOk
                                     END,
            BookFail               = src.BookFail,
            DistinctOkRes          = CASE
                                         WHEN target.FirstSeen IS NOT NULL
                                          AND src.FirstSeen > target.FirstSeen
                                          AND target.DistinctOkRes > src.DistinctOkRes
                                             THEN target.DistinctOkRes
                                         ELSE src.DistinctOkRes
                                     END,
            BookedReservations     = CASE
                                         WHEN target.FirstSeen IS NOT NULL
                                          AND src.FirstSeen > target.FirstSeen
                                          AND (
                                              src.BookedReservations IS NULL
                                              OR src.BookedReservations IN (N'', N'[]', N'null')
                                          )
                                          AND NULLIF(target.BookedReservations, N'') IS NOT NULL
                                          AND target.BookedReservations <> N'[]'
                                             THEN target.BookedReservations
                                         ELSE src.BookedReservations
                                     END,
            FailedCalls            = src.FailedCalls,
            IsBookSuccessful       = CASE
                                         WHEN target.FirstSeen IS NOT NULL
                                          AND src.FirstSeen > target.FirstSeen
                                          AND target.IsBookSuccessful = 1
                                             THEN CONVERT(bit, 1)
                                         ELSE src.IsBookSuccessful
                                     END,
            PassedSearch           = CASE
                                         WHEN target.FirstSeen IS NOT NULL
                                          AND src.FirstSeen > target.FirstSeen
                                          AND target.PassedSearch = 1
                                             THEN CONVERT(bit, 1)
                                         ELSE src.PassedSearch
                                     END,
            Exceptions             = src.Exceptions,
            JourneyTotalMs         = src.JourneyTotalMs,
            SlowestCallMs          = src.SlowestCallMs,
            OverallP95             = src.OverallP95,
            SearchToConfirmSeconds = src.SearchToConfirmSeconds,
            -- FirstSeen is when we first observed the journey; the newest payload may have dropped
            -- the journey's earlier calls out of its window, so never let it move forward.
            FirstSeen              = CASE
                                         WHEN target.FirstSeen IS NULL THEN src.FirstSeen
                                         WHEN src.FirstSeen IS NULL THEN target.FirstSeen
                                         WHEN src.FirstSeen < target.FirstSeen THEN src.FirstSeen
                                         ELSE target.FirstSeen
                                     END,
            LastSeen               = src.LastSeen,
            StepsJson              = CASE
                                         WHEN target.FirstSeen IS NOT NULL
                                          AND src.FirstSeen > target.FirstSeen
                                          AND ISJSON(target.StepsJson) = 1
                                          AND ISJSON(src.StepsJson) = 1
                                             THEN COALESCE(
                                                 (
                                                     SELECT N'[' + STRING_AGG(N'"' + STRING_ESCAPE(names.stepName, 'json') + N'"', N',') + N']'
                                                     FROM (
                                                         SELECT DISTINCT steps.[value] AS stepName
                                                         FROM (
                                                             SELECT head.[value]
                                                             FROM OPENJSON(target.StepsJson) AS head
                                                             WHERE ISJSON(head.[value]) = 0
                                                             UNION
                                                             SELECT tail.[value]
                                                             FROM OPENJSON(src.StepsJson) AS tail
                                                             WHERE ISJSON(tail.[value]) = 0
                                                         ) AS steps
                                                     ) AS names
                                                 ),
                                                 src.StepsJson)
                                         ELSE src.StepsJson
                                     END,
            JourneyJson            = CASE
                                         WHEN target.FirstSeen IS NOT NULL
                                          AND src.FirstSeen > target.FirstSeen
                                          AND ISJSON(target.JourneyJson) = 1
                                          AND ISJSON(src.JourneyJson) = 1
                                             THEN COALESCE(
                                                 (
                                                     SELECT N'[' + STRING_AGG(CAST(piece.[value] AS nvarchar(max)), N',')
                                                            WITHIN GROUP (ORDER BY TRY_CONVERT(int, JSON_VALUE(piece.[value], '$.stageNo'))) + N']'
                                                     FROM (
                                                         SELECT head.[value]
                                                         FROM OPENJSON(target.JourneyJson) AS head
                                                         WHERE (
                                                             TRY_CONVERT(datetimeoffset(7), JSON_VALUE(head.[value], '$.t')) IS NULL
                                                             OR TRY_CONVERT(datetimeoffset(7), JSON_VALUE(head.[value], '$.t')) < src.FirstSeen
                                                         )
                                                         AND NOT EXISTS (
                                                             SELECT 1
                                                             FROM OPENJSON(src.JourneyJson) AS incoming
                                                             WHERE JSON_VALUE(incoming.[value], '$.stageNo') = JSON_VALUE(head.[value], '$.stageNo')
                                                               AND JSON_VALUE(incoming.[value], '$.stageNo') IS NOT NULL
                                                         )
                                                         UNION ALL
                                                         SELECT tail.[value]
                                                         FROM OPENJSON(src.JourneyJson) AS tail
                                                     ) AS piece
                                                 ),
                                                 src.JourneyJson)
                                         ELSE src.JourneyJson
                                     END
    WHEN NOT MATCHED BY TARGET THEN
        INSERT
        (
            QueryRunId, ClubId, CorrelationId, UserId, Outcome, LastStep, FailReason,
            BookOk, BookFail, DistinctOkRes, BookedReservations,
            FailedCalls, IsBookSuccessful, PassedSearch, Exceptions,
            JourneyTotalMs, SlowestCallMs, OverallP95, SearchToConfirmSeconds,
            FirstSeen, LastSeen, StepsJson, JourneyJson
        )
        VALUES
        (
            @QueryRunId, @ClubId, src.CorrelationId, src.UserId, src.Outcome, src.LastStep, src.FailReason,
            src.BookOk, src.BookFail, src.DistinctOkRes, src.BookedReservations,
            src.FailedCalls, src.IsBookSuccessful, src.PassedSearch, src.Exceptions,
            src.JourneyTotalMs, src.SlowestCallMs, src.OverallP95, src.SearchToConfirmSeconds,
            src.FirstSeen, src.LastSeen, src.StepsJson, src.JourneyJson
        )
    -- Every row this MERGE emits was either new or rewritten, so all of them need their steps
    -- and calls rebuilt.
    OUTPUT inserted.JourneyId, $action, CONVERT(bit, 1)
    INTO @Touched (JourneyId, Action, Changed);

    -- Journeys the overlapping window re-reported with nothing new. Their stored row stays as is;
    -- re-stamping QueryRunId is what keeps them in the snapshot the portal reads.
    UPDATE j
    SET QueryRunId = @QueryRunId,
        ClubId     = @ClubId
    OUTPUT inserted.JourneyId, N'SEEN', CONVERT(bit, 0)
    INTO @Touched (JourneyId, Action, Changed)
    FROM car.Journey AS j
    INNER JOIN #Src AS s
        ON s.CorrelationId = j.CorrelationId
    WHERE NOT EXISTS (SELECT 1 FROM @Touched AS t WHERE t.JourneyId = j.JourneyId);

    -- Steps and calls are derived from JourneyJson, so they are only stale where the journey moved.
    -- Deleting the steps takes their calls with them through the cascade.
    DELETE FROM car.JourneyStep
    WHERE JourneyId IN (SELECT JourneyId FROM @Touched WHERE Changed = 1);

    INSERT INTO car.JourneyStep
    (
        JourneyId, StageNo, Stage, StepStart, CallCount, StageTotalMs,
        P50Ms, P90Ms, P95Ms, P99Ms, MaxMs, FailureRatePct, Status
    )
    SELECT
        j.JourneyId,
        s.StageNo,
        s.Stage,
        s.StepStart,
        ISNULL(s.CallCount, 0),
        s.StageTotalMs,
        s.P50Ms,
        s.P90Ms,
        s.P95Ms,
        s.P99Ms,
        s.MaxMs,
        s.FailureRatePct,
        s.Status
    FROM car.Journey AS j
    INNER JOIN @Touched AS t
        ON t.JourneyId = j.JourneyId
       AND t.Changed = 1
    CROSS APPLY OPENJSON(j.JourneyJson) AS stepJson
    CROSS APPLY OPENJSON(stepJson.[value])
    WITH
    (
        StageNo        int               N'$.stageNo',
        Stage          nvarchar(128)     N'$.stage',
        StepStart      datetimeoffset(7) N'$.t',
        CallCount      int               N'$.callCount',
        StageTotalMs   decimal(18, 2)    N'$.stageTotalMs',
        P50Ms          decimal(18, 2)    N'$.p50Ms',
        P90Ms          decimal(18, 2)    N'$.p90Ms',
        P95Ms          decimal(18, 2)    N'$.p95Ms',
        P99Ms          decimal(18, 2)    N'$.p99Ms',
        MaxMs          decimal(18, 2)    N'$.maxMs',
        FailureRatePct decimal(9, 2)     N'$.failureRatePct',
        Status         nvarchar(16)      N'$.status',
        CallsJson      nvarchar(max)     N'$.calls' AS JSON
    ) AS s
    WHERE s.StageNo IS NOT NULL;

    INSERT INTO car.JourneyCall
    (
        JourneyStepId, Kind, CallName, CorrelationId, N,
        CallTotalMs, P50Ms, P90Ms, P95Ms, P99Ms, MaxMs,
        FailureRatePct, Status, Detail, ErrorsJson
    )
    SELECT
        st.JourneyStepId,
        c.Kind,
        c.CallName,
        c.CorrelationId,
        c.N,
        c.CallTotalMs,
        c.P50Ms,
        c.P90Ms,
        c.P95Ms,
        c.P99Ms,
        c.MaxMs,
        c.FailureRatePct,
        c.Status,
        c.Detail,
        c.ErrorsJson
    FROM car.Journey AS j
    INNER JOIN @Touched AS t
        ON t.JourneyId = j.JourneyId
       AND t.Changed = 1
    INNER JOIN car.JourneyStep AS st
        ON st.JourneyId = j.JourneyId
    CROSS APPLY OPENJSON(j.JourneyJson) AS stepJson
    CROSS APPLY OPENJSON(stepJson.[value])
    WITH
    (
        StageNo   int           N'$.stageNo',
        CallsJson nvarchar(max) N'$.calls' AS JSON
    ) AS s
    CROSS APPLY OPENJSON(s.CallsJson) AS callJson
    CROSS APPLY OPENJSON(callJson.[value])
    WITH
    (
        Kind           nvarchar(32)   N'$.kind',
        CallName       nvarchar(1024) N'$.call',
        CorrelationId  nvarchar(128)  N'$.correlationId',
        N              int            N'$.n',
        CallTotalMs    decimal(18, 2) N'$.callTotalMs',
        P50Ms          decimal(18, 2) N'$.p50Ms',
        P90Ms          decimal(18, 2) N'$.p90Ms',
        P95Ms          decimal(18, 2) N'$.p95Ms',
        P99Ms          decimal(18, 2) N'$.p99Ms',
        MaxMs          decimal(18, 2) N'$.maxMs',
        FailureRatePct decimal(9, 2)  N'$.failureRatePct',
        Status         nvarchar(16)   N'$.status',
        Detail         nvarchar(1024) N'$.detail',
        ErrorsJson     nvarchar(max)  N'$.errors' AS JSON
    ) AS c
    WHERE s.StageNo = st.StageNo
      AND c.CallName IS NOT NULL;

    -- Counted over every journey this run saw, not just the ones it rewrote: that whole set is
    -- what the snapshot exposes.
    DECLARE @JourneyCount int = (SELECT COUNT(*) FROM @Touched);
    DECLARE @StepCount int =
        (SELECT COUNT(*) FROM car.JourneyStep AS st
         INNER JOIN @Touched AS t ON t.JourneyId = st.JourneyId);
    DECLARE @CallCount int =
        (SELECT COUNT(*) FROM car.JourneyCall AS c
         INNER JOIN car.JourneyStep AS st ON st.JourneyStepId = c.JourneyStepId
         INNER JOIN @Touched AS t ON t.JourneyId = st.JourneyId);

    SET @RecordCount = @JourneyCount;
    SET @InsertedCount  = (SELECT COUNT(*) FROM @Touched WHERE Action = N'INSERT');
    SET @UpdatedCount   = (SELECT COUNT(*) FROM @Touched WHERE Action = N'UPDATE');
    SET @UnchangedCount = (SELECT COUNT(*) FROM @Touched WHERE Action = N'SEEN');

    UPDATE car.QueryRun
    SET [RowCount] = @JourneyCount
    WHERE QueryRunId = @QueryRunId;

    MERGE car.CurrentSnapshot WITH (HOLDLOCK) AS target
    USING (SELECT N'car' AS Domain, @ClubId AS ClubId) AS src
        ON target.Domain = src.Domain
       AND target.ClubId = src.ClubId
    WHEN MATCHED AND target.FetchedAt <= @Now THEN
        UPDATE SET
            QueryRunId  = @QueryRunId,
            FetchedAt   = @Now,
            Window      = @Window,
            Environment = @Environment,
            ClubId      = @ClubId,
            RecordCount = @JourneyCount,
            StepCount   = @StepCount,
            CallCount   = @CallCount
    WHEN NOT MATCHED THEN
        INSERT (Domain, QueryRunId, FetchedAt, Window, Environment, ClubId, RecordCount, StepCount, CallCount)
        VALUES (N'car', @QueryRunId, @Now, @Window, @Environment, @ClubId, @JourneyCount, @StepCount, @CallCount);

    COMMIT TRANSACTION;
END
GO

-- Read path: @RecordCount is how many Journey rows the following SELECT must return.
-- Filter by QueryRunId, which selects the journeys the newest run saw; the table itself holds
-- every journey ever ingested. Do not SELECT TOP (@RecordCount) from the whole Journey table.
CREATE OR ALTER PROCEDURE car.usp_GetLatestJourneys
    @QueryRunId  uniqueidentifier = NULL OUTPUT,
    @RecordCount int              = NULL OUTPUT,
    @FetchedAt   datetimeoffset(7) = NULL OUTPUT,
    @Window      nvarchar(16)     = NULL OUTPUT,
    @Environment nvarchar(32)     = NULL OUTPUT,
    @RequestedClubId nvarchar(64) = NULL,
    @ClubId      nvarchar(64)     = NULL OUTPUT,
    @Host        nvarchar(256)    = NULL OUTPUT,
    @SearchId    nvarchar(256)    = NULL,
    @Status      nvarchar(64)     = NULL,
    @RowCap      int              = 2147483647,
    @Since       datetimeoffset(7) = NULL
AS
BEGIN
    SET NOCOUNT ON;

SET @QueryRunId = NULL;
SET @RecordCount = NULL;
SET @FetchedAt = NULL;
SET @Window = NULL;
SET @Environment = NULL;
SET @ClubId = NULL;
SET @Host = NULL;

IF @RequestedClubId IS NULL
BEGIN
    SELECT TOP (1)
        @QueryRunId  = QueryRunId,
        @FetchedAt   = FetchedAt,
        @Window      = Window,
        @Environment = Environment,
        @ClubId      = ClubId
    FROM car.CurrentSnapshot
    WHERE Domain = N'car'
    ORDER BY FetchedAt DESC;

    SELECT @RecordCount = COALESCE(SUM(RecordCount), 0)
    FROM car.CurrentSnapshot
    WHERE Domain = N'car';
END
ELSE
BEGIN
    SELECT
        @QueryRunId  = QueryRunId,
        @RecordCount = RecordCount,
        @FetchedAt   = FetchedAt,
        @Window      = Window,
        @Environment = Environment,
        @ClubId      = ClubId
    FROM car.CurrentSnapshot
    WHERE Domain = N'car'
      AND ClubId = @RequestedClubId;
END

SELECT @Host = Host
FROM car.QueryRun
WHERE QueryRunId = @QueryRunId;

    IF @QueryRunId IS NULL
    BEGIN
        SET @RecordCount = 0;
        RETURN;
    END

    SELECT TOP (@RowCap)
        JourneyId,
        QueryRunId,
        CorrelationId,
        ClubId,
        UserId,
        Outcome,
        LastStep,
        FailReason,
        BookOk,
        BookFail,
        DistinctOkRes,
        BookedReservations,
        FailedCalls,
        IsBookSuccessful,
        PassedSearch,
        Exceptions,
        JourneyTotalMs,
        SlowestCallMs,
        OverallP95,
        SearchToConfirmSeconds,
        FirstSeen,
        LastSeen,
        StepsJson,
        JourneyJson
    FROM car.Journey
    WHERE (
            (
                @Since IS NOT NULL
                AND (LastSeen IS NULL OR LastSeen >= @Since)
                AND QueryRunId IN (
                    SELECT QueryRunId
                    FROM car.QueryRun
                    WHERE (@RequestedClubId IS NULL OR ClubId = @RequestedClubId)
                      AND (@Environment IS NULL OR Environment IS NULL OR Environment = @Environment)
                )
            )
            OR (
                @Since IS NULL
                AND (
                    (@RequestedClubId IS NOT NULL AND QueryRunId = @QueryRunId)
                    OR (
                        @RequestedClubId IS NULL
                        AND QueryRunId IN (SELECT QueryRunId FROM car.CurrentSnapshot WHERE Domain = N'car')
                    )
                )
            )
          )
      AND (
          @SearchId IS NULL
          OR UserId LIKE N'%' + @SearchId + N'%'
          OR CorrelationId LIKE N'%' + @SearchId + N'%'
          OR BookedReservations LIKE N'%' + @SearchId + N'%'
      )
      -- @Status is reserved until UI health states are mapped to domain outcomes.
    ORDER BY LastSeen DESC;
END
GO
