-- Air journey store for KQL rows from TMobile_AIR_Query_KQL_v6_demo_2h.txt.
-- Grain: one air.Journey row per session (CorrelationId), carrying that session's current state.
-- Air groups by session, not by member, so UserId is an attribute the session reports rather than
-- the group key; it is an updatable column and must not be used to identify a row.
-- Nested Journey[] / calls[] are shredded into air.JourneyStep and air.JourneyCall.
-- JourneyJson keeps the original Kusto bag so the portal can still round-trip raw rows.
--
-- The KQL job may run many times a day and its window overlaps, so consecutive runs re-report
-- the same sessions. Ingestion upserts on CorrelationId: a session seen again only has its row
-- rewritten when LastSeen advanced, otherwise the run just re-stamps QueryRunId to record
-- "still seen". air.Journey.QueryRunId therefore means "the last run that saw this session",
-- which is what makes the read below return exactly the sessions present in the newest run.
--
-- Do not load TMobile_HOTEL_Query_KQL_v6d_demo_2h.txt into these tables.
-- Hotel v6d is one row per UserId and projects Outcome, SessionCount, BookedReservations, etc.
--
-- Auth when calling from Azure: Managed Identity + Azure SQL (not SQL passwords in source).
-- Connection strings belong in Key Vault / Function app settings.

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'air')
    EXEC(N'CREATE SCHEMA air');
GO

IF OBJECT_ID(N'air.QueryRun', N'U') IS NULL
BEGIN
    CREATE TABLE air.QueryRun
    (
        QueryRunId   uniqueidentifier NOT NULL
            CONSTRAINT PK_air_QueryRun PRIMARY KEY
            CONSTRAINT DF_air_QueryRun_Id DEFAULT NEWSEQUENTIALID(),
        Domain       nvarchar(32)     NOT NULL
            CONSTRAINT DF_air_QueryRun_Domain DEFAULT N'air',
        Window       nvarchar(16)     NOT NULL,
        Environment  nvarchar(32)     NULL,
        ClubId       nvarchar(64)     NULL,
        Host         nvarchar(256)    NULL,
        FetchedAt    datetimeoffset(7) NOT NULL
            CONSTRAINT DF_air_QueryRun_FetchedAt DEFAULT SYSDATETIMEOFFSET(),
        [RowCount]   int              NOT NULL
            CONSTRAINT DF_air_QueryRun_RowCount DEFAULT 0,
        Source       nvarchar(32)     NOT NULL
            CONSTRAINT DF_air_QueryRun_Source DEFAULT N'live',
        KqlFile      nvarchar(256)    NULL
            CONSTRAINT DF_air_QueryRun_KqlFile DEFAULT N'TMobile_AIR_Query_KQL_v6_demo_2h.txt',
        CONSTRAINT CK_air_QueryRun_Domain CHECK (Domain = N'air')
    );

    CREATE INDEX IX_air_QueryRun_FetchedAt
        ON air.QueryRun (FetchedAt DESC);
END
GO

-- One row per club. RecordCount is the fetch size for air.Journey rows whose
-- QueryRunId is that club's CurrentSnapshot.QueryRunId.
IF OBJECT_ID(N'air.CurrentSnapshot', N'U') IS NULL
BEGIN
    CREATE TABLE air.CurrentSnapshot
    (
        Domain       nvarchar(32)      NOT NULL
            CONSTRAINT DF_air_CurrentSnapshot_Domain DEFAULT N'air',
        QueryRunId   uniqueidentifier  NOT NULL
            CONSTRAINT FK_air_CurrentSnapshot_QueryRun
                REFERENCES air.QueryRun (QueryRunId),
        FetchedAt    datetimeoffset(7) NOT NULL,
        Window       nvarchar(16)      NOT NULL,
        Environment  nvarchar(32)      NULL,
        ClubId       nvarchar(64)      NOT NULL,
        RecordCount  int               NOT NULL
            CONSTRAINT DF_air_CurrentSnapshot_RecordCount DEFAULT 0,
        StepCount    int               NOT NULL
            CONSTRAINT DF_air_CurrentSnapshot_StepCount DEFAULT 0,
        CallCount    int               NOT NULL
            CONSTRAINT DF_air_CurrentSnapshot_CallCount DEFAULT 0,
        CONSTRAINT PK_air_CurrentSnapshot PRIMARY KEY (Domain, ClubId),
        CONSTRAINT CK_air_CurrentSnapshot_Domain CHECK (Domain = N'air'),
        CONSTRAINT CK_air_CurrentSnapshot_RecordCount CHECK (RecordCount >= 0)
    );
END
GO

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
IF OBJECT_ID(N'air.CurrentSnapshot', N'U') IS NOT NULL
   AND NOT EXISTS (
        SELECT 1
        FROM sys.indexes AS i
        INNER JOIN sys.index_columns AS ic
            ON ic.object_id = i.object_id AND ic.index_id = i.index_id
        INNER JOIN sys.columns AS c
            ON c.object_id = ic.object_id AND c.column_id = ic.column_id
        WHERE i.object_id = OBJECT_ID(N'air.CurrentSnapshot')
          AND i.is_primary_key = 1
          AND c.name = N'ClubId'
   )
BEGIN
    DECLARE @pk sysname;
    SELECT @pk = name
    FROM sys.key_constraints
    WHERE parent_object_id = OBJECT_ID(N'air.CurrentSnapshot')
      AND [type] = N'PK';

    IF @pk IS NOT NULL
    BEGIN
        DECLARE @drop nvarchar(400) = N'ALTER TABLE air.CurrentSnapshot DROP CONSTRAINT ' + QUOTENAME(@pk);
        EXEC(@drop);
    END

    DELETE FROM air.CurrentSnapshot WHERE ClubId IS NULL;

    ALTER TABLE air.CurrentSnapshot ALTER COLUMN ClubId nvarchar(64) NOT NULL;
    ALTER TABLE air.CurrentSnapshot
        ADD CONSTRAINT PK_air_CurrentSnapshot PRIMARY KEY (Domain, ClubId);
END
GO

IF OBJECT_ID(N'air.Journey', N'U') IS NULL
BEGIN
    CREATE TABLE air.Journey
    (
        JourneyId          bigint            NOT NULL IDENTITY(1, 1)
            CONSTRAINT PK_air_Journey PRIMARY KEY,
        QueryRunId         uniqueidentifier  NOT NULL
            CONSTRAINT FK_air_Journey_QueryRun
                REFERENCES air.QueryRun (QueryRunId) ON DELETE CASCADE,
        CorrelationId      nvarchar(36)      NOT NULL,
        UserId             nvarchar(128)     NULL,
        ClubId             nvarchar(64)      NULL,
        Status             nvarchar(64)      NOT NULL,
        BookingId          nvarchar(128)     NULL,
        BookOk             int               NOT NULL
            CONSTRAINT DF_air_Journey_BookOk DEFAULT 0,
        BookFail           int               NOT NULL
            CONSTRAINT DF_air_Journey_BookFail DEFAULT 0,
        FailedCalls        int               NOT NULL
            CONSTRAINT DF_air_Journey_FailedCalls DEFAULT 0,
        IsBookSuccessful   bit               NOT NULL
            CONSTRAINT DF_air_Journey_IsBookSuccessful DEFAULT 0,
        PassedSearch       bit               NOT NULL
            CONSTRAINT DF_air_Journey_PassedSearch DEFAULT 0,
        Exceptions         int               NOT NULL
            CONSTRAINT DF_air_Journey_Exceptions DEFAULT 0,
        JourneyTotalMs     decimal(18, 2)    NULL,
        OverallP95         decimal(18, 2)    NULL,
        SlowestCallMs      decimal(18, 2)    NULL,
        Route              nvarchar(256)     NULL,
        StepsCompleted     int               NOT NULL
            CONSTRAINT DF_air_Journey_StepsCompleted DEFAULT 0,
        FunnelStep         int               NOT NULL
            CONSTRAINT DF_air_Journey_FunnelStep DEFAULT 0,
        LastStep           int               NOT NULL
            CONSTRAINT DF_air_Journey_LastStep DEFAULT 0,
        FirstSeen          datetimeoffset(7) NULL,
        LastSeen           datetimeoffset(7) NULL,
        SourcesJson        nvarchar(max)     NULL,
        StepsJson          nvarchar(max)     NULL,
        JourneyJson        nvarchar(max)     NULL,
        CONSTRAINT UQ_air_Journey_Run_Correlation UNIQUE (QueryRunId, CorrelationId),
        CONSTRAINT CK_air_Journey_SourcesJson CHECK (SourcesJson IS NULL OR ISJSON(SourcesJson) = 1),
        CONSTRAINT CK_air_Journey_StepsJson   CHECK (StepsJson   IS NULL OR ISJSON(StepsJson)   = 1),
        CONSTRAINT CK_air_Journey_JourneyJson CHECK (JourneyJson IS NULL OR ISJSON(JourneyJson) = 1)
    );

    CREATE INDEX IX_air_Journey_LastSeen
        ON air.Journey (LastSeen DESC)
        INCLUDE (CorrelationId, Status, UserId, ClubId);

    CREATE INDEX IX_air_Journey_UserId_LastSeen
        ON air.Journey (UserId, LastSeen DESC);

    CREATE INDEX IX_air_Journey_Status_LastSeen
        ON air.Journey (Status, LastSeen DESC);

    CREATE INDEX IX_air_Journey_ClubId_LastSeen
        ON air.Journey (ClubId, LastSeen DESC);
END
GO

-- Databases written by the previous insert-per-run ingestion hold one row per session per run.
-- Collapse them to the newest row per session so the unique index below can exist. Steps and
-- calls of the discarded rows go with them through their cascading foreign keys.
IF OBJECT_ID(N'air.Journey', N'U') IS NOT NULL
   AND EXISTS (SELECT 1 FROM air.Journey GROUP BY CorrelationId HAVING COUNT(*) > 1)
BEGIN
    WITH ranked AS
    (
        SELECT
            JourneyId,
            ROW_NUMBER() OVER (PARTITION BY CorrelationId ORDER BY LastSeen DESC, JourneyId DESC) AS rn
        FROM air.Journey
    )
    DELETE FROM air.Journey
    WHERE JourneyId IN (SELECT JourneyId FROM ranked WHERE rn > 1);
END
GO

-- The grain is the session, so CorrelationId is the identity ingestion upserts on.
IF OBJECT_ID(N'air.Journey', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE name = N'UQ_air_Journey_CorrelationId'
                     AND object_id = OBJECT_ID(N'air.Journey'))
    CREATE UNIQUE INDEX UQ_air_Journey_CorrelationId ON air.Journey (CorrelationId);
GO

-- QueryRunId now points at the last run that saw the session, not at the run that created the row,
-- so cascading from QueryRun would delete sessions that are still current the moment old runs are
-- purged. Fail loudly on such a delete instead.
IF EXISTS (SELECT 1 FROM sys.foreign_keys
           WHERE name = N'FK_air_Journey_QueryRun' AND delete_referential_action = 1)
BEGIN
    ALTER TABLE air.Journey DROP CONSTRAINT FK_air_Journey_QueryRun;
    ALTER TABLE air.Journey ADD CONSTRAINT FK_air_Journey_QueryRun
        FOREIGN KEY (QueryRunId) REFERENCES air.QueryRun (QueryRunId);
END
GO

IF OBJECT_ID(N'air.JourneyStep', N'U') IS NULL
BEGIN
    CREATE TABLE air.JourneyStep
    (
        JourneyStepId bigint         NOT NULL IDENTITY(1, 1)
            CONSTRAINT PK_air_JourneyStep PRIMARY KEY,
        JourneyId     bigint         NOT NULL
            CONSTRAINT FK_air_JourneyStep_Journey
                REFERENCES air.Journey (JourneyId) ON DELETE CASCADE,
        StepNo        int            NOT NULL,
        Step          nvarchar(128)  NOT NULL,
        OffsetMs      decimal(18, 2) NULL,
        DurationMs    decimal(18, 2) NULL,
        SumCallMs     decimal(18, 2) NULL,
        SumApiMs      decimal(18, 2) NULL,
        SumUiMs       decimal(18, 2) NULL,
        MaxCallMs     decimal(18, 2) NULL,
        P95Ms         decimal(18, 2) NULL,
        WallMs        decimal(18, 2) NULL,
        CallCount     int            NOT NULL
            CONSTRAINT DF_air_JourneyStep_CallCount DEFAULT 0,
        Fails         int            NOT NULL
            CONSTRAINT DF_air_JourneyStep_Fails DEFAULT 0,
        CONSTRAINT UQ_air_JourneyStep UNIQUE (JourneyId, StepNo)
    );

    CREATE INDEX IX_air_JourneyStep_StepNo
        ON air.JourneyStep (StepNo)
        INCLUDE (Step, Fails, DurationMs);
END
GO

IF OBJECT_ID(N'air.JourneyCall', N'U') IS NULL
BEGIN
    CREATE TABLE air.JourneyCall
    (
        JourneyCallId  bigint         NOT NULL IDENTITY(1, 1)
            CONSTRAINT PK_air_JourneyCall PRIMARY KEY,
        JourneyStepId  bigint         NOT NULL
            CONSTRAINT FK_air_JourneyCall_JourneyStep
                REFERENCES air.JourneyStep (JourneyStepId) ON DELETE CASCADE,
        Name           nvarchar(1024) NOT NULL,
        Kind           nvarchar(32)   NULL,
        Source         nvarchar(32)   NULL,
        DurationMs     decimal(18, 2) NULL,
        DurationSource nvarchar(128)  NULL,
        MaxMs          decimal(18, 2) NULL,
        P50Ms          decimal(18, 2) NULL,
        P90Ms          decimal(18, 2) NULL,
        P95Ms          decimal(18, 2) NULL,
        P99Ms          decimal(18, 2) NULL,
        GapMs          decimal(18, 2) NULL,
        OffsetMs       decimal(18, 2) NULL,
        Failed         bit            NOT NULL
            CONSTRAINT DF_air_JourneyCall_Failed DEFAULT 0,
        Status         nvarchar(64)   NULL,
        Target         nvarchar(512)  NULL,
        Path           nvarchar(1024) NULL
    );

    CREATE INDEX IX_air_JourneyCall_Failed
        ON air.JourneyCall (Failed)
        WHERE Failed = 1;

CREATE INDEX IX_air_JourneyCall_Kind_Source
        ON air.JourneyCall (Kind, Source);

    CREATE INDEX IX_air_JourneyCall_JourneyStepId
        ON air.JourneyCall (JourneyStepId);
END
GO

CREATE OR ALTER VIEW air.v_LatestQueryRun
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
    FROM air.CurrentSnapshot AS s
    INNER JOIN air.QueryRun AS r
        ON r.QueryRunId = s.QueryRunId;
GO

CREATE OR ALTER VIEW air.v_LatestJourney
AS
    SELECT j.*
    FROM air.Journey AS j
    INNER JOIN air.CurrentSnapshot AS s
        ON s.QueryRunId = j.QueryRunId;
GO

-- Ingest named-property JSON: an array of Air KQL rows (the portal /api/journeys rows shape),
-- not the Application Insights tables[].rows[] matrix. Parameterize from the app; do not concatenate.
CREATE OR ALTER PROCEDURE air.usp_IngestKqlRows
    @Window       nvarchar(16),
    @Environment  nvarchar(32)     = NULL,
    @ClubId       nvarchar(64)     = NULL,
    @Host         nvarchar(256)    = NULL,
    @Source       nvarchar(32)     = N'live',
    @KqlFile      nvarchar(256)    = N'TMobile_AIR_Query_KQL_v6_demo_2h.txt',
    @RowsJson     nvarchar(max)     = NULL,
    @QueryRunId   uniqueidentifier = NULL OUTPUT,
    @RecordCount  int              = NULL OUTPUT,
    -- How the payload split across new sessions, sessions whose journey advanced, and sessions
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
        THROW 50001, N'@RowsJson must be a JSON array of Air KQL rows.', 1;
    END

    IF NULLIF(@ClubId, N'') IS NULL
        THROW 50002, N'@ClubId is required. Each club keeps its own current snapshot.', 1;

    IF @QueryRunId IS NULL
        SET @QueryRunId = NEWID();

    DECLARE @Now datetimeoffset(7) = SYSDATETIMEOFFSET();

    BEGIN TRANSACTION;

    INSERT INTO air.QueryRun
        (QueryRunId, Domain, Window, Environment, ClubId, Host, FetchedAt, [RowCount], Source, KqlFile)
    VALUES
        (@QueryRunId, N'air', @Window, @Environment, @ClubId, @Host, @Now, 0, @Source, @KqlFile);

    -- Which sessions this run touched, and whether their journey actually moved. Steps and calls
    -- are rebuilt only for the ones that moved.
    DECLARE @Touched TABLE
    (
        JourneyId bigint       NOT NULL PRIMARY KEY,
        Action    nvarchar(10) NOT NULL,
        Changed   bit          NOT NULL
    );

    -- Parsed once into a temp table because both the upsert and the "still seen" pass below read
    -- it. One row per session: MERGE cannot match the same target row twice, and a payload that
    -- repeated a CorrelationId would abort the whole run.
    SELECT d.*
    INTO #Src
    FROM
    (
        SELECT
            r.CorrelationId,
            NULLIF(r.UserId, N'')                           AS UserId,
            NULLIF(r.ClubId, N'')                           AS ClubId,
            r.Status                                        AS Status,
            NULLIF(r.BookingId, N'')                        AS BookingId,
            ISNULL(r.BookOk, 0)                             AS BookOk,
            ISNULL(r.BookFail, 0)                           AS BookFail,
            ISNULL(r.FailedCalls, 0)                        AS FailedCalls,
            ISNULL(r.IsBookSuccessful, 0)                   AS IsBookSuccessful,
            ISNULL(r.PassedSearch, 0)                       AS PassedSearch,
            ISNULL(r.Exceptions, ISNULL(r.FailedCalls, 0))  AS Exceptions,
            r.JourneyTotalMs,
            r.OverallP95,
            r.SlowestCallMs,
            NULLIF(r.Route, N'')                            AS Route,
            ISNULL(r.StepsCompleted, 0)                     AS StepsCompleted,
            ISNULL(r.FunnelStep, 0)                         AS FunnelStep,
            ISNULL(r.LastStep, 0)                           AS LastStep,
            r.FirstSeen,
            r.LastSeen,
            r.SourcesJson,
            r.StepsJson,
            r.JourneyJson,
            ROW_NUMBER() OVER (PARTITION BY r.CorrelationId ORDER BY r.LastSeen DESC) AS rn
        FROM OPENJSON(@RowsJson)
        WITH
        (
            CorrelationId    nvarchar(36)      N'$.CorrelationId',
            UserId           nvarchar(128)     N'$.UserId',
            ClubId           nvarchar(64)      N'$.ClubId',
            Status           nvarchar(64)      N'$.Status',
            BookingId        nvarchar(128)     N'$.BookingId',
            BookOk           int               N'$.BookOk',
            BookFail         int               N'$.BookFail',
            FailedCalls      int               N'$.FailedCalls',
            IsBookSuccessful bit               N'$.IsBookSuccessful',
            PassedSearch     bit               N'$.PassedSearch',
            Exceptions       int               N'$.Exceptions',
            JourneyTotalMs   decimal(18, 2)    N'$.JourneyTotalMs',
            OverallP95       decimal(18, 2)    N'$.OverallP95',
            SlowestCallMs    decimal(18, 2)    N'$.SlowestCallMs',
            Route            nvarchar(256)     N'$.Route',
            StepsCompleted   int               N'$.StepsCompleted',
            FunnelStep       int               N'$.FunnelStep',
            LastStep         int               N'$.LastStep',
            FirstSeen        datetimeoffset(7) N'$.FirstSeen',
            LastSeen         datetimeoffset(7) N'$.LastSeen',
            SourcesJson      nvarchar(max)     N'$.Sources' AS JSON,
            StepsJson        nvarchar(max)     N'$.Steps' AS JSON,
            JourneyJson      nvarchar(max)     N'$.Journey' AS JSON
        ) AS r
        -- An empty CorrelationId is not an identity: now that it is the upsert key, letting one
        -- through would make every anonymous journey overwrite the previous one.
        WHERE NULLIF(r.CorrelationId, N'') IS NOT NULL
    ) AS d
    WHERE d.rn = 1;

    MERGE air.Journey WITH (HOLDLOCK) AS target
    USING #Src AS src
        ON target.CorrelationId = src.CorrelationId
    -- The session's journey moved on, so the stored row is stale: rewrite it. Sessions matched but
    -- not advanced deliberately fall through to the pass below; SQL Server allows only one
    -- WHEN MATCHED ... UPDATE per MERGE.
    WHEN MATCHED AND src.LastSeen IS NOT NULL
                 AND (target.LastSeen IS NULL OR src.LastSeen > target.LastSeen) THEN
        UPDATE SET
            QueryRunId       = @QueryRunId,
            UserId           = src.UserId,
            ClubId           = src.ClubId,
            Status           = src.Status,
            BookingId        = src.BookingId,
            BookOk           = src.BookOk,
            BookFail         = src.BookFail,
            FailedCalls      = src.FailedCalls,
            IsBookSuccessful = src.IsBookSuccessful,
            PassedSearch     = src.PassedSearch,
            Exceptions       = src.Exceptions,
            JourneyTotalMs   = src.JourneyTotalMs,
            OverallP95       = src.OverallP95,
            SlowestCallMs    = src.SlowestCallMs,
            Route            = src.Route,
            StepsCompleted   = src.StepsCompleted,
            FunnelStep       = src.FunnelStep,
            LastStep         = src.LastStep,
            -- FirstSeen is when we first observed the session; the newest payload may have dropped
            -- the older calls out of its window, so never let it move forward.
            FirstSeen        = CASE
                                   WHEN target.FirstSeen IS NULL THEN src.FirstSeen
                                   WHEN src.FirstSeen IS NULL THEN target.FirstSeen
                                   WHEN src.FirstSeen < target.FirstSeen THEN src.FirstSeen
                                   ELSE target.FirstSeen
                               END,
            LastSeen         = src.LastSeen,
            SourcesJson      = src.SourcesJson,
            StepsJson        = src.StepsJson,
            JourneyJson      = src.JourneyJson
    WHEN NOT MATCHED BY TARGET THEN
        INSERT
        (
            QueryRunId, CorrelationId, UserId, ClubId, Status, BookingId,
            BookOk, BookFail, FailedCalls, IsBookSuccessful, PassedSearch, Exceptions,
            JourneyTotalMs, OverallP95, SlowestCallMs, Route,
            StepsCompleted, FunnelStep, LastStep, FirstSeen, LastSeen,
            SourcesJson, StepsJson, JourneyJson
        )
        VALUES
        (
            @QueryRunId, src.CorrelationId, src.UserId, src.ClubId, src.Status, src.BookingId,
            src.BookOk, src.BookFail, src.FailedCalls, src.IsBookSuccessful, src.PassedSearch,
            src.Exceptions, src.JourneyTotalMs, src.OverallP95, src.SlowestCallMs, src.Route,
            src.StepsCompleted, src.FunnelStep, src.LastStep, src.FirstSeen, src.LastSeen,
            src.SourcesJson, src.StepsJson, src.JourneyJson
        )
    -- Every row this MERGE emits was either new or rewritten, so all of them need their steps
    -- and calls rebuilt.
    OUTPUT inserted.JourneyId, $action, CONVERT(bit, 1)
    INTO @Touched (JourneyId, Action, Changed);

    -- Sessions the overlapping window re-reported with nothing new. Their stored row stays as is;
    -- re-stamping QueryRunId is what keeps them in the snapshot the portal reads.
    UPDATE j
    SET QueryRunId = @QueryRunId
    OUTPUT inserted.JourneyId, N'SEEN', CONVERT(bit, 0)
    INTO @Touched (JourneyId, Action, Changed)
    FROM air.Journey AS j
    INNER JOIN #Src AS s
        ON s.CorrelationId = j.CorrelationId
    WHERE NOT EXISTS (SELECT 1 FROM @Touched AS t WHERE t.JourneyId = j.JourneyId);

    -- Steps and calls are derived from JourneyJson, so they are only stale where the journey moved.
    -- Deleting the steps takes their calls with them through the cascade.
    DELETE FROM air.JourneyStep
    WHERE JourneyId IN (SELECT JourneyId FROM @Touched WHERE Changed = 1);

    INSERT INTO air.JourneyStep
    (
        JourneyId, StepNo, Step, OffsetMs, DurationMs,
        SumCallMs, SumApiMs, SumUiMs, MaxCallMs, P95Ms, WallMs, CallCount, Fails
    )
    SELECT
        j.JourneyId,
        s.StepNo,
        s.Step,
        s.OffsetMs,
        s.DurationMs,
        s.SumCallMs,
        s.SumApiMs,
        s.SumUiMs,
        s.MaxCallMs,
        s.P95Ms,
        s.WallMs,
        ISNULL(s.CallCount, 0),
        ISNULL(s.Fails, 0)
    FROM air.Journey AS j
    INNER JOIN @Touched AS t
        ON t.JourneyId = j.JourneyId
       AND t.Changed = 1
    CROSS APPLY OPENJSON(j.JourneyJson)
    WITH
    (
        StepNo     int            N'$.stepNo',
        Step       nvarchar(128)  N'$.step',
        OffsetMs   decimal(18, 2) N'$.offsetMs',
        DurationMs decimal(18, 2) N'$.durationMs',
        SumCallMs  decimal(18, 2) N'$.sumCallMs',
        SumApiMs   decimal(18, 2) N'$.sumApiMs',
        SumUiMs    decimal(18, 2) N'$.sumUiMs',
        MaxCallMs  decimal(18, 2) N'$.maxCallMs',
        P95Ms      decimal(18, 2) N'$.p95Ms',
        WallMs     decimal(18, 2) N'$.wallMs',
        CallCount  int            N'$.callCount',
        Fails      int            N'$.fails',
        CallsJson  nvarchar(max)  N'$.calls' AS JSON
    ) AS s
    WHERE s.StepNo IS NOT NULL;

    INSERT INTO air.JourneyCall
    (
        JourneyStepId, Name, Kind, Source, DurationMs, DurationSource,
        MaxMs, P50Ms, P90Ms, P95Ms, P99Ms, GapMs, OffsetMs,
        Failed, Status, Target, Path
    )
    SELECT
        st.JourneyStepId,
        c.Name,
        c.Kind,
        c.Source,
        c.DurationMs,
        c.DurationSource,
        c.MaxMs,
        c.P50Ms,
        c.P90Ms,
        c.P95Ms,
        c.P99Ms,
        c.GapMs,
        c.OffsetMs,
        ISNULL(c.Failed, 0),
        c.Status,
        c.Target,
        c.Path
    FROM air.Journey AS j
    INNER JOIN @Touched AS t
        ON t.JourneyId = j.JourneyId
       AND t.Changed = 1
    INNER JOIN air.JourneyStep AS st
        ON st.JourneyId = j.JourneyId
    CROSS APPLY OPENJSON(j.JourneyJson)
    WITH
    (
        StepNo    int           N'$.stepNo',
        CallsJson nvarchar(max) N'$.calls' AS JSON
    ) AS s
    CROSS APPLY OPENJSON(s.CallsJson)
    WITH
    (
        Name           nvarchar(1024) N'$.name',
        Kind           nvarchar(32)   N'$.kind',
        Source         nvarchar(32)   N'$.source',
        DurationMs     decimal(18, 2) N'$.durationMs',
        DurationSource nvarchar(128)  N'$.durationSource',
        MaxMs          decimal(18, 2) N'$.maxMs',
        P50Ms          decimal(18, 2) N'$.p50Ms',
        P90Ms          decimal(18, 2) N'$.p90Ms',
        P95Ms          decimal(18, 2) N'$.p95Ms',
        P99Ms          decimal(18, 2) N'$.p99Ms',
        GapMs          decimal(18, 2) N'$.gapMs',
        OffsetMs       decimal(18, 2) N'$.offsetMs',
        Failed         bit            N'$.failed',
        Status         nvarchar(64)   N'$.status',
        Target         nvarchar(512)  N'$.target',
        Path           nvarchar(1024) N'$.path'
    ) AS c
    WHERE s.StepNo = st.StepNo
      AND c.Name IS NOT NULL;

    -- Counted over every session this run saw, not just the ones it rewrote: that whole set is
    -- what the snapshot exposes.
    DECLARE @JourneyCount int = (SELECT COUNT(*) FROM @Touched);
    DECLARE @StepCount int =
        (SELECT COUNT(*) FROM air.JourneyStep AS st
         INNER JOIN @Touched AS t ON t.JourneyId = st.JourneyId);
    DECLARE @CallCount int =
        (SELECT COUNT(*) FROM air.JourneyCall AS c
         INNER JOIN air.JourneyStep AS st ON st.JourneyStepId = c.JourneyStepId
         INNER JOIN @Touched AS t ON t.JourneyId = st.JourneyId);

    SET @RecordCount = @JourneyCount;
    SET @InsertedCount  = (SELECT COUNT(*) FROM @Touched WHERE Action = N'INSERT');
    SET @UpdatedCount   = (SELECT COUNT(*) FROM @Touched WHERE Action = N'UPDATE');
    SET @UnchangedCount = (SELECT COUNT(*) FROM @Touched WHERE Action = N'SEEN');

    UPDATE air.QueryRun
    SET [RowCount] = @JourneyCount
    WHERE QueryRunId = @QueryRunId;

    MERGE air.CurrentSnapshot AS target
    USING (SELECT N'air' AS Domain, @ClubId AS ClubId) AS src
        ON target.Domain = src.Domain
       AND target.ClubId = src.ClubId
    WHEN MATCHED THEN
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
        VALUES (N'air', @QueryRunId, @Now, @Window, @Environment, @ClubId, @JourneyCount, @StepCount, @CallCount);

    COMMIT TRANSACTION;
END
GO

-- Read path: @RecordCount is how many Journey rows the following SELECT must return.
-- Filter by QueryRunId, which selects the sessions the newest run saw; the table itself holds
-- every session ever ingested. Do not SELECT TOP (@RecordCount) from the whole Journey table.
CREATE OR ALTER PROCEDURE air.usp_GetLatestJourneys
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

    IF @RequestedClubId IS NULL
    BEGIN
        SELECT TOP (1)
            @QueryRunId  = QueryRunId,
            @FetchedAt   = FetchedAt,
            @Window      = Window,
            @Environment = Environment,
            @ClubId      = ClubId
        FROM air.CurrentSnapshot
        WHERE Domain = N'air'
        ORDER BY FetchedAt DESC;

        SELECT @RecordCount = COALESCE(SUM(RecordCount), 0)
        FROM air.CurrentSnapshot
        WHERE Domain = N'air';
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
        FROM air.CurrentSnapshot
        WHERE Domain = N'air'
          AND ClubId = @RequestedClubId;
    END

SELECT @Host = Host
FROM air.QueryRun
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
        UserId,
        ClubId,
        Status,
        BookingId,
        BookOk,
        BookFail,
        FailedCalls,
        IsBookSuccessful,
        PassedSearch,
        Exceptions,
        JourneyTotalMs,
        OverallP95,
        SlowestCallMs,
        Route,
        StepsCompleted,
        FunnelStep,
        LastStep,
        FirstSeen,
        LastSeen,
        SourcesJson,
        StepsJson,
        JourneyJson
    FROM air.Journey
    WHERE (
            (
                @Since IS NOT NULL
                AND (LastSeen IS NULL OR LastSeen >= @Since)
                AND QueryRunId IN (
                    SELECT QueryRunId
                    FROM air.QueryRun
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
                        AND QueryRunId IN (SELECT QueryRunId FROM air.CurrentSnapshot WHERE Domain = N'air')
                    )
                )
            )
          )
      AND (
          @SearchId IS NULL
          OR UserId LIKE N'%' + @SearchId + N'%'
          OR CorrelationId LIKE N'%' + @SearchId + N'%'
          OR BookingId LIKE N'%' + @SearchId + N'%'
      )
      -- @Status is reserved until UI health states are mapped to domain outcomes.
    ORDER BY LastSeen DESC;
END
GO
