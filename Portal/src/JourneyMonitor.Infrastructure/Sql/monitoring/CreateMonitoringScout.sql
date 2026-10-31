-- Scout Monkey (detection phase): one row per cycle, and one row per InvestigationPackage handed
-- to L3. Grain/keys mirror the Core domain records exactly (JourneyMonitor.Core/Domain/Scout.cs)
-- so the Sql*Repository classes stay thin Dapper pass-throughs with no translation logic.
--
-- Deliberately NOT folded into monitoring.Finding: a Finding is a latency/SLO cluster, while an
-- InvestigationPackage is a handoff contract that carries the board's measurements and the
-- Scout's inference as two separately addressable payloads.

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'monitoring')
    EXEC(N'CREATE SCHEMA monitoring');
GO

IF OBJECT_ID(N'monitoring.ScoutRun', N'U') IS NULL
BEGIN
    CREATE TABLE monitoring.ScoutRun
    (
        Id                       uniqueidentifier  NOT NULL CONSTRAINT PK_monitoring_ScoutRun PRIMARY KEY,
        -- Which company profile classified this cycle. See the ALTER guard below for why it
        -- defaults to the empty string rather than to an incumbent company's key.
        ProfileKey               nvarchar(128)     NOT NULL CONSTRAINT DF_monitoring_ScoutRun_ProfileKey DEFAULT N'',
        StartedAt                datetimeoffset(7) NOT NULL,
        CompletedAt              datetimeoffset(7) NULL,
        -- running | clear | detected | failed | disabled
        Status                   nvarchar(16)      NOT NULL,
        WindowFrom               datetimeoffset(7) NOT NULL,
        WindowTo                 datetimeoffset(7) NOT NULL,
        BoardRowsEvaluated       int               NOT NULL CONSTRAINT DF_monitoring_ScoutRun_Rows DEFAULT 0,
        CandidatesFound          int               NOT NULL CONSTRAINT DF_monitoring_ScoutRun_Candidates DEFAULT 0,
        PackagesCreated          int               NOT NULL CONSTRAINT DF_monitoring_ScoutRun_Created DEFAULT 0,
        PackagesSkippedDuplicate int               NOT NULL CONSTRAINT DF_monitoring_ScoutRun_Dup DEFAULT 0,
        PackagesSkippedCap       int               NOT NULL CONSTRAINT DF_monitoring_ScoutRun_Cap DEFAULT 0,
        PackagesVetoedByScout    int               NOT NULL CONSTRAINT DF_monitoring_ScoutRun_Veto DEFAULT 0,
        DurationMs               bigint            NOT NULL CONSTRAINT DF_monitoring_ScoutRun_Duration DEFAULT 0,
        ErrorMessage             nvarchar(1024)    NULL
    );

    CREATE INDEX IX_monitoring_ScoutRun_StartedAt ON monitoring.ScoutRun (StartedAt DESC);
END
GO

IF OBJECT_ID(N'monitoring.InvestigationPackage', N'U') IS NULL
BEGIN
    CREATE TABLE monitoring.InvestigationPackage
    (
        Id                 uniqueidentifier  NOT NULL CONSTRAINT PK_monitoring_InvestigationPackage PRIMARY KEY,
        -- "{Source} | {Vertical} | {Entity} | {Stage} | {Bucket}" - part of the dedup key.
        CaseId             nvarchar(512)     NOT NULL,
        -- Which company profile classified this package. Part of the dedup key: the CaseId shape
        -- is identical for two companies reading the same telemetry.
        ProfileKey         nvarchar(128)     NOT NULL CONSTRAINT DF_monitoring_InvPkg_ProfileKey DEFAULT N'',
        -- latency | exception
        Board              nvarchar(16)      NOT NULL,
        Vertical           nvarchar(64)      NOT NULL,
        Stage              nvarchar(128)     NOT NULL,
        Source             nvarchar(64)      NOT NULL,
        -- "club:{id}" | "host:{host}" | "[host/club unavailable]"
        EntityKey          nvarchar(256)     NOT NULL,
        -- club | host | unknown
        EntityKind         nvarchar(16)      NOT NULL,
        ClubId             nvarchar(64)      NULL,
        IcePartnerId       nvarchar(64)      NULL,
        -- NULL when brand.ClubInventory has no match; never a placeholder brand.
        BrandName          nvarchar(200)     NULL,
        -- club-id | ice-partner-id | host-exact | host-base-domain | unresolved
        ClubResolvedBy     nvarchar(32)      NOT NULL,
        Priority           nvarchar(64)      NOT NULL,
        PriorityRank       int               NOT NULL,
        Reason             nvarchar(512)     NOT NULL,
        WindowFrom         datetimeoffset(7) NOT NULL,
        WindowTo           datetimeoffset(7) NOT NULL,
        -- What the board measured, verbatim. Facts only.
        DeterministicJson  nvarchar(max)     NOT NULL,
        -- What the Scout inferred. Never merged with the above.
        AssessmentJson     nvarchar(max)     NOT NULL,
        RequestCount       int               NOT NULL CONSTRAINT DF_monitoring_InvPkg_Requests DEFAULT 0,
        FailedRequestCount int               NOT NULL CONSTRAINT DF_monitoring_InvPkg_Failed DEFAULT 0,
        ExceptionCount     int               NOT NULL CONSTRAINT DF_monitoring_InvPkg_Exceptions DEFAULT 0,
        AffectedUserCount  int               NOT NULL CONSTRAINT DF_monitoring_InvPkg_Users DEFAULT 0,
        -- pending_handoff | handed_off | dismissed
        Status             nvarchar(32)      NOT NULL CONSTRAINT DF_monitoring_InvPkg_Status DEFAULT N'pending_handoff',
        ScoutRunId         uniqueidentifier  NOT NULL,
        CreatedAt          datetimeoffset(7) NOT NULL CONSTRAINT DF_monitoring_InvPkg_CreatedAt DEFAULT SYSDATETIMEOFFSET()
    );

    -- The dedup guarantee lives here, not only in C#: two cycles overlapping on the same instance
    -- (or a future scale-out) must not be able to package the same bucket twice. Scoped to the
    -- board because the CaseId is not: the same group and bucket can be flagged by the latency
    -- board and by the exception board for different reasons, and each scout decides its own case.
    -- Scoped to the profile for the same reason one level up: two companies reading the same
    -- telemetry produce the same CaseId, and whichever ran first would otherwise suppress the
    -- other's package entirely.
    CREATE UNIQUE INDEX UQ_monitoring_InvestigationPackage_CaseId_Board_Profile ON monitoring.InvestigationPackage (CaseId, Board, ProfileKey);
    CREATE INDEX IX_monitoring_InvestigationPackage_CreatedAt ON monitoring.InvestigationPackage (CreatedAt DESC);
    CREATE INDEX IX_monitoring_InvestigationPackage_Status ON monitoring.InvestigationPackage (Status, CreatedAt DESC);
    CREATE INDEX IX_monitoring_InvestigationPackage_Entity ON monitoring.InvestigationPackage (EntityKey, CreatedAt DESC);
    CREATE INDEX IX_monitoring_InvestigationPackage_Board ON monitoring.InvestigationPackage (Board, CreatedAt DESC);
    CREATE INDEX IX_monitoring_InvestigationPackage_Profile ON monitoring.InvestigationPackage (ProfileKey, CreatedAt DESC);
END
GO

-- The club's own display name (brand.ClubInventory.ClubName) - BrandName above is only the chain.
IF COL_LENGTH(N'monitoring.InvestigationPackage', N'ClubName') IS NULL
    ALTER TABLE monitoring.InvestigationPackage ADD ClubName nvarchar(200) NULL;
GO

-- Columns added after the tables first shipped. "IF OBJECT_ID ... IS NULL CREATE TABLE" above does
-- nothing on a database where the table already exists, so every later column needs its own
-- idempotent ALTER or it silently never reaches an existing environment.
IF COL_LENGTH(N'monitoring.ScoutRun', N'BoardQueriesJson') IS NULL
    ALTER TABLE monitoring.ScoutRun ADD BoardQueriesJson nvarchar(max) NULL;
GO

-- Which candidates were skipped as already-known, not just how many: a cycle that hands off
-- nothing still did work, and the reader needs to see what it recognised.
IF COL_LENGTH(N'monitoring.ScoutRun', N'SkippedCaseIdsJson') IS NULL
    ALTER TABLE monitoring.ScoutRun ADD SkippedCaseIdsJson nvarchar(max) NULL;
GO

IF COL_LENGTH(N'monitoring.InvestigationPackage', N'DrillDownKql') IS NULL
    ALTER TABLE monitoring.InvestigationPackage ADD DrillDownKql nvarchar(max) NULL;
GO

IF COL_LENGTH(N'monitoring.InvestigationPackage', N'RawEvidenceJson') IS NULL
    ALTER TABLE monitoring.InvestigationPackage ADD RawEvidenceJson nvarchar(max) NULL;
GO

IF COL_LENGTH(N'monitoring.ScoutRun', N'ClubsWatched') IS NULL
    ALTER TABLE monitoring.ScoutRun ADD ClubsWatched int NOT NULL CONSTRAINT DF_monitoring_ScoutRun_Clubs DEFAULT 0;
GO

-- What an in-flight cycle is doing. NULL once it finishes.
IF COL_LENGTH(N'monitoring.ScoutRun', N'Phase') IS NULL
    ALTER TABLE monitoring.ScoutRun ADD Phase nvarchar(32) NULL;
GO

-- Which board the cycle read. Added when the single scout was split into one agent per board;
-- a cycle now covers exactly one board, and this is what tells the two agents' cycles apart.
-- Defaulted to 'latency' so a pre-split row is attributed to something rather than to NULL.
IF COL_LENGTH(N'monitoring.ScoutRun', N'Board') IS NULL
    ALTER TABLE monitoring.ScoutRun ADD Board nvarchar(16) NOT NULL CONSTRAINT DF_monitoring_ScoutRun_Board DEFAULT N'latency';
GO

-- Which company profile classified the work. One deployment now serves several companies, and
-- until this column existed nothing the pipeline produced said whose it was.
--
-- Why the default is N'' and NOT the incumbent company's key: rows written before this column
-- existed were produced by whatever profile the deployment happened to be configured with at the
-- time, and nobody recorded that. Stamping them with 'arrivia' would turn an assumption into a
-- stored fact in the one table whose purpose is to say what was actually measured, and it would
-- also make those rows dedup-collide with real arrivia cycles re-reading the same window - a
-- package would be skipped as "already known" on the strength of a guess. N'' is honest:
-- unattributed. The cost is real and accepted - a per-company filter will not return them, so
-- historical packages become invisible to a company view until someone decides, with evidence,
-- to backfill them. That is a deliberate trade of reach for truthfulness.
-- NOT NULL + DEFAULT because the table has rows: ADD ... NOT NULL without a default fails.
IF COL_LENGTH(N'monitoring.InvestigationPackage', N'ProfileKey') IS NULL
    ALTER TABLE monitoring.InvestigationPackage ADD ProfileKey nvarchar(128) NOT NULL
        CONSTRAINT DF_monitoring_InvPkg_ProfileKey DEFAULT N'';
GO

-- Same column, same reasoning, on the cycle log. Also matches the C# default (ScoutRun.ProfileKey
-- is ""): a cycle that fails before resolving a profile is recorded as having classified under
-- nothing, which is a fact worth keeping rather than an error to hide.
IF COL_LENGTH(N'monitoring.ScoutRun', N'ProfileKey') IS NULL
    ALTER TABLE monitoring.ScoutRun ADD ProfileKey nvarchar(128) NOT NULL
        CONSTRAINT DF_monitoring_ScoutRun_ProfileKey DEFAULT N'';
GO

-- Same split: the dedup key gained the board. An environment provisioned before the split still
-- carries the CaseId-only unique index, which would make the second scout's package collide with
-- the first scout's for the same group and bucket.
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_monitoring_InvestigationPackage_CaseId' AND object_id = OBJECT_ID(N'monitoring.InvestigationPackage'))
    DROP INDEX UQ_monitoring_InvestigationPackage_CaseId ON monitoring.InvestigationPackage;
GO

-- And now it gained the profile. The two-column index is actively harmful once a second company
-- exists: it makes the two companies' packages for the same group and bucket mutually exclusive
-- at the storage layer, so it is dropped rather than left alongside the new one.
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_monitoring_InvestigationPackage_CaseId_Board' AND object_id = OBJECT_ID(N'monitoring.InvestigationPackage'))
    DROP INDEX UQ_monitoring_InvestigationPackage_CaseId_Board ON monitoring.InvestigationPackage;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_monitoring_InvestigationPackage_CaseId_Board_Profile' AND object_id = OBJECT_ID(N'monitoring.InvestigationPackage'))
    CREATE UNIQUE INDEX UQ_monitoring_InvestigationPackage_CaseId_Board_Profile ON monitoring.InvestigationPackage (CaseId, Board, ProfileKey);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_monitoring_InvestigationPackage_Board' AND object_id = OBJECT_ID(N'monitoring.InvestigationPackage'))
    CREATE INDEX IX_monitoring_InvestigationPackage_Board ON monitoring.InvestigationPackage (Board, CreatedAt DESC);
GO

-- "Everything company X produced, newest first." ProfileKey leads because it is the equality
-- predicate and the only one that partitions the table by customer - a seek lands on one
-- company's contiguous range. CreatedAt DESC follows so the rows come out of the index already in
-- the order every read wants, with no sort on top. The reverse order (CreatedAt, ProfileKey)
-- would force a scan across every company's rows to answer the same question. Same shape as the
-- Board/Status/Entity indexes above, for the same reason.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_monitoring_InvestigationPackage_Profile' AND object_id = OBJECT_ID(N'monitoring.InvestigationPackage'))
    CREATE INDEX IX_monitoring_InvestigationPackage_Profile ON monitoring.InvestigationPackage (ProfileKey, CreatedAt DESC);
GO

-- Same shape on the cycle log, for the same reason: usp_GetRecentScoutRuns and usp_GetScoutStats
-- now filter by ProfileKey, and usp_GetScoutStats alone runs a dozen aggregates over this table
-- per call - without a profile-leading index every one of them scans every company's cycles.
-- StartedAt DESC follows because that is the order both the run list and the MIN/MAX stats want.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_monitoring_ScoutRun_Profile' AND object_id = OBJECT_ID(N'monitoring.ScoutRun'))
    CREATE INDEX IX_monitoring_ScoutRun_Profile ON monitoring.ScoutRun (ProfileKey, StartedAt DESC);
GO

-- Audit trail of the Scout's LLM calls - prompt sent and raw response received, before any
-- parsing. Same role monitoring.LlmCallLog plays for the Doctor Monkey, and separate from
-- InvestigationPackage for the same reason: the package is a handoff contract, this is evidence
-- of how the opinion inside it was produced.
IF OBJECT_ID(N'monitoring.ScoutLlmCallLog', N'U') IS NULL
BEGIN
    CREATE TABLE monitoring.ScoutLlmCallLog
    (
        Id                     uniqueidentifier  NOT NULL CONSTRAINT PK_monitoring_ScoutLlmCallLog PRIMARY KEY,
        InvestigationPackageId uniqueidentifier  NOT NULL,
        PromptText             nvarchar(max)     NOT NULL,
        RawResponseText        nvarchar(max)     NOT NULL,
        ModelUsed              nvarchar(128)     NOT NULL,
        TokensIn               int               NOT NULL CONSTRAINT DF_monitoring_ScoutLlmCallLog_TokensIn DEFAULT 0,
        TokensOut              int               NOT NULL CONSTRAINT DF_monitoring_ScoutLlmCallLog_TokensOut DEFAULT 0,
        DurationMs             bigint            NOT NULL CONSTRAINT DF_monitoring_ScoutLlmCallLog_Duration DEFAULT 0,
        ErrorMessage           nvarchar(1024)    NULL,
        CalledAt               datetimeoffset(7) NOT NULL
            CONSTRAINT DF_monitoring_ScoutLlmCallLog_CalledAt DEFAULT SYSDATETIMEOFFSET()
    );

    CREATE UNIQUE INDEX UQ_monitoring_ScoutLlmCallLog_PackageId ON monitoring.ScoutLlmCallLog (InvestigationPackageId);
END
GO

CREATE OR ALTER PROCEDURE monitoring.usp_AddScoutLlmCallLog
    @Id uniqueidentifier, @InvestigationPackageId uniqueidentifier, @PromptText nvarchar(max),
    @RawResponseText nvarchar(max), @ModelUsed nvarchar(128), @TokensIn int, @TokensOut int,
    @DurationMs bigint, @ErrorMessage nvarchar(1024), @CalledAt datetimeoffset(7)
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM monitoring.ScoutLlmCallLog WITH (UPDLOCK, HOLDLOCK) WHERE InvestigationPackageId = @InvestigationPackageId)
    BEGIN
        INSERT INTO monitoring.ScoutLlmCallLog
            (Id, InvestigationPackageId, PromptText, RawResponseText, ModelUsed, TokensIn, TokensOut, DurationMs, ErrorMessage, CalledAt)
        VALUES
            (@Id, @InvestigationPackageId, @PromptText, @RawResponseText, @ModelUsed, @TokensIn, @TokensOut, @DurationMs, @ErrorMessage, @CalledAt);
    END
END
GO

CREATE OR ALTER PROCEDURE monitoring.usp_GetScoutLlmCallLogByPackageId
    @InvestigationPackageId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    SELECT TOP (1) Id, InvestigationPackageId, PromptText, RawResponseText, ModelUsed,
           TokensIn, TokensOut, DurationMs, ErrorMessage, CalledAt
    FROM monitoring.ScoutLlmCallLog
    WHERE InvestigationPackageId = @InvestigationPackageId;
END
GO

CREATE OR ALTER PROCEDURE monitoring.usp_AddScoutRun

    @Id uniqueidentifier, @Board nvarchar(16), @ProfileKey nvarchar(128), @StartedAt datetimeoffset(7), @CompletedAt datetimeoffset(7),
    @Status nvarchar(16), @WindowFrom datetimeoffset(7), @WindowTo datetimeoffset(7),
    @BoardRowsEvaluated int, @ClubsWatched int, @CandidatesFound int, @PackagesCreated int,
    @PackagesSkippedDuplicate int, @PackagesSkippedCap int, @PackagesVetoedByScout int,
    @DurationMs bigint, @ErrorMessage nvarchar(1024)
AS
BEGIN
    SET NOCOUNT ON;
    -- @ProfileKey is normally N'' here: the row is written before the company profile resolves, so
    -- usp_UpdateScoutRun is what actually attributes the cycle.
    INSERT INTO monitoring.ScoutRun
        (Id, Board, ProfileKey, StartedAt, CompletedAt, Status, WindowFrom, WindowTo, BoardRowsEvaluated, ClubsWatched, CandidatesFound,
         PackagesCreated, PackagesSkippedDuplicate, PackagesSkippedCap, PackagesVetoedByScout, DurationMs, ErrorMessage)
    VALUES
        (@Id, @Board, @ProfileKey, @StartedAt, @CompletedAt, @Status, @WindowFrom, @WindowTo, @BoardRowsEvaluated, @ClubsWatched, @CandidatesFound,
         @PackagesCreated, @PackagesSkippedDuplicate, @PackagesSkippedCap, @PackagesVetoedByScout, @DurationMs, @ErrorMessage);
END
GO

CREATE OR ALTER PROCEDURE monitoring.usp_UpdateScoutRun
    @Id uniqueidentifier, @ProfileKey nvarchar(128), @CompletedAt datetimeoffset(7), @Status nvarchar(16),
    @BoardRowsEvaluated int, @ClubsWatched int, @CandidatesFound int, @PackagesCreated int,
    @PackagesSkippedDuplicate int, @PackagesSkippedCap int, @PackagesVetoedByScout int,
    @DurationMs bigint, @BoardQueriesJson nvarchar(max), @SkippedCaseIdsJson nvarchar(max),
    @ErrorMessage nvarchar(1024)
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE monitoring.ScoutRun
    SET CompletedAt = @CompletedAt,
        Status = @Status,
        -- The attributing write: the insert could only carry the default, because the profile
        -- resolves after the row exists.
        ProfileKey = @ProfileKey,
        BoardRowsEvaluated = @BoardRowsEvaluated,
        ClubsWatched = @ClubsWatched,
        Phase = NULL,
        CandidatesFound = @CandidatesFound,
        PackagesCreated = @PackagesCreated,
        PackagesSkippedDuplicate = @PackagesSkippedDuplicate,
        PackagesSkippedCap = @PackagesSkippedCap,
        PackagesVetoedByScout = @PackagesVetoedByScout,
        DurationMs = @DurationMs,
        BoardQueriesJson = @BoardQueriesJson,
        SkippedCaseIdsJson = @SkippedCaseIdsJson,
        ErrorMessage = @ErrorMessage
    WHERE Id = @Id;
END
GO

-- Deliberately narrow: called several times per cycle just to publish progress, so it touches
-- only the columns that change mid-flight.
CREATE OR ALTER PROCEDURE monitoring.usp_UpdateScoutRunPhase
    @Id uniqueidentifier, @Phase nvarchar(32), @BoardRowsEvaluated int, @ClubsWatched int
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE monitoring.ScoutRun
    SET Phase = @Phase, BoardRowsEvaluated = @BoardRowsEvaluated, ClubsWatched = @ClubsWatched
    WHERE Id = @Id AND CompletedAt IS NULL;
END
GO

-- Cumulative proof of work. Token totals come from the LLM call log, not from the runs, because
-- they are the one number that cannot exist without a model having actually run.
-- @Board NULL covers both scouts; each card passes its own board.
--
-- @ProfileKey NULL means EVERY COMPANY - the pre-multi-company behaviour, kept so existing
-- callers that do not pass it keep seeing the same numbers. The default is NOT N'': N'' is a real
-- stored value (the ProfileKey of every row written before the column existed, see the ALTER
-- above), so defaulting to it would silently turn "all companies" into "only the unattributed
-- rows" and every KPI would read zero. NULL is the only value that cannot collide with a real one.
-- Without it these counters sum two customers into one number, which is worse than no number.
CREATE OR ALTER PROCEDURE monitoring.usp_GetScoutStats
    @Board nvarchar(16) = NULL,
    @ProfileKey nvarchar(128) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SELECT
        Cycles             = (SELECT COUNT(*) FROM monitoring.ScoutRun WHERE CompletedAt IS NOT NULL AND (@Board IS NULL OR Board = @Board) AND (@ProfileKey IS NULL OR ProfileKey = @ProfileKey)),
        ClearCycles        = (SELECT COUNT(*) FROM monitoring.ScoutRun WHERE Status = N'clear' AND (@Board IS NULL OR Board = @Board) AND (@ProfileKey IS NULL OR ProfileKey = @ProfileKey)),
        DetectedCycles     = (SELECT COUNT(*) FROM monitoring.ScoutRun WHERE Status = N'detected' AND (@Board IS NULL OR Board = @Board) AND (@ProfileKey IS NULL OR ProfileKey = @ProfileKey)),
        FailedCycles       = (SELECT COUNT(*) FROM monitoring.ScoutRun WHERE Status = N'failed' AND (@Board IS NULL OR Board = @Board) AND (@ProfileKey IS NULL OR ProfileKey = @ProfileKey)),
        BoardRowsEvaluated = ISNULL((SELECT SUM(CAST(BoardRowsEvaluated AS bigint)) FROM monitoring.ScoutRun WHERE (@Board IS NULL OR Board = @Board) AND (@ProfileKey IS NULL OR ProfileKey = @ProfileKey)), 0),
        CandidatesFound    = ISNULL((SELECT SUM(CandidatesFound) FROM monitoring.ScoutRun WHERE (@Board IS NULL OR Board = @Board) AND (@ProfileKey IS NULL OR ProfileKey = @ProfileKey)), 0),
        PackagesCreated    = ISNULL((SELECT SUM(PackagesCreated) FROM monitoring.ScoutRun WHERE (@Board IS NULL OR Board = @Board) AND (@ProfileKey IS NULL OR ProfileKey = @ProfileKey)), 0),
        DuplicatesAvoided  = ISNULL((SELECT SUM(PackagesSkippedDuplicate) FROM monitoring.ScoutRun WHERE (@Board IS NULL OR Board = @Board) AND (@ProfileKey IS NULL OR ProfileKey = @ProfileKey)), 0),
        MaxClubsWatched    = ISNULL((SELECT MAX(ClubsWatched) FROM monitoring.ScoutRun WHERE (@Board IS NULL OR Board = @Board) AND (@ProfileKey IS NULL OR ProfileKey = @ProfileKey)), 0),
        -- Joined through the package so the token totals follow the same board AND profile filter
        -- as the rest: the call log has no ProfileKey of its own, the package it documents does.
        -- Both predicates live inside the EXISTS so an unfiltered call still counts every row.
        LlmCalls           = (SELECT COUNT(*) FROM monitoring.ScoutLlmCallLog l
                              WHERE (@Board IS NULL AND @ProfileKey IS NULL)
                                 OR EXISTS (SELECT 1 FROM monitoring.InvestigationPackage p WHERE p.Id = l.InvestigationPackageId
                                            AND (@Board IS NULL OR p.Board = @Board) AND (@ProfileKey IS NULL OR p.ProfileKey = @ProfileKey))),
        TokensIn           = ISNULL((SELECT SUM(CAST(l.TokensIn AS bigint)) FROM monitoring.ScoutLlmCallLog l
                              WHERE (@Board IS NULL AND @ProfileKey IS NULL)
                                 OR EXISTS (SELECT 1 FROM monitoring.InvestigationPackage p WHERE p.Id = l.InvestigationPackageId
                                            AND (@Board IS NULL OR p.Board = @Board) AND (@ProfileKey IS NULL OR p.ProfileKey = @ProfileKey))), 0),
        TokensOut          = ISNULL((SELECT SUM(CAST(l.TokensOut AS bigint)) FROM monitoring.ScoutLlmCallLog l
                              WHERE (@Board IS NULL AND @ProfileKey IS NULL)
                                 OR EXISTS (SELECT 1 FROM monitoring.InvestigationPackage p WHERE p.Id = l.InvestigationPackageId
                                            AND (@Board IS NULL OR p.Board = @Board) AND (@ProfileKey IS NULL OR p.ProfileKey = @ProfileKey))), 0),
        FirstCycleAt       = (SELECT MIN(StartedAt) FROM monitoring.ScoutRun WHERE (@Board IS NULL OR Board = @Board) AND (@ProfileKey IS NULL OR ProfileKey = @ProfileKey)),
        LastCycleAt        = (SELECT MAX(StartedAt) FROM monitoring.ScoutRun WHERE (@Board IS NULL OR Board = @Board) AND (@ProfileKey IS NULL OR ProfileKey = @ProfileKey));
END
GO

-- @ProfileKey NULL means every company, for the reason spelled out on usp_GetScoutStats above.
-- Seeks IX_monitoring_ScoutRun_Profile (ProfileKey, StartedAt DESC) when a company is named.
CREATE OR ALTER PROCEDURE monitoring.usp_GetRecentScoutRuns
    @Top int, @Board nvarchar(16) = NULL, @ProfileKey nvarchar(128) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    -- Column order is load-bearing: SqlScoutRunRepository.ScoutRunRow is a positional record, and
    -- Dapper matches it to this list by position AND name with an exact count. @ProfileKey only
    -- narrows the rows - it must never change this list.
    SELECT TOP (@Top) Id, Board, ProfileKey, StartedAt, CompletedAt, Status, WindowFrom, WindowTo, BoardRowsEvaluated,
           ClubsWatched, Phase, CandidatesFound, PackagesCreated, PackagesSkippedDuplicate,
           PackagesSkippedCap, PackagesVetoedByScout, DurationMs, BoardQueriesJson, SkippedCaseIdsJson, ErrorMessage
    FROM monitoring.ScoutRun
    WHERE (@Board IS NULL OR Board = @Board)
      AND (@ProfileKey IS NULL OR ProfileKey = @ProfileKey)
    ORDER BY StartedAt DESC;
END
GO

CREATE OR ALTER PROCEDURE monitoring.usp_AddInvestigationPackage
    @Id uniqueidentifier, @CaseId nvarchar(512), @ProfileKey nvarchar(128), @Board nvarchar(16), @Vertical nvarchar(64),
    @Stage nvarchar(128), @Source nvarchar(64), @EntityKey nvarchar(256), @EntityKind nvarchar(16),
    @ClubId nvarchar(64), @IcePartnerId nvarchar(64), @BrandName nvarchar(200), @ClubName nvarchar(200), @ClubResolvedBy nvarchar(32),
    @Priority nvarchar(64), @PriorityRank int, @Reason nvarchar(512),
    @WindowFrom datetimeoffset(7), @WindowTo datetimeoffset(7),
    @DeterministicJson nvarchar(max), @AssessmentJson nvarchar(max),
    @DrillDownKql nvarchar(max), @RawEvidenceJson nvarchar(max),
    @RequestCount int, @FailedRequestCount int, @ExceptionCount int, @AffectedUserCount int,
    @Status nvarchar(32), @ScoutRunId uniqueidentifier, @CreatedAt datetimeoffset(7)
AS
BEGIN
    SET NOCOUNT ON;
    -- Idempotent on (CaseId, Board, ProfileKey): a duplicate is the expected outcome of a 4h window
    -- being re-read every 5 minutes, not an error worth failing the whole cycle over. The board is
    -- part of the key because the same group and bucket can be flagged by the latency board and by
    -- the exception board for different reasons; the profile is part of it because two companies
    -- reading the same telemetry produce the same CaseId, and without it whichever ran first would
    -- silently suppress the other's work.
    -- @ProfileKey has no default on purpose: an unattributed package should fail loudly at the
    -- call site, not be quietly filed under N''.
    IF NOT EXISTS (SELECT 1 FROM monitoring.InvestigationPackage WITH (UPDLOCK, HOLDLOCK)
                   WHERE CaseId = @CaseId AND Board = @Board AND ProfileKey = @ProfileKey)
    BEGIN
        INSERT INTO monitoring.InvestigationPackage
            (Id, CaseId, ProfileKey, Board, Vertical, Stage, Source, EntityKey, EntityKind, ClubId, IcePartnerId,
             BrandName, ClubName, ClubResolvedBy, Priority, PriorityRank, Reason, WindowFrom, WindowTo,
             DeterministicJson, AssessmentJson, DrillDownKql, RawEvidenceJson,
             RequestCount, FailedRequestCount, ExceptionCount,
             AffectedUserCount, Status, ScoutRunId, CreatedAt)
        VALUES
            (@Id, @CaseId, @ProfileKey, @Board, @Vertical, @Stage, @Source, @EntityKey, @EntityKind, @ClubId, @IcePartnerId,
             @BrandName, @ClubName, @ClubResolvedBy, @Priority, @PriorityRank, @Reason, @WindowFrom, @WindowTo,
             @DeterministicJson, @AssessmentJson, @DrillDownKql, @RawEvidenceJson,
             @RequestCount, @FailedRequestCount, @ExceptionCount,
             @AffectedUserCount, @Status, @ScoutRunId, @CreatedAt);
    END
END
GO

CREATE OR ALTER PROCEDURE monitoring.usp_InvestigationPackageExists
    @CaseId nvarchar(512), @Board nvarchar(16), @ProfileKey nvarchar(128)
AS
BEGIN
    SET NOCOUNT ON;
    SELECT CAST(CASE WHEN EXISTS (SELECT 1 FROM monitoring.InvestigationPackage
                                  WHERE CaseId = @CaseId AND Board = @Board AND ProfileKey = @ProfileKey)
                     THEN 1 ELSE 0 END AS bit);
END
GO

CREATE OR ALTER PROCEDURE monitoring.usp_GetInvestigationPackageById
    @Id uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    -- Column order is load-bearing: SqlInvestigationPackageRepository.PackageRow is a positional
    -- record, and Dapper matches it to this list by position AND name with an exact count.
    SELECT TOP (1) Id, CaseId, ProfileKey, Board, Vertical, Stage, Source, EntityKey, EntityKind, ClubId, IcePartnerId,
           BrandName, ClubName, ClubResolvedBy, Priority, PriorityRank, Reason, WindowFrom, WindowTo,
           DeterministicJson, AssessmentJson, DrillDownKql, RawEvidenceJson,
           RequestCount, FailedRequestCount, ExceptionCount,
           AffectedUserCount, Status, ScoutRunId, CreatedAt
    FROM monitoring.InvestigationPackage
    WHERE Id = @Id;
END
GO

-- @ProfileKey NULL means every company, for the reason spelled out on usp_GetScoutStats above.
-- A named company seeks IX_monitoring_InvestigationPackage_Profile (ProfileKey, CreatedAt DESC),
-- which already delivers the rows in this ORDER BY with no sort on top.
CREATE OR ALTER PROCEDURE monitoring.usp_GetRecentInvestigationPackages
    @Top int, @Board nvarchar(16) = NULL, @ProfileKey nvarchar(128) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    -- Same positional contract as usp_GetInvestigationPackageById above: @ProfileKey only narrows
    -- the rows, it must never change this column list.
    SELECT TOP (@Top) Id, CaseId, ProfileKey, Board, Vertical, Stage, Source, EntityKey, EntityKind, ClubId, IcePartnerId,
           BrandName, ClubName, ClubResolvedBy, Priority, PriorityRank, Reason, WindowFrom, WindowTo,
           DeterministicJson, AssessmentJson, DrillDownKql, RawEvidenceJson,
           RequestCount, FailedRequestCount, ExceptionCount,
           AffectedUserCount, Status, ScoutRunId, CreatedAt
    FROM monitoring.InvestigationPackage
    WHERE (@Board IS NULL OR Board = @Board)
      AND (@ProfileKey IS NULL OR ProfileKey = @ProfileKey)
    ORDER BY CreatedAt DESC;
END
GO

-- A package is a 5-minute snapshot, so on its own it cannot say whether the problem is still
-- firing. CaseId is "{GroupKey} | {bucket}" by construction, so the group is everything before
-- the last '|' - cheaper than carrying a denormalised GroupKey column.
CREATE OR ALTER PROCEDURE monitoring.usp_GetInvestigationPackageRecurrence
    @GroupKey nvarchar(512)
AS
BEGIN
    SET NOCOUNT ON;
    SELECT COUNT(*) AS Occurrences, MIN(WindowFrom) AS FirstSeen, MAX(WindowFrom) AS LastSeen
    FROM monitoring.InvestigationPackage
    WHERE LTRIM(RTRIM(LEFT(CaseId, LEN(CaseId) - CHARINDEX('|', REVERSE(CaseId))))) = @GroupKey;
END
GO
