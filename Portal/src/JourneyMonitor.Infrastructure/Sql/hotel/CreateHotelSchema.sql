-- Hotel journey store for KQL rows from TMobile_HOTEL_Query_KQL_v6d_demo_2h.txt.
-- The query returns one row per inactivity session. CorrelationId is the session key
-- (raw correlation id + "-" + sequence). Steps with no correlation id inherit the id on the
-- same operation_Id, or attach to a nearby session of the same member. They are not stored
-- under a synthetic UserId-nocorr key.
-- HotelVisitMerger then folds a session into the next one when the same member continues
-- the funnel within 2 minutes and the earlier session did not book. The stored CorrelationId
-- is the session that reached the furthest stage. AbsorbedCorrelationIds on that row names
-- the fragment keys a previous ingest must drop.
-- UserId is the member and is not unique. A later booking is a different row and does not
-- rewrite an earlier abandoned session.
-- Nested Journey[] / calls[] are shredded into hotel.JourneyStep and hotel.JourneyCall.
-- JourneyJson keeps the original Kusto bag so the portal can still round-trip raw rows.
--
-- The KQL job may run many times a day and its window overlaps, so consecutive runs re-report
-- the same sessions. Ingestion upserts on CorrelationId: a session seen again only has its row
-- rewritten when LastSeen advanced, otherwise the run just re-stamps QueryRunId to record
-- "still seen". hotel.Journey.QueryRunId therefore means "the last run that saw this session",
-- which is what makes the read below return exactly the sessions present in the newest run.
--
-- Do not load Air or Cruise KQL rows into these tables. Cruise and Air also upsert on
-- CorrelationId; Air uses Status / FunnelStep / Route.
--
-- Auth when calling from Azure: Managed Identity + Azure SQL (not SQL passwords in source).
-- Connection strings belong in Key Vault / Function app settings.

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'hotel')
    EXEC(N'CREATE SCHEMA hotel');
GO

IF OBJECT_ID(N'hotel.QueryRun', N'U') IS NULL
BEGIN
    CREATE TABLE hotel.QueryRun
    (
        QueryRunId   uniqueidentifier NOT NULL
            CONSTRAINT PK_hotel_QueryRun PRIMARY KEY
            CONSTRAINT DF_hotel_QueryRun_Id DEFAULT NEWSEQUENTIALID(),
        Domain       nvarchar(32)     NOT NULL
            CONSTRAINT DF_hotel_QueryRun_Domain DEFAULT N'hotel',
        Window       nvarchar(16)     NOT NULL,
        Environment  nvarchar(32)     NULL,
        ClubId       nvarchar(64)     NULL,
        Host         nvarchar(256)    NULL,
        FetchedAt    datetimeoffset(7) NOT NULL
            CONSTRAINT DF_hotel_QueryRun_FetchedAt DEFAULT SYSDATETIMEOFFSET(),
        [RowCount]   int              NOT NULL
            CONSTRAINT DF_hotel_QueryRun_RowCount DEFAULT 0,
        Source       nvarchar(32)     NOT NULL
            CONSTRAINT DF_hotel_QueryRun_Source DEFAULT N'live',
        KqlFile      nvarchar(256)    NULL
            CONSTRAINT DF_hotel_QueryRun_KqlFile DEFAULT N'TMobile_HOTEL_Query_KQL_v6d_demo_2h.txt',
        CONSTRAINT CK_hotel_QueryRun_Domain CHECK (Domain = N'hotel')
    );

    CREATE INDEX IX_hotel_QueryRun_FetchedAt
        ON hotel.QueryRun (FetchedAt DESC);
END
GO

-- One row per club. RecordCount is the fetch size for hotel.Journey rows whose
-- QueryRunId is that club's CurrentSnapshot.QueryRunId. A single row per domain
-- would let the next club's ingest hide every other club from the portal.
IF OBJECT_ID(N'hotel.CurrentSnapshot', N'U') IS NULL
BEGIN
    CREATE TABLE hotel.CurrentSnapshot
    (
        Domain       nvarchar(32)      NOT NULL
            CONSTRAINT DF_hotel_CurrentSnapshot_Domain DEFAULT N'hotel',
        QueryRunId   uniqueidentifier  NOT NULL
            CONSTRAINT FK_hotel_CurrentSnapshot_QueryRun
                REFERENCES hotel.QueryRun (QueryRunId),
        FetchedAt    datetimeoffset(7) NOT NULL,
        Window       nvarchar(16)      NOT NULL,
        Environment  nvarchar(32)      NULL,
        ClubId       nvarchar(64)      NOT NULL,
        RecordCount  int               NOT NULL
            CONSTRAINT DF_hotel_CurrentSnapshot_RecordCount DEFAULT 0,
        StepCount    int               NOT NULL
            CONSTRAINT DF_hotel_CurrentSnapshot_StepCount DEFAULT 0,
        CallCount    int               NOT NULL
            CONSTRAINT DF_hotel_CurrentSnapshot_CallCount DEFAULT 0,
        CONSTRAINT PK_hotel_CurrentSnapshot PRIMARY KEY (Domain, ClubId),
        CONSTRAINT CK_hotel_CurrentSnapshot_Domain CHECK (Domain = N'hotel'),
        CONSTRAINT CK_hotel_CurrentSnapshot_RecordCount CHECK (RecordCount >= 0)
    );
END
GO

-- Databases created with the old singleton (primary key on Domain only) keep one club.
-- Widen the key so each brand.Information club has its own snapshot.
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
IF OBJECT_ID(N'hotel.CurrentSnapshot', N'U') IS NOT NULL
   AND NOT EXISTS (
        SELECT 1
        FROM sys.indexes AS i
        INNER JOIN sys.index_columns AS ic
            ON ic.object_id = i.object_id AND ic.index_id = i.index_id
        INNER JOIN sys.columns AS c
            ON c.object_id = ic.object_id AND c.column_id = ic.column_id
        WHERE i.object_id = OBJECT_ID(N'hotel.CurrentSnapshot')
          AND i.is_primary_key = 1
          AND c.name = N'ClubId'
   )
BEGIN
    DECLARE @pk sysname;
    SELECT @pk = name
    FROM sys.key_constraints
    WHERE parent_object_id = OBJECT_ID(N'hotel.CurrentSnapshot')
      AND [type] = N'PK';

    IF @pk IS NOT NULL
    BEGIN
        DECLARE @drop nvarchar(400) = N'ALTER TABLE hotel.CurrentSnapshot DROP CONSTRAINT ' + QUOTENAME(@pk);
        EXEC(@drop);
    END

    DELETE FROM hotel.CurrentSnapshot WHERE ClubId IS NULL;

    ALTER TABLE hotel.CurrentSnapshot ALTER COLUMN ClubId nvarchar(64) NOT NULL;
    ALTER TABLE hotel.CurrentSnapshot
        ADD CONSTRAINT PK_hotel_CurrentSnapshot PRIMARY KEY (Domain, ClubId);
END
GO

IF OBJECT_ID(N'hotel.Journey', N'U') IS NULL
BEGIN
    CREATE TABLE hotel.Journey
    (
        JourneyId              bigint            NOT NULL IDENTITY(1, 1)
            CONSTRAINT PK_hotel_Journey PRIMARY KEY,
        QueryRunId             uniqueidentifier  NOT NULL
            CONSTRAINT FK_hotel_Journey_QueryRun
                REFERENCES hotel.QueryRun (QueryRunId) ON DELETE CASCADE,
        -- Club of the ingest that last wrote this session. KQL rows do not carry it;
        -- usp_IngestKqlRows stamps @ClubId. NOT NULL so a MERGE cannot leave it blank.
        ClubId                 nvarchar(64)      NOT NULL,
        UserId                 nvarchar(128)     NOT NULL,
        CorrelationId          nvarchar(128)     NOT NULL,
        SessionCount           int               NOT NULL
            CONSTRAINT DF_hotel_Journey_SessionCount DEFAULT 0,
        CallsViaCorrelation    int               NOT NULL
            CONSTRAINT DF_hotel_Journey_CallsViaCorrelation DEFAULT 0,
        CallsNoCorrelation     int               NOT NULL
            CONSTRAINT DF_hotel_Journey_CallsNoCorrelation DEFAULT 0,
        Outcome                nvarchar(256)     NOT NULL,
        LastStep               int               NOT NULL
            CONSTRAINT DF_hotel_Journey_LastStep DEFAULT 0,
        FailReason             nvarchar(1024)    NULL,
        BookOk                 int               NOT NULL
            CONSTRAINT DF_hotel_Journey_BookOk DEFAULT 0,
        BookFail               int               NOT NULL
            CONSTRAINT DF_hotel_Journey_BookFail DEFAULT 0,
        DistinctOkRes          int               NOT NULL
            CONSTRAINT DF_hotel_Journey_DistinctOkRes DEFAULT 0,
        BookedReservations     nvarchar(max)     NULL,
        FailedCalls            int               NOT NULL
            CONSTRAINT DF_hotel_Journey_FailedCalls DEFAULT 0,
        IsBookSuccessful       bit               NOT NULL
            CONSTRAINT DF_hotel_Journey_IsBookSuccessful DEFAULT 0,
        PassedSearch           bit               NOT NULL
            CONSTRAINT DF_hotel_Journey_PassedSearch DEFAULT 0,
        Exceptions             int               NOT NULL
            CONSTRAINT DF_hotel_Journey_Exceptions DEFAULT 0,
        JourneyTotalMs         decimal(18, 2)    NULL,
        SlowestCallMs          decimal(18, 2)    NULL,
        OverallP95             decimal(18, 2)    NULL,
        SearchToConfirmSeconds int               NULL,
        FirstSeen              datetimeoffset(7) NULL,
        LastSeen               datetimeoffset(7) NULL,
        StepsJson              nvarchar(max)     NULL,
        JourneyJson            nvarchar(max)     NULL,
        CONSTRAINT UQ_hotel_Journey_Run_Correlation UNIQUE (QueryRunId, CorrelationId),
        CONSTRAINT CK_hotel_Journey_BookedReservations CHECK (BookedReservations IS NULL OR ISJSON(BookedReservations) = 1),
        CONSTRAINT CK_hotel_Journey_StepsJson   CHECK (StepsJson   IS NULL OR ISJSON(StepsJson)   = 1),
        CONSTRAINT CK_hotel_Journey_JourneyJson CHECK (JourneyJson IS NULL OR ISJSON(JourneyJson) = 1)
    );

    CREATE INDEX IX_hotel_Journey_LastSeen
        ON hotel.Journey (LastSeen DESC)
        INCLUDE (UserId, Outcome, CorrelationId);

    CREATE INDEX IX_hotel_Journey_UserId_LastSeen
        ON hotel.Journey (UserId, LastSeen DESC);

    CREATE INDEX IX_hotel_Journey_Outcome_LastSeen
        ON hotel.Journey (Outcome, LastSeen DESC);

    CREATE INDEX IX_hotel_Journey_CorrelationId
        ON hotel.Journey (CorrelationId);
END
GO

-- One-time move off one-row-per-member. The old unique index is the guard: once it is gone,
-- later deploys must not wipe again, and must not collapse sessions that share a UserId.
IF OBJECT_ID(N'hotel.Journey', N'U') IS NOT NULL
   AND EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'UQ_hotel_Journey_UserId'
                 AND object_id = OBJECT_ID(N'hotel.Journey'))
BEGIN
    DELETE FROM hotel.Journey;
    DROP INDEX UQ_hotel_Journey_UserId ON hotel.Journey;
END
GO

-- UQ_hotel_Journey_Club_User (ClubId, UserId) is not created anywhere in this script, but a
-- database provisioned before the session-grain migration above can still carry it (it predates
-- this file's version history). It enforces one row per member per club, which contradicts the
-- CorrelationId session grain documented at the top: a member legitimately gets a second
-- hotel.Journey row in the same club once their prior session's correlation id is reused after a
-- 30+ minute gap, or once a new raw correlation id starts. With that old index still in place,
-- usp_IngestKqlRows's MERGE throws "Cannot insert duplicate key row ... UQ_hotel_Journey_Club_User"
-- on the second session and - because the procedure runs under SET XACT_ABORT ON - rolls back and
-- drops every session that run reported for that whole club, not just the conflicting one.
IF OBJECT_ID(N'hotel.Journey', N'U') IS NOT NULL
   AND EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'UQ_hotel_Journey_Club_User'
                 AND object_id = OBJECT_ID(N'hotel.Journey'))
    DROP INDEX UQ_hotel_Journey_Club_User ON hotel.Journey;
GO

IF OBJECT_ID(N'hotel.Journey', N'U') IS NOT NULL
   AND EXISTS (SELECT 1 FROM sys.key_constraints
               WHERE name = N'UQ_hotel_Journey_Run_User'
                 AND parent_object_id = OBJECT_ID(N'hotel.Journey'))
    ALTER TABLE hotel.Journey DROP CONSTRAINT UQ_hotel_Journey_Run_User;
GO

-- CorrelationId was a nullable label. It is the session key now, so it has to be present.
-- The lookup index has to come off first; SQL Server will not change nullability under it.
IF OBJECT_ID(N'hotel.Journey', N'U') IS NOT NULL
   AND COLUMNPROPERTY(OBJECT_ID(N'hotel.Journey'), N'CorrelationId', 'AllowsNull') = 1
BEGIN
    DELETE FROM hotel.Journey WHERE CorrelationId IS NULL;

    IF EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'IX_hotel_Journey_CorrelationId'
                 AND object_id = OBJECT_ID(N'hotel.Journey'))
        DROP INDEX IX_hotel_Journey_CorrelationId ON hotel.Journey;

    IF EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'IX_hotel_Journey_LastSeen'
                 AND object_id = OBJECT_ID(N'hotel.Journey'))
        DROP INDEX IX_hotel_Journey_LastSeen ON hotel.Journey;

    ALTER TABLE hotel.Journey ALTER COLUMN CorrelationId nvarchar(128) NOT NULL;

    CREATE INDEX IX_hotel_Journey_CorrelationId ON hotel.Journey (CorrelationId);
    CREATE INDEX IX_hotel_Journey_LastSeen
        ON hotel.Journey (LastSeen DESC)
        INCLUDE (UserId, Outcome, CorrelationId);
END
GO

IF OBJECT_ID(N'hotel.Journey', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.key_constraints
                   WHERE name = N'UQ_hotel_Journey_Run_Correlation'
                     AND parent_object_id = OBJECT_ID(N'hotel.Journey'))
    ALTER TABLE hotel.Journey
        ADD CONSTRAINT UQ_hotel_Journey_Run_Correlation UNIQUE (QueryRunId, CorrelationId);
GO

-- The grain is the session, so CorrelationId is the identity ingestion upserts on.
IF OBJECT_ID(N'hotel.Journey', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE name = N'UQ_hotel_Journey_CorrelationId'
                     AND object_id = OBJECT_ID(N'hotel.Journey'))
    CREATE UNIQUE INDEX UQ_hotel_Journey_CorrelationId ON hotel.Journey (CorrelationId);
GO

-- UserId-nocorr-seq rows were a synthetic key for steps that had no correlation id.
-- Those steps now join a real session, so the old key no longer matches an upsert and
-- would linger beside the journey it belongs to. Steps and calls cascade from Journey.
IF OBJECT_ID(N'hotel.Journey', N'U') IS NOT NULL
    DELETE FROM hotel.Journey WHERE CorrelationId LIKE N'%-nocorr-%';
GO

-- Databases created before ClubId existed on the journey row. The ingest parameter is the
-- club; backfill from the query run that last saw the member, then require a value so the
-- MERGE below cannot write NULL (SQL Server reports that as "UPDATE fails").
-- The backfill runs through EXEC on purpose: column names are resolved when the batch is
-- compiled, so a literal UPDATE on the column the line above adds fails the whole batch with
-- "Invalid column name 'ClubId'" and the ALTER never runs.
IF OBJECT_ID(N'hotel.Journey', N'U') IS NOT NULL
   AND COL_LENGTH(N'hotel.Journey', N'ClubId') IS NULL
BEGIN
    ALTER TABLE hotel.Journey ADD ClubId nvarchar(64) NULL;

    EXEC(N'
        UPDATE j
        SET ClubId = r.ClubId
        FROM hotel.Journey AS j
        INNER JOIN hotel.QueryRun AS r
            ON r.QueryRunId = j.QueryRunId
        WHERE j.ClubId IS NULL
          AND NULLIF(r.ClubId, N'''') IS NOT NULL;');
END
GO

IF OBJECT_ID(N'hotel.Journey', N'U') IS NOT NULL
   AND COL_LENGTH(N'hotel.Journey', N'ClubId') IS NOT NULL
   AND EXISTS (
        SELECT 1
        FROM sys.columns
        WHERE object_id = OBJECT_ID(N'hotel.Journey')
          AND name = N'ClubId'
          AND is_nullable = 1
   )
   AND NOT EXISTS (SELECT 1 FROM hotel.Journey WHERE ClubId IS NULL)
BEGIN
    ALTER TABLE hotel.Journey ALTER COLUMN ClubId nvarchar(64) NOT NULL;
END
GO

-- QueryRunId now points at the last run that saw the member, not at the run that created the row,
-- so cascading from QueryRun would delete members who are still current the moment old runs are
-- purged. Fail loudly on such a delete instead.
IF EXISTS (SELECT 1 FROM sys.foreign_keys
           WHERE name = N'FK_hotel_Journey_QueryRun' AND delete_referential_action = 1)
BEGIN
    ALTER TABLE hotel.Journey DROP CONSTRAINT FK_hotel_Journey_QueryRun;
    ALTER TABLE hotel.Journey ADD CONSTRAINT FK_hotel_Journey_QueryRun
        FOREIGN KEY (QueryRunId) REFERENCES hotel.QueryRun (QueryRunId);
END
GO

IF OBJECT_ID(N'hotel.JourneyStep', N'U') IS NULL
BEGIN
    CREATE TABLE hotel.JourneyStep
    (
        JourneyStepId    bigint            NOT NULL IDENTITY(1, 1)
            CONSTRAINT PK_hotel_JourneyStep PRIMARY KEY,
        JourneyId        bigint            NOT NULL
            CONSTRAINT FK_hotel_JourneyStep_Journey
                REFERENCES hotel.Journey (JourneyId) ON DELETE CASCADE,
        StageNo          int               NOT NULL,
        Stage            nvarchar(128)     NOT NULL,
        StepStart        datetimeoffset(7) NULL,
        CallCount        int               NOT NULL
            CONSTRAINT DF_hotel_JourneyStep_CallCount DEFAULT 0,
        StageTotalMs     decimal(18, 2)    NULL,
        P50Ms            decimal(18, 2)    NULL,
        P90Ms            decimal(18, 2)    NULL,
        P95Ms            decimal(18, 2)    NULL,
        P99Ms            decimal(18, 2)    NULL,
        MaxMs            decimal(18, 2)    NULL,
        FailureRatePct   decimal(9, 2)     NULL,
        Status           nvarchar(16)      NULL,
        CONSTRAINT UQ_hotel_JourneyStep UNIQUE (JourneyId, StageNo)
    );

    CREATE INDEX IX_hotel_JourneyStep_StageNo
        ON hotel.JourneyStep (StageNo)
        INCLUDE (Stage, Status, StageTotalMs);
END
GO

IF OBJECT_ID(N'hotel.JourneyCall', N'U') IS NULL
BEGIN
    CREATE TABLE hotel.JourneyCall
    (
        JourneyCallId    bigint            NOT NULL IDENTITY(1, 1)
            CONSTRAINT PK_hotel_JourneyCall PRIMARY KEY,
        JourneyStepId    bigint            NOT NULL
            CONSTRAINT FK_hotel_JourneyCall_JourneyStep
                REFERENCES hotel.JourneyStep (JourneyStepId) ON DELETE CASCADE,
        Kind             nvarchar(32)      NULL,
        CallName         nvarchar(1024)    NOT NULL,
        CallAt           datetimeoffset(7) NULL,
        N                int               NULL,
        CallTotalMs      decimal(18, 2)    NULL,
        P50Ms            decimal(18, 2)    NULL,
        P90Ms            decimal(18, 2)    NULL,
        P95Ms            decimal(18, 2)    NULL,
        P99Ms            decimal(18, 2)    NULL,
        MaxMs            decimal(18, 2)    NULL,
        FailureRatePct   decimal(9, 2)     NULL,
        Status           nvarchar(16)      NULL,
        Detail           nvarchar(1024)    NULL,
        ErrorsJson       nvarchar(max)     NULL,
        CONSTRAINT CK_hotel_JourneyCall_ErrorsJson CHECK (ErrorsJson IS NULL OR ISJSON(ErrorsJson) = 1)
    );

    CREATE INDEX IX_hotel_JourneyCall_Kind
        ON hotel.JourneyCall (Kind);

    CREATE INDEX IX_hotel_JourneyCall_JourneyStepId
        ON hotel.JourneyCall (JourneyStepId);

    CREATE INDEX IX_hotel_JourneyCall_Failed
        ON hotel.JourneyCall (FailureRatePct)
        WHERE FailureRatePct > 0;
END
GO

CREATE OR ALTER VIEW hotel.v_LatestQueryRun
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
    FROM hotel.CurrentSnapshot AS s
    INNER JOIN hotel.QueryRun AS r
        ON r.QueryRunId = s.QueryRunId;
GO

CREATE OR ALTER VIEW hotel.v_LatestJourney
AS
    SELECT j.*
    FROM hotel.Journey AS j
    INNER JOIN hotel.CurrentSnapshot AS s
        ON s.QueryRunId = j.QueryRunId;
GO

-- Ingest named-property JSON: an array of Hotel v6d KQL rows (the portal /api/journeys rows shape),
-- not the Application Insights tables[].rows[] matrix. Parameterize from the app; do not concatenate.
CREATE OR ALTER PROCEDURE hotel.usp_IngestKqlRows
    @Window       nvarchar(16),
    @Environment  nvarchar(32)     = NULL,
    @ClubId       nvarchar(64)     = NULL,
    @Host         nvarchar(256)    = NULL,
    @Source       nvarchar(32)     = N'live',
    @KqlFile      nvarchar(256)    = N'TMobile_HOTEL_Query_KQL_v6d_demo_2h.txt',
    @RowsJson     nvarchar(max)     = NULL,
    @QueryRunId   uniqueidentifier = NULL OUTPUT,
    @RecordCount  int              = NULL OUTPUT,
    -- How the payload split across new members, members whose journey advanced, and members
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
        THROW 50001, N'@RowsJson must be a JSON array of Hotel KQL rows.', 1;
    END

    IF NULLIF(@ClubId, N'') IS NULL
        THROW 50002, N'@ClubId is required. Each club keeps its own current snapshot.', 1;

    IF @QueryRunId IS NULL
        SET @QueryRunId = NEWID();

    DECLARE @Now datetimeoffset(7) = SYSDATETIMEOFFSET();

    BEGIN TRANSACTION;

    INSERT INTO hotel.QueryRun
        (QueryRunId, Domain, Window, Environment, ClubId, Host, FetchedAt, [RowCount], Source, KqlFile)
    VALUES
        (@QueryRunId, N'hotel', @Window, @Environment, @ClubId, @Host, @Now, 0, @Source, @KqlFile);

    -- Which sessions this run touched, and whether the journey actually moved. Steps and calls
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
            r.UserId,
            NULLIF(r.CorrelationId, N'')                    AS CorrelationId,
            NULLIF(r.CorrelationId, N'')                    AS PayloadCorrelationId,
            ISNULL(r.SessionCount, 0)                       AS SessionCount,
            ISNULL(r.CallsViaCorrelation, 0)                AS CallsViaCorrelation,
            ISNULL(r.CallsNoCorrelation, 0)                 AS CallsNoCorrelation,
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
            UserId                   nvarchar(128)     N'$.UserId',
            CorrelationId            nvarchar(128)     N'$.CorrelationId',
            SessionCount             int               N'$.SessionCount',
            CallsViaCorrelation      int               N'$.CallsViaCorrelation',
            CallsNoCorrelation       int               N'$.CallsNoCorrelation',
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
        WHERE NULLIF(r.UserId, N'') IS NOT NULL
          AND NULLIF(r.CorrelationId, N'') IS NOT NULL
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
        FROM hotel.Journey AS j
        WHERE j.UserId = s.UserId
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
                        FROM hotel.Journey AS j
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
            FROM hotel.Journey AS j
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

    -- HotelVisitMerger names a fragment by the id in this payload. A clipped window can
    -- give that raw id a later suffix, and a later stage can become the keeper, so the
    -- stored login is not that exact id. Resolve the fragment to the stored session whose
    -- activity is within 30 minutes, copy the stages the keeper no longer has, then delete
    -- the fragment. A separate visit with the same raw id, more than 30 minutes earlier,
    -- is left alone. PayloadCorrelationId stays the id from the payload after stitching
    -- rewrites CorrelationId.
    SELECT
        keeper.CorrelationId AS KeeperId,
        j.CorrelationId AS StoredId
    INTO #AbsorbedHit
    FROM OPENJSON(@RowsJson) AS payload
    CROSS APPLY OPENJSON(payload.[value], '$.AbsorbedCorrelationIds') AS absorbed
    INNER JOIN #Src AS keeper
        ON keeper.PayloadCorrelationId = JSON_VALUE(payload.[value], '$.CorrelationId')
    INNER JOIN hotel.Journey AS j
        ON (@ClubId IS NULL OR j.ClubId = @ClubId OR j.ClubId IS NULL)
       AND j.UserId = keeper.UserId
       AND j.CorrelationId <> keeper.CorrelationId
       AND (
            j.CorrelationId = absorbed.[value]
            OR (
                absorbed.[value] LIKE N'%-[0-9]%'
                AND j.CorrelationId LIKE N'%-[0-9]%'
                AND RIGHT(absorbed.[value], CHARINDEX(N'-', REVERSE(absorbed.[value])) - 1) NOT LIKE N'%[^0-9]%'
                AND RIGHT(j.CorrelationId, CHARINDEX(N'-', REVERSE(j.CorrelationId)) - 1) NOT LIKE N'%[^0-9]%'
                AND LEN(RIGHT(absorbed.[value], CHARINDEX(N'-', REVERSE(absorbed.[value])) - 1)) BETWEEN 1 AND 6
                AND LEN(RIGHT(j.CorrelationId, CHARINDEX(N'-', REVERSE(j.CorrelationId)) - 1)) BETWEEN 1 AND 6
                AND LEFT(j.CorrelationId, LEN(j.CorrelationId) - CHARINDEX(N'-', REVERSE(j.CorrelationId)))
                    = LEFT(absorbed.[value], LEN(absorbed.[value]) - CHARINDEX(N'-', REVERSE(absorbed.[value])))
                AND j.FirstSeen IS NOT NULL
                AND j.LastSeen IS NOT NULL
                AND keeper.FirstSeen IS NOT NULL
                AND j.LastSeen >= DATEADD(minute, -30, keeper.FirstSeen)
                AND j.FirstSeen <= DATEADD(minute, 30, COALESCE(keeper.LastSeen, keeper.FirstSeen))
            )
       )
    WHERE NULLIF(absorbed.[value], N'') IS NOT NULL
      AND NOT EXISTS (SELECT 1 FROM #Src AS other WHERE other.CorrelationId = j.CorrelationId);

    UPDATE s
    SET
        FirstSeen = CASE
                        WHEN edge.StoredFirst IS NOT NULL AND (s.FirstSeen IS NULL OR edge.StoredFirst < s.FirstSeen)
                            THEN edge.StoredFirst
                        ELSE s.FirstSeen
                    END,
        LastStep = CASE WHEN edge.StoredLastStep > s.LastStep THEN edge.StoredLastStep ELSE s.LastStep END,
        PassedSearch = CASE WHEN edge.StoredPassed = 1 THEN CONVERT(bit, 1) ELSE s.PassedSearch END,
        IsBookSuccessful = CASE WHEN edge.StoredBooked = 1 THEN CONVERT(bit, 1) ELSE s.IsBookSuccessful END,
        BookOk = CASE WHEN edge.StoredBookOk > s.BookOk THEN edge.StoredBookOk ELSE s.BookOk END,
        DistinctOkRes = CASE WHEN edge.StoredDistinct > s.DistinctOkRes THEN edge.StoredDistinct ELSE s.DistinctOkRes END,
        Outcome = CASE
                      WHEN edge.StoredBooked = 1 AND s.IsBookSuccessful = 0 THEN edge.StoredOutcome
                      ELSE s.Outcome
                  END,
        BookedReservations = CASE
                                 WHEN (
                                     s.BookedReservations IS NULL
                                     OR s.BookedReservations IN (N'', N'[]', N'null')
                                 )
                                  AND edge.StoredReservations IS NOT NULL
                                     THEN edge.StoredReservations
                                 ELSE s.BookedReservations
                             END,
        JourneyJson = COALESCE(edge.MergedJourney, s.JourneyJson),
        StepsJson = COALESCE(edge.MergedSteps, s.StepsJson)
    FROM #Src AS s
    CROSS APPLY (
        SELECT
            MIN(j.FirstSeen) AS StoredFirst,
            MAX(j.LastStep) AS StoredLastStep,
            MAX(CONVERT(int, j.PassedSearch)) AS StoredPassed,
            MAX(CONVERT(int, j.IsBookSuccessful)) AS StoredBooked,
            MAX(j.BookOk) AS StoredBookOk,
            MAX(j.DistinctOkRes) AS StoredDistinct,
            (
                SELECT TOP (1) booked.Outcome
                FROM hotel.Journey AS booked
                INNER JOIN #AbsorbedHit AS hit
                    ON hit.StoredId = booked.CorrelationId
                WHERE hit.KeeperId = s.CorrelationId
                  AND booked.IsBookSuccessful = 1
                ORDER BY booked.FirstSeen
            ) AS StoredOutcome,
            (
                SELECT TOP (1) booked.BookedReservations
                FROM hotel.Journey AS booked
                INNER JOIN #AbsorbedHit AS hit
                    ON hit.StoredId = booked.CorrelationId
                WHERE hit.KeeperId = s.CorrelationId
                  AND NULLIF(booked.BookedReservations, N'') IS NOT NULL
                  AND booked.BookedReservations NOT IN (N'[]', N'null')
                ORDER BY booked.IsBookSuccessful DESC, booked.DistinctOkRes DESC
            ) AS StoredReservations,
            (
                SELECT N'[' + STRING_AGG(CAST(chosen.[value] AS nvarchar(max)), N',')
                       WITHIN GROUP (ORDER BY TRY_CONVERT(int, JSON_VALUE(chosen.[value], '$.stageNo'))) + N']'
                FROM (
                    SELECT piece.[value],
                           ROW_NUMBER() OVER (
                               PARTITION BY JSON_VALUE(piece.[value], '$.stageNo')
                               ORDER BY piece.PreferIncoming,
                                        TRY_CONVERT(datetimeoffset(7), JSON_VALUE(piece.[value], '$.t'))
                           ) AS rn
                    FROM (
                        SELECT head.[value], 1 AS PreferIncoming
                        FROM hotel.Journey AS stored
                        INNER JOIN #AbsorbedHit AS hit
                            ON hit.StoredId = stored.CorrelationId
                           AND hit.KeeperId = s.CorrelationId
                        CROSS APPLY OPENJSON(stored.JourneyJson) AS head
                        WHERE ISJSON(stored.JourneyJson) = 1
                        UNION ALL
                        SELECT incoming.[value], 0 AS PreferIncoming
                        FROM OPENJSON(s.JourneyJson) AS incoming
                        WHERE ISJSON(s.JourneyJson) = 1
                    ) AS piece
                ) AS chosen
                WHERE chosen.rn = 1
            ) AS MergedJourney,
            (
                SELECT N'[' + STRING_AGG(N'"' + STRING_ESCAPE(names.stepName, 'json') + N'"', N',') + N']'
                FROM (
                    SELECT DISTINCT steps.[value] AS stepName
                    FROM (
                        SELECT head.[value]
                        FROM hotel.Journey AS stored
                        INNER JOIN #AbsorbedHit AS hit
                            ON hit.StoredId = stored.CorrelationId
                           AND hit.KeeperId = s.CorrelationId
                        CROSS APPLY OPENJSON(stored.StepsJson) AS head
                        WHERE ISJSON(stored.StepsJson) = 1
                          AND ISJSON(head.[value]) = 0
                        UNION
                        SELECT incoming.[value]
                        FROM OPENJSON(s.StepsJson) AS incoming
                        WHERE ISJSON(s.StepsJson) = 1
                          AND ISJSON(incoming.[value]) = 0
                    ) AS steps
                ) AS names
            ) AS MergedSteps
        FROM hotel.Journey AS j
        INNER JOIN #AbsorbedHit AS hit
            ON hit.StoredId = j.CorrelationId
        WHERE hit.KeeperId = s.CorrelationId
    ) AS edge
    WHERE EXISTS (SELECT 1 FROM #AbsorbedHit AS hit WHERE hit.KeeperId = s.CorrelationId);

    DELETE j
    FROM hotel.Journey AS j
    INNER JOIN #AbsorbedHit AS hit
        ON hit.StoredId = j.CorrelationId;

    DROP TABLE #AbsorbedHit;

    MERGE hotel.Journey WITH (HOLDLOCK) AS target
    USING #Src AS src
        ON target.CorrelationId = src.CorrelationId
    -- The session moved on, so the stored row is stale: rewrite it. A row that lists
    -- AbsorbedCorrelationIds also rewrites when the fold does not move LastSeen, but only
    -- if this payload still contains the start of the visit. A later window that dropped
    -- the login step and kept the same LastSeen must not replace the stored journey.
    -- Sessions matched but not advanced fall through to the pass below; SQL Server allows
    -- only one WHEN MATCHED UPDATE per MERGE.
    WHEN MATCHED AND src.LastSeen IS NOT NULL
                 AND (target.LastSeen IS NULL OR src.LastSeen >= target.LastSeen)
                 AND (
            (target.LastSeen IS NULL OR src.LastSeen > target.LastSeen)
            OR (
                EXISTS (
                    SELECT 1
                    FROM OPENJSON(@RowsJson) AS payload
                    CROSS APPLY OPENJSON(payload.[value], '$.AbsorbedCorrelationIds') AS absorbed
                    WHERE JSON_VALUE(payload.[value], '$.CorrelationId') = src.PayloadCorrelationId
                      AND NULLIF(absorbed.value, N'') IS NOT NULL
                )
                AND (target.FirstSeen IS NULL OR src.FirstSeen IS NULL OR src.FirstSeen <= target.FirstSeen)
            )
        ) THEN
        UPDATE SET
            QueryRunId             = @QueryRunId,
            ClubId                 = @ClubId,
            CorrelationId          = src.CorrelationId,
            SessionCount           = src.SessionCount,
            CallsViaCorrelation    = src.CallsViaCorrelation,
            CallsNoCorrelation     = src.CallsNoCorrelation,
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
            FailReason             = src.FailReason,
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
            JourneyTotalMs         = CASE
                                         WHEN target.FirstSeen IS NOT NULL
                                          AND src.FirstSeen > target.FirstSeen
                                          AND ISNULL(target.JourneyTotalMs, 0) > ISNULL(src.JourneyTotalMs, 0)
                                             THEN target.JourneyTotalMs
                                         ELSE src.JourneyTotalMs
                                     END,
            SlowestCallMs          = src.SlowestCallMs,
            OverallP95             = src.OverallP95,
            SearchToConfirmSeconds = src.SearchToConfirmSeconds,
            -- FirstSeen is when we first observed this session; a later payload may have dropped
            -- the earlier steps out of its window, so never let it move forward.
            FirstSeen              = CASE
                                         WHEN target.FirstSeen IS NULL THEN src.FirstSeen
                                         WHEN src.FirstSeen IS NULL THEN target.FirstSeen
                                         WHEN src.FirstSeen < target.FirstSeen THEN src.FirstSeen
                                         ELSE target.FirstSeen
                                     END,
            LastSeen               = src.LastSeen,
            -- A clipped window reports a later FirstSeen. Keep stages that started before
            -- that, and take the fresh tail from this payload. Replacing the JSON wholesale
            -- would delete the login step while FirstSeen still says the visit began then.
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
            QueryRunId, ClubId, UserId, CorrelationId, SessionCount, CallsViaCorrelation, CallsNoCorrelation,
            Outcome, LastStep, FailReason, BookOk, BookFail, DistinctOkRes, BookedReservations,
            FailedCalls, IsBookSuccessful, PassedSearch, Exceptions,
            JourneyTotalMs, SlowestCallMs, OverallP95, SearchToConfirmSeconds,
            FirstSeen, LastSeen, StepsJson, JourneyJson
        )
        VALUES
        (
            @QueryRunId, @ClubId, src.UserId, src.CorrelationId, src.SessionCount, src.CallsViaCorrelation,
            src.CallsNoCorrelation, src.Outcome, src.LastStep, src.FailReason, src.BookOk,
            src.BookFail, src.DistinctOkRes, src.BookedReservations, src.FailedCalls,
            src.IsBookSuccessful, src.PassedSearch, src.Exceptions, src.JourneyTotalMs,
            src.SlowestCallMs, src.OverallP95, src.SearchToConfirmSeconds, src.FirstSeen,
            src.LastSeen, src.StepsJson, src.JourneyJson
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
    FROM hotel.Journey AS j
    INNER JOIN #Src AS s
        ON s.CorrelationId = j.CorrelationId
    WHERE NOT EXISTS (SELECT 1 FROM @Touched AS t WHERE t.JourneyId = j.JourneyId);

    -- Steps and calls are derived from JourneyJson, so they are only stale where the journey moved.
    -- Deleting the steps takes their calls with them through the cascade.
    DELETE FROM hotel.JourneyStep
    WHERE JourneyId IN (SELECT JourneyId FROM @Touched WHERE Changed = 1);

    INSERT INTO hotel.JourneyStep
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
    FROM hotel.Journey AS j
    INNER JOIN @Touched AS t
        ON t.JourneyId = j.JourneyId
       AND t.Changed = 1
    CROSS APPLY OPENJSON(j.JourneyJson) AS stageJson
    CROSS APPLY OPENJSON(stageJson.[value])
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

    INSERT INTO hotel.JourneyCall
    (
        JourneyStepId, Kind, CallName, CallAt, N,
        CallTotalMs, P50Ms, P90Ms, P95Ms, P99Ms, MaxMs,
        FailureRatePct, Status, Detail, ErrorsJson
    )
    SELECT
        st.JourneyStepId,
        c.Kind,
        c.CallName,
        c.CallAt,
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
    FROM hotel.Journey AS j
    INNER JOIN @Touched AS t
        ON t.JourneyId = j.JourneyId
       AND t.Changed = 1
    INNER JOIN hotel.JourneyStep AS st
        ON st.JourneyId = j.JourneyId
    CROSS APPLY OPENJSON(j.JourneyJson) AS stageJson
    CROSS APPLY OPENJSON(stageJson.[value])
    WITH
    (
        StageNo   int           N'$.stageNo',
        CallsJson nvarchar(max) N'$.calls' AS JSON
    ) AS s
    CROSS APPLY OPENJSON(s.CallsJson) AS callJson
    CROSS APPLY OPENJSON(callJson.[value])
    WITH
    (
        Kind           nvarchar(32)      N'$.kind',
        CallName       nvarchar(1024)    N'$.call',
        CallAt         datetimeoffset(7) N'$.t',
        N              int               N'$.n',
        CallTotalMs    decimal(18, 2)    N'$.callTotalMs',
        P50Ms          decimal(18, 2)    N'$.p50Ms',
        P90Ms          decimal(18, 2)    N'$.p90Ms',
        P95Ms          decimal(18, 2)    N'$.p95Ms',
        P99Ms          decimal(18, 2)    N'$.p99Ms',
        MaxMs          decimal(18, 2)    N'$.maxMs',
        FailureRatePct decimal(9, 2)     N'$.failureRatePct',
        Status         nvarchar(16)      N'$.status',
        Detail         nvarchar(1024)    N'$.detail',
        ErrorsJson     nvarchar(max)     N'$.errors' AS JSON
    ) AS c
    WHERE s.StageNo = st.StageNo
      AND c.CallName IS NOT NULL;

    -- Counted over every session this run saw, not just the ones it rewrote: that whole set is
    -- what the snapshot exposes.
    DECLARE @JourneyCount int = (SELECT COUNT(*) FROM @Touched);
    DECLARE @StepCount int =
        (SELECT COUNT(*) FROM hotel.JourneyStep AS st
         INNER JOIN @Touched AS t ON t.JourneyId = st.JourneyId);
    DECLARE @CallCount int =
        (SELECT COUNT(*) FROM hotel.JourneyCall AS c
         INNER JOIN hotel.JourneyStep AS st ON st.JourneyStepId = c.JourneyStepId
         INNER JOIN @Touched AS t ON t.JourneyId = st.JourneyId);

    SET @RecordCount = @JourneyCount;
    SET @InsertedCount  = (SELECT COUNT(*) FROM @Touched WHERE Action = N'INSERT');
    SET @UpdatedCount   = (SELECT COUNT(*) FROM @Touched WHERE Action = N'UPDATE');
    SET @UnchangedCount = (SELECT COUNT(*) FROM @Touched WHERE Action = N'SEEN');

    UPDATE hotel.QueryRun
    SET [RowCount] = @JourneyCount
    WHERE QueryRunId = @QueryRunId;

    MERGE hotel.CurrentSnapshot WITH (HOLDLOCK) AS target
    USING (SELECT N'hotel' AS Domain, @ClubId AS ClubId) AS src
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
        VALUES (N'hotel', @QueryRunId, @Now, @Window, @Environment, @ClubId, @JourneyCount, @StepCount, @CallCount);

    COMMIT TRANSACTION;
END
GO

-- Read path. With @Since, return every session of the club whose LastSeen is still inside
-- the portal range, including sessions the latest 15-minute ingest no longer re-saw.
-- Without @Since, return only the current snapshot. Do not SELECT TOP (@RecordCount)
-- from the whole Journey table.
CREATE OR ALTER PROCEDURE hotel.usp_GetLatestJourneys
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
    FROM hotel.CurrentSnapshot
    WHERE Domain = N'hotel'
    ORDER BY FetchedAt DESC;

    SELECT @RecordCount = COALESCE(SUM(RecordCount), 0)
    FROM hotel.CurrentSnapshot
    WHERE Domain = N'hotel';
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
    FROM hotel.CurrentSnapshot
    WHERE Domain = N'hotel'
      AND ClubId = @RequestedClubId;
END

SELECT @Host = Host
FROM hotel.QueryRun
WHERE QueryRunId = @QueryRunId;

    IF @QueryRunId IS NULL
    BEGIN
        SET @RecordCount = 0;
        RETURN;
    END

    SELECT TOP (@RowCap)
        JourneyId,
        QueryRunId,
        UserId,
        CorrelationId,
        -- Club stamped at ingest. An unfiltered read mixes clubs, so the portal names each
        -- row from this instead of the response header (that header is only the newest snapshot).
        ClubId,
        SessionCount,
        CallsViaCorrelation,
        CallsNoCorrelation,
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
    FROM hotel.Journey
    WHERE (
            (
                @Since IS NOT NULL
                AND (LastSeen IS NULL OR LastSeen >= @Since)
                AND QueryRunId IN (
                    SELECT QueryRunId
                    FROM hotel.QueryRun
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
                        AND QueryRunId IN (SELECT QueryRunId FROM hotel.CurrentSnapshot WHERE Domain = N'hotel')
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
