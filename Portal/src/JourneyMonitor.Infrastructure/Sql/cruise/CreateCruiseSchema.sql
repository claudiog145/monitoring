-- Cruise journey store for KQL rows from TMobile_CRUISE_Query_KQL_v2.txt.
-- Grain: one cruise.Journey row per frontend session (CorrelationId = SessionId), carrying that
-- session's current state.
-- Nested Journey[] / calls[] are shredded into cruise.JourneyStep and cruise.JourneyCall.
-- JourneyJson keeps the original Kusto bag so the portal can still round-trip raw rows.
--
-- The KQL job may run many times a day and its window overlaps, so consecutive runs re-report
-- the same sessions. Ingestion upserts on CorrelationId: a session seen again only has its row
-- rewritten when LastSeen advanced, otherwise the run just re-stamps QueryRunId to record
-- "still seen". cruise.Journey.QueryRunId therefore means "the last run that saw this session",
-- which is what makes the read below return exactly the sessions present in the newest run.
--
-- Do not load Hotel v6d or Air KQL rows into these tables. Cruise Outcome names the failed
-- stage; Air uses Status / FunnelStep; Hotel v6d groups by UserId.
--
-- Auth when calling from Azure: Managed Identity + Azure SQL (not SQL passwords in source).
-- Connection strings belong in Key Vault / Function app settings.

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'cruise')
    EXEC(N'CREATE SCHEMA cruise');
GO

IF OBJECT_ID(N'cruise.QueryRun', N'U') IS NULL
BEGIN
    CREATE TABLE cruise.QueryRun
    (
        QueryRunId   uniqueidentifier NOT NULL
            CONSTRAINT PK_cruise_QueryRun PRIMARY KEY
            CONSTRAINT DF_cruise_QueryRun_Id DEFAULT NEWSEQUENTIALID(),
        Domain       nvarchar(32)     NOT NULL
            CONSTRAINT DF_cruise_QueryRun_Domain DEFAULT N'cruise',
        Window       nvarchar(16)     NOT NULL,
        Environment  nvarchar(32)     NULL,
        ClubId       nvarchar(64)     NULL,
        Host         nvarchar(256)    NULL,
        FetchedAt    datetimeoffset(7) NOT NULL
            CONSTRAINT DF_cruise_QueryRun_FetchedAt DEFAULT SYSDATETIMEOFFSET(),
        [RowCount]   int              NOT NULL
            CONSTRAINT DF_cruise_QueryRun_RowCount DEFAULT 0,
        Source       nvarchar(32)     NOT NULL
            CONSTRAINT DF_cruise_QueryRun_Source DEFAULT N'live',
        KqlFile      nvarchar(256)    NULL
            CONSTRAINT DF_cruise_QueryRun_KqlFile DEFAULT N'TMobile_CRUISE_Query_KQL_v2.txt',
        CONSTRAINT CK_cruise_QueryRun_Domain CHECK (Domain = N'cruise')
    );

    CREATE INDEX IX_cruise_QueryRun_FetchedAt
        ON cruise.QueryRun (FetchedAt DESC);
END
GO

-- One row per club. RecordCount is the fetch size for cruise.Journey rows whose
-- QueryRunId is that club's CurrentSnapshot.QueryRunId.
IF OBJECT_ID(N'cruise.CurrentSnapshot', N'U') IS NULL
BEGIN
    CREATE TABLE cruise.CurrentSnapshot
    (
        Domain       nvarchar(32)      NOT NULL
            CONSTRAINT DF_cruise_CurrentSnapshot_Domain DEFAULT N'cruise',
        QueryRunId   uniqueidentifier  NOT NULL
            CONSTRAINT FK_cruise_CurrentSnapshot_QueryRun
                REFERENCES cruise.QueryRun (QueryRunId),
        FetchedAt    datetimeoffset(7) NOT NULL,
        Window       nvarchar(16)      NOT NULL,
        Environment  nvarchar(32)      NULL,
        ClubId       nvarchar(64)      NOT NULL,
        RecordCount  int               NOT NULL
            CONSTRAINT DF_cruise_CurrentSnapshot_RecordCount DEFAULT 0,
        StepCount    int               NOT NULL
            CONSTRAINT DF_cruise_CurrentSnapshot_StepCount DEFAULT 0,
        CallCount    int               NOT NULL
            CONSTRAINT DF_cruise_CurrentSnapshot_CallCount DEFAULT 0,
        CONSTRAINT PK_cruise_CurrentSnapshot PRIMARY KEY (Domain, ClubId),
        CONSTRAINT CK_cruise_CurrentSnapshot_Domain CHECK (Domain = N'cruise'),
        CONSTRAINT CK_cruise_CurrentSnapshot_RecordCount CHECK (RecordCount >= 0)
    );
END
GO

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
IF OBJECT_ID(N'cruise.CurrentSnapshot', N'U') IS NOT NULL
   AND NOT EXISTS (
        SELECT 1
        FROM sys.indexes AS i
        INNER JOIN sys.index_columns AS ic
            ON ic.object_id = i.object_id AND ic.index_id = i.index_id
        INNER JOIN sys.columns AS c
            ON c.object_id = ic.object_id AND c.column_id = ic.column_id
        WHERE i.object_id = OBJECT_ID(N'cruise.CurrentSnapshot')
          AND i.is_primary_key = 1
          AND c.name = N'ClubId'
   )
BEGIN
    DECLARE @pk sysname;
    SELECT @pk = name
    FROM sys.key_constraints
    WHERE parent_object_id = OBJECT_ID(N'cruise.CurrentSnapshot')
      AND [type] = N'PK';

    IF @pk IS NOT NULL
    BEGIN
        DECLARE @drop nvarchar(400) = N'ALTER TABLE cruise.CurrentSnapshot DROP CONSTRAINT ' + QUOTENAME(@pk);
        EXEC(@drop);
    END

    DELETE FROM cruise.CurrentSnapshot WHERE ClubId IS NULL;

    ALTER TABLE cruise.CurrentSnapshot ALTER COLUMN ClubId nvarchar(64) NOT NULL;
    ALTER TABLE cruise.CurrentSnapshot
        ADD CONSTRAINT PK_cruise_CurrentSnapshot PRIMARY KEY (Domain, ClubId);
END
GO

IF OBJECT_ID(N'cruise.Journey', N'U') IS NULL
BEGIN
    CREATE TABLE cruise.Journey
    (
        JourneyId            bigint            NOT NULL IDENTITY(1, 1)
            CONSTRAINT PK_cruise_Journey PRIMARY KEY,
        QueryRunId           uniqueidentifier  NOT NULL
            CONSTRAINT FK_cruise_Journey_QueryRun
                REFERENCES cruise.QueryRun (QueryRunId) ON DELETE CASCADE,
        -- ICE partner id of the ingest that last wrote this session. Cruise telemetry is scoped
        -- by partner id, and the timer passes that value as @ClubId. NOT NULL so a MERGE cannot leave it blank.
        ClubId               nvarchar(64)      NOT NULL,
        CorrelationId        nvarchar(128)     NOT NULL,
        SessionId            nvarchar(128)     NOT NULL,
        UserId               nvarchar(128)     NULL,
        SessionCount         int               NOT NULL
            CONSTRAINT DF_cruise_Journey_SessionCount DEFAULT 1,
        Outcome              nvarchar(128)     NOT NULL,
        LastStep             int               NOT NULL
            CONSTRAINT DF_cruise_Journey_LastStep DEFAULT 0,
        FailReason           nvarchar(1024)    NULL,
        BookOk               int               NOT NULL
            CONSTRAINT DF_cruise_Journey_BookOk DEFAULT 0,
        BookFail             int               NOT NULL
            CONSTRAINT DF_cruise_Journey_BookFail DEFAULT 0,
        IsBookSuccessful     bit               NOT NULL
            CONSTRAINT DF_cruise_Journey_IsBookSuccessful DEFAULT 0,
        PassedSearch         bit               NOT NULL
            CONSTRAINT DF_cruise_Journey_PassedSearch DEFAULT 0,
        DistinctOkRes        int               NOT NULL
            CONSTRAINT DF_cruise_Journey_DistinctOkRes DEFAULT 0,
        BookedReservations   nvarchar(max)     NULL,
        FailedCalls          int               NOT NULL
            CONSTRAINT DF_cruise_Journey_FailedCalls DEFAULT 0,
        Exceptions           int               NOT NULL
            CONSTRAINT DF_cruise_Journey_Exceptions DEFAULT 0,
        JourneyTotalMs       decimal(18, 2)    NULL,
        SlowestCallMs        decimal(18, 2)    NULL,
        OverallP95           decimal(18, 2)    NULL,
        SearchToConfirmSeconds int             NULL,
        FirstSeen            datetimeoffset(7) NULL,
        LastSeen             datetimeoffset(7) NULL,
        StepsJson            nvarchar(max)     NULL,
        JourneyJson          nvarchar(max)     NULL,
        CONSTRAINT UQ_cruise_Journey_Run_Correlation UNIQUE (QueryRunId, CorrelationId),
        CONSTRAINT CK_cruise_Journey_BookedReservations CHECK (BookedReservations IS NULL OR ISJSON(BookedReservations) = 1),
        CONSTRAINT CK_cruise_Journey_StepsJson   CHECK (StepsJson   IS NULL OR ISJSON(StepsJson)   = 1),
        CONSTRAINT CK_cruise_Journey_JourneyJson CHECK (JourneyJson IS NULL OR ISJSON(JourneyJson) = 1)
    );

    CREATE INDEX IX_cruise_Journey_LastSeen
        ON cruise.Journey (LastSeen DESC)
        INCLUDE (CorrelationId, Outcome, UserId, SessionId);

    CREATE INDEX IX_cruise_Journey_UserId_LastSeen
        ON cruise.Journey (UserId, LastSeen DESC);

    CREATE INDEX IX_cruise_Journey_Outcome_LastSeen
        ON cruise.Journey (Outcome, LastSeen DESC);
END
GO

-- Databases written by the previous insert-per-run ingestion hold one row per session per run.
-- Collapse them to the newest row per session so the unique index below can exist. Steps and
-- calls of the discarded rows go with them through their cascading foreign keys.
IF OBJECT_ID(N'cruise.Journey', N'U') IS NOT NULL
   AND EXISTS (SELECT 1 FROM cruise.Journey GROUP BY CorrelationId HAVING COUNT(*) > 1)
BEGIN
    WITH ranked AS
    (
        SELECT
            JourneyId,
            ROW_NUMBER() OVER (PARTITION BY CorrelationId ORDER BY LastSeen DESC, JourneyId DESC) AS rn
        FROM cruise.Journey
    )
    DELETE FROM cruise.Journey
    WHERE JourneyId IN (SELECT JourneyId FROM ranked WHERE rn > 1);
END
GO

-- The grain is the frontend session, so CorrelationId is the identity ingestion upserts on.
IF OBJECT_ID(N'cruise.Journey', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE name = N'UQ_cruise_Journey_CorrelationId'
                     AND object_id = OBJECT_ID(N'cruise.Journey'))
    CREATE UNIQUE INDEX UQ_cruise_Journey_CorrelationId ON cruise.Journey (CorrelationId);
GO

-- Databases created before ClubId existed on the journey row. For cruises that value is the
-- ICE partner id the timer already passes as @ClubId. Backfill from the query run, then require
-- a value so the MERGE below cannot write NULL.
-- The backfill runs through EXEC on purpose: column names are resolved when the batch is
-- compiled, so a literal UPDATE on the column the line above adds fails the whole batch with
-- "Invalid column name 'ClubId'" and the ALTER never runs.
IF OBJECT_ID(N'cruise.Journey', N'U') IS NOT NULL
   AND COL_LENGTH(N'cruise.Journey', N'ClubId') IS NULL
BEGIN
    ALTER TABLE cruise.Journey ADD ClubId nvarchar(64) NULL;

    EXEC(N'
        UPDATE j
        SET ClubId = r.ClubId
        FROM cruise.Journey AS j
        INNER JOIN cruise.QueryRun AS r
            ON r.QueryRunId = j.QueryRunId
        WHERE j.ClubId IS NULL
          AND NULLIF(r.ClubId, N'''') IS NOT NULL;');
END
GO

IF OBJECT_ID(N'cruise.Journey', N'U') IS NOT NULL
   AND COL_LENGTH(N'cruise.Journey', N'ClubId') IS NOT NULL
   AND EXISTS (
        SELECT 1
        FROM sys.columns
        WHERE object_id = OBJECT_ID(N'cruise.Journey')
          AND name = N'ClubId'
          AND is_nullable = 1
   )
   AND NOT EXISTS (SELECT 1 FROM cruise.Journey WHERE ClubId IS NULL)
BEGIN
    ALTER TABLE cruise.Journey ALTER COLUMN ClubId nvarchar(64) NOT NULL;
END
GO

-- QueryRunId now points at the last run that saw the session, not at the run that created the row,
-- so cascading from QueryRun would delete sessions that are still current the moment old runs are
-- purged. Fail loudly on such a delete instead.
IF EXISTS (SELECT 1 FROM sys.foreign_keys
           WHERE name = N'FK_cruise_Journey_QueryRun' AND delete_referential_action = 1)
BEGIN
    ALTER TABLE cruise.Journey DROP CONSTRAINT FK_cruise_Journey_QueryRun;
    ALTER TABLE cruise.Journey ADD CONSTRAINT FK_cruise_Journey_QueryRun
        FOREIGN KEY (QueryRunId) REFERENCES cruise.QueryRun (QueryRunId);
END
GO

IF OBJECT_ID(N'cruise.JourneyStep', N'U') IS NULL
BEGIN
    CREATE TABLE cruise.JourneyStep
    (
        JourneyStepId    bigint            NOT NULL IDENTITY(1, 1)
            CONSTRAINT PK_cruise_JourneyStep PRIMARY KEY,
        JourneyId        bigint            NOT NULL
            CONSTRAINT FK_cruise_JourneyStep_Journey
                REFERENCES cruise.Journey (JourneyId) ON DELETE CASCADE,
        StageNo          int               NOT NULL,
        Stage            nvarchar(128)     NOT NULL,
        StepStart        datetimeoffset(7) NULL,
        CallCount        int               NOT NULL
            CONSTRAINT DF_cruise_JourneyStep_CallCount DEFAULT 0,
        StageTotalMs     decimal(18, 2)    NULL,
        P50Ms            decimal(18, 2)    NULL,
        P90Ms            decimal(18, 2)    NULL,
        P95Ms            decimal(18, 2)    NULL,
        P99Ms            decimal(18, 2)    NULL,
        MaxMs            decimal(18, 2)    NULL,
        FailureRatePct   decimal(9, 2)     NULL,
        Status           nvarchar(16)      NULL,
        CONSTRAINT UQ_cruise_JourneyStep UNIQUE (JourneyId, StageNo)
    );

    CREATE INDEX IX_cruise_JourneyStep_StageNo
        ON cruise.JourneyStep (StageNo)
        INCLUDE (Stage, Status, StageTotalMs);
END
GO

IF OBJECT_ID(N'cruise.JourneyCall', N'U') IS NULL
BEGIN
    CREATE TABLE cruise.JourneyCall
    (
        JourneyCallId    bigint         NOT NULL IDENTITY(1, 1)
            CONSTRAINT PK_cruise_JourneyCall PRIMARY KEY,
        JourneyStepId    bigint         NOT NULL
            CONSTRAINT FK_cruise_JourneyCall_JourneyStep
                REFERENCES cruise.JourneyStep (JourneyStepId) ON DELETE CASCADE,
        Kind             nvarchar(32)   NULL,
        Source           nvarchar(64)   NULL,
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
        CONSTRAINT CK_cruise_JourneyCall_ErrorsJson CHECK (ErrorsJson IS NULL OR ISJSON(ErrorsJson) = 1)
    );

CREATE INDEX IX_cruise_JourneyCall_Kind_Source
        ON cruise.JourneyCall (Kind, Source);

    CREATE INDEX IX_cruise_JourneyCall_JourneyStepId
        ON cruise.JourneyCall (JourneyStepId);

    CREATE INDEX IX_cruise_JourneyCall_Failed
        ON cruise.JourneyCall (FailureRatePct)
        WHERE FailureRatePct > 0;
END
GO

CREATE OR ALTER VIEW cruise.v_LatestQueryRun
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
    FROM cruise.CurrentSnapshot AS s
    INNER JOIN cruise.QueryRun AS r
        ON r.QueryRunId = s.QueryRunId;
GO

CREATE OR ALTER VIEW cruise.v_LatestJourney
AS
    SELECT j.*
    FROM cruise.Journey AS j
    INNER JOIN cruise.CurrentSnapshot AS s
        ON s.QueryRunId = j.QueryRunId;
GO

-- Ingest named-property JSON: an array of Cruise KQL rows (the portal /api/journeys rows shape),
-- not the Application Insights tables[].rows[] matrix. Parameterize from the app; do not concatenate.
CREATE OR ALTER PROCEDURE cruise.usp_IngestKqlRows
    @Window       nvarchar(16),
    @Environment  nvarchar(32)     = NULL,
    @ClubId       nvarchar(64)     = NULL,
    @Host         nvarchar(256)    = NULL,
    @Source       nvarchar(32)     = N'live',
    @KqlFile      nvarchar(256)    = N'TMobile_CRUISE_Query_KQL_v2.txt',
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
        THROW 50001, N'@RowsJson must be a JSON array of Cruise KQL rows.', 1;
    END

    IF NULLIF(@ClubId, N'') IS NULL
        THROW 50002, N'@ClubId is required. Each club keeps its own current snapshot.', 1;

    IF @QueryRunId IS NULL
        SET @QueryRunId = NEWID();

    DECLARE @Now datetimeoffset(7) = SYSDATETIMEOFFSET();

    BEGIN TRANSACTION;

    INSERT INTO cruise.QueryRun
        (QueryRunId, Domain, Window, Environment, ClubId, Host, FetchedAt, [RowCount], Source, KqlFile)
    VALUES
        (@QueryRunId, N'cruise', @Window, @Environment, @ClubId, @Host, @Now, 0, @Source, @KqlFile);

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
    -- repeated a CorrelationId would abort the whole run. The partition uses the same fallback
    -- the key column is built from, because a row carrying only SessionId still resolves to the
    -- CorrelationId of an existing journey.
    SELECT d.*
    INTO #Src
    FROM
    (
        SELECT
            COALESCE(NULLIF(r.CorrelationId, N''), NULLIF(r.SessionId, N''))  AS CorrelationId,
            COALESCE(NULLIF(r.SessionId, N''), NULLIF(r.CorrelationId, N''))  AS SessionId,
            NULLIF(r.UserId, N'')                           AS UserId,
            ISNULL(r.SessionCount, 1)                       AS SessionCount,
            r.Outcome                                       AS Outcome,
            ISNULL(r.LastStep, 0)                           AS LastStep,
            NULLIF(r.FailReason, N'')                       AS FailReason,
            ISNULL(r.BookOk, 0)                             AS BookOk,
            ISNULL(r.BookFail, 0)                           AS BookFail,
            ISNULL(r.IsBookSuccessful, 0)                   AS IsBookSuccessful,
            ISNULL(r.PassedSearch, 0)                       AS PassedSearch,
            ISNULL(r.DistinctOkRes, 0)                      AS DistinctOkRes,
            r.BookedReservations                            AS BookedReservations,
            ISNULL(r.FailedCalls, 0)                        AS FailedCalls,
            ISNULL(r.Exceptions, ISNULL(r.FailedCalls, 0))  AS Exceptions,
            r.JourneyTotalMs,
            r.SlowestCallMs,
            r.OverallP95,
            r.SearchToConfirmSeconds,
            r.FirstSeen,
            r.LastSeen,
            r.StepsJson,
            r.JourneyJson,
            ROW_NUMBER() OVER (PARTITION BY COALESCE(NULLIF(r.CorrelationId, N''), NULLIF(r.SessionId, N''))
                               ORDER BY r.LastSeen DESC) AS rn
        FROM OPENJSON(@RowsJson)
        WITH
        (
            CorrelationId            nvarchar(128)     N'$.CorrelationId',
            SessionId                nvarchar(128)     N'$.SessionId',
            UserId                   nvarchar(128)     N'$.UserId',
            SessionCount             int               N'$.SessionCount',
            Outcome                  nvarchar(128)     N'$.Outcome',
            LastStep                 int               N'$.LastStep',
            FailReason               nvarchar(1024)    N'$.FailReason',
            BookOk                   int               N'$.BookOk',
            BookFail                 int               N'$.BookFail',
            IsBookSuccessful         bit               N'$.IsBookSuccessful',
            PassedSearch             bit               N'$.PassedSearch',
            DistinctOkRes            int               N'$.DistinctOkRes',
            BookedReservations       nvarchar(max)     N'$.BookedReservations' AS JSON,
            FailedCalls              int               N'$.FailedCalls',
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
        WHERE COALESCE(NULLIF(r.CorrelationId, N''), NULLIF(r.SessionId, N'')) IS NOT NULL
          AND r.Outcome IS NOT NULL
    ) AS d
    WHERE d.rn = 1;

    MERGE cruise.Journey WITH (HOLDLOCK) AS target
    USING #Src AS src
        ON target.CorrelationId = src.CorrelationId
    -- The session's journey moved on, so the stored row is stale: rewrite it. Sessions matched but
    -- not advanced deliberately fall through to the pass below; SQL Server allows only one
    -- WHEN MATCHED ... UPDATE per MERGE.
    WHEN MATCHED AND src.LastSeen IS NOT NULL
                 AND (target.LastSeen IS NULL OR src.LastSeen > target.LastSeen) THEN
        UPDATE SET
            QueryRunId             = @QueryRunId,
            ClubId                 = @ClubId,
            SessionId              = src.SessionId,
            UserId                 = src.UserId,
            SessionCount           = src.SessionCount,
            Outcome                = src.Outcome,
            LastStep               = src.LastStep,
            FailReason             = src.FailReason,
            BookOk                 = src.BookOk,
            BookFail               = src.BookFail,
            IsBookSuccessful       = src.IsBookSuccessful,
            PassedSearch           = src.PassedSearch,
            DistinctOkRes          = src.DistinctOkRes,
            BookedReservations     = src.BookedReservations,
            FailedCalls            = src.FailedCalls,
            Exceptions             = src.Exceptions,
            JourneyTotalMs         = src.JourneyTotalMs,
            SlowestCallMs          = src.SlowestCallMs,
            OverallP95             = src.OverallP95,
            SearchToConfirmSeconds = src.SearchToConfirmSeconds,
            -- FirstSeen is when we first observed the session; the newest payload may have dropped
            -- the older sessions out of its window, so never let it move forward.
            FirstSeen              = CASE
                                         WHEN target.FirstSeen IS NULL THEN src.FirstSeen
                                         WHEN src.FirstSeen IS NULL THEN target.FirstSeen
                                         WHEN src.FirstSeen < target.FirstSeen THEN src.FirstSeen
                                         ELSE target.FirstSeen
                                     END,
            LastSeen               = src.LastSeen,
            StepsJson              = src.StepsJson,
            JourneyJson            = src.JourneyJson
    WHEN NOT MATCHED BY TARGET THEN
        INSERT
        (
            QueryRunId, ClubId, CorrelationId, SessionId, UserId, SessionCount, Outcome, LastStep, FailReason,
            BookOk, BookFail, IsBookSuccessful, PassedSearch, DistinctOkRes, BookedReservations,
            FailedCalls, Exceptions, JourneyTotalMs, SlowestCallMs, OverallP95,
            SearchToConfirmSeconds, FirstSeen, LastSeen, StepsJson, JourneyJson
        )
        VALUES
        (
            @QueryRunId, @ClubId, src.CorrelationId, src.SessionId, src.UserId, src.SessionCount, src.Outcome,
            src.LastStep, src.FailReason, src.BookOk, src.BookFail, src.IsBookSuccessful,
            src.PassedSearch, src.DistinctOkRes, src.BookedReservations, src.FailedCalls,
            src.Exceptions, src.JourneyTotalMs, src.SlowestCallMs, src.OverallP95,
            src.SearchToConfirmSeconds, src.FirstSeen, src.LastSeen, src.StepsJson, src.JourneyJson
        )
    -- Every row this MERGE emits was either new or rewritten, so all of them need their steps
    -- and calls rebuilt.
    OUTPUT inserted.JourneyId, $action, CONVERT(bit, 1)
    INTO @Touched (JourneyId, Action, Changed);

    -- Sessions the overlapping window re-reported with nothing new. Their stored row stays as is;
    -- re-stamping QueryRunId is what keeps them in the snapshot the portal reads.
    UPDATE j
    SET QueryRunId = @QueryRunId,
        ClubId     = @ClubId
    OUTPUT inserted.JourneyId, N'SEEN', CONVERT(bit, 0)
    INTO @Touched (JourneyId, Action, Changed)
    FROM cruise.Journey AS j
    INNER JOIN #Src AS s
        ON s.CorrelationId = j.CorrelationId
    WHERE NOT EXISTS (SELECT 1 FROM @Touched AS t WHERE t.JourneyId = j.JourneyId);

    -- Steps and calls are derived from JourneyJson, so they are only stale where the journey moved.
    -- Deleting the steps takes their calls with them through the cascade.
    DELETE FROM cruise.JourneyStep
    WHERE JourneyId IN (SELECT JourneyId FROM @Touched WHERE Changed = 1);

    INSERT INTO cruise.JourneyStep
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
    FROM cruise.Journey AS j
    INNER JOIN @Touched AS t
        ON t.JourneyId = j.JourneyId
       AND t.Changed = 1
    CROSS APPLY OPENJSON(j.JourneyJson)
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

    INSERT INTO cruise.JourneyCall
    (
        JourneyStepId, Kind, Source, CallName, CorrelationId, N,
        CallTotalMs, P50Ms, P90Ms, P95Ms, P99Ms, MaxMs,
        FailureRatePct, Status, Detail, ErrorsJson
    )
    SELECT
        st.JourneyStepId,
        c.Kind,
        c.Source,
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
    FROM cruise.Journey AS j
    INNER JOIN @Touched AS t
        ON t.JourneyId = j.JourneyId
       AND t.Changed = 1
    INNER JOIN cruise.JourneyStep AS st
        ON st.JourneyId = j.JourneyId
    CROSS APPLY OPENJSON(j.JourneyJson)
    WITH
    (
        StageNo   int           N'$.stageNo',
        CallsJson nvarchar(max) N'$.calls' AS JSON
    ) AS s
    CROSS APPLY OPENJSON(s.CallsJson)
    WITH
    (
        Kind           nvarchar(32)   N'$.kind',
        Source         nvarchar(64)   N'$.source',
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

    -- Counted over every session this run saw, not just the ones it rewrote: that whole set is
    -- what the snapshot exposes.
    DECLARE @JourneyCount int = (SELECT COUNT(*) FROM @Touched);
    DECLARE @StepCount int =
        (SELECT COUNT(*) FROM cruise.JourneyStep AS st
         INNER JOIN @Touched AS t ON t.JourneyId = st.JourneyId);
    DECLARE @CallCount int =
        (SELECT COUNT(*) FROM cruise.JourneyCall AS c
         INNER JOIN cruise.JourneyStep AS st ON st.JourneyStepId = c.JourneyStepId
         INNER JOIN @Touched AS t ON t.JourneyId = st.JourneyId);

    SET @RecordCount = @JourneyCount;
    SET @InsertedCount  = (SELECT COUNT(*) FROM @Touched WHERE Action = N'INSERT');
    SET @UpdatedCount   = (SELECT COUNT(*) FROM @Touched WHERE Action = N'UPDATE');
    SET @UnchangedCount = (SELECT COUNT(*) FROM @Touched WHERE Action = N'SEEN');

    UPDATE cruise.QueryRun
    SET [RowCount] = @JourneyCount
    WHERE QueryRunId = @QueryRunId;

    MERGE cruise.CurrentSnapshot AS target
    USING (SELECT N'cruise' AS Domain, @ClubId AS ClubId) AS src
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
        VALUES (N'cruise', @QueryRunId, @Now, @Window, @Environment, @ClubId, @JourneyCount, @StepCount, @CallCount);

    COMMIT TRANSACTION;
END
GO

-- Read path: @RecordCount is how many Journey rows the following SELECT must return.
-- Filter by QueryRunId, which selects the sessions the newest run saw; the table itself holds
-- every session ever ingested. Do not SELECT TOP (@RecordCount) from the whole Journey table.
CREATE OR ALTER PROCEDURE cruise.usp_GetLatestJourneys
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
        FROM cruise.CurrentSnapshot
        WHERE Domain = N'cruise'
        ORDER BY FetchedAt DESC;

        SELECT @RecordCount = COALESCE(SUM(RecordCount), 0)
        FROM cruise.CurrentSnapshot
        WHERE Domain = N'cruise';
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
        FROM cruise.CurrentSnapshot
        WHERE Domain = N'cruise'
          AND ClubId = @RequestedClubId;
    END

SELECT @Host = Host
FROM cruise.QueryRun
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
        SessionId,
        UserId,
        -- ICE partner id stored at ingest. The portal labels each row from this, not from the response header.
        ClubId,
        SessionCount,
        Outcome,
        LastStep,
        FailReason,
        BookOk,
        BookFail,
        IsBookSuccessful,
        PassedSearch,
        DistinctOkRes,
        BookedReservations,
        FailedCalls,
        Exceptions,
        JourneyTotalMs,
        SlowestCallMs,
        OverallP95,
        SearchToConfirmSeconds,
        FirstSeen,
        LastSeen,
        StepsJson,
        JourneyJson
    FROM cruise.Journey
    WHERE (
            (
                @Since IS NOT NULL
                AND (LastSeen IS NULL OR LastSeen >= @Since)
                AND QueryRunId IN (
                    SELECT QueryRunId
                    FROM cruise.QueryRun
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
                        AND QueryRunId IN (SELECT QueryRunId FROM cruise.CurrentSnapshot WHERE Domain = N'cruise')
                    )
                )
            )
          )
      AND (
          @SearchId IS NULL
          OR UserId LIKE N'%' + @SearchId + N'%'
          OR SessionId LIKE N'%' + @SearchId + N'%'
          OR CorrelationId LIKE N'%' + @SearchId + N'%'
          OR BookedReservations LIKE N'%' + @SearchId + N'%'
      )
      -- @Status is reserved until UI health states are mapped to domain outcomes.
    ORDER BY LastSeen DESC;
END
GO
