-- Inspector Monkey (L3 analysis phase): the generic monkey-to-monkey work queue, plus one row per
-- investigation and the full agent transcript behind it. Grain/keys mirror the Core domain types
-- exactly (JourneyMonitor.Core/Domain/Inspector.cs) so the Sql*Repository classes stay thin Dapper
-- pass-throughs with no translation logic.
--
-- AgentWorkQueue is deliberately NOT named after the Inspector: the Scout is simply the first
-- producer and the Inspector the first consumer. Adding a monkey must be a new SourceMonkey value,
-- not a new queue table with its own near-identical claim logic to get subtly wrong.
--
-- The claim is one atomic statement on purpose (see usp_TryLeaseAgentWork): an investigation costs
-- real LLM calls, so two instances claiming the same row is money spent twice, not a cosmetic race.
-- Leases expire rather than being released explicitly, because the failure mode that matters is the
-- consumer dying mid-investigation - and a dead process cannot run cleanup code.

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'monitoring')
    EXEC(N'CREATE SCHEMA monitoring');
GO

IF OBJECT_ID(N'monitoring.AgentWorkQueue', N'U') IS NULL
BEGIN
    CREATE TABLE monitoring.AgentWorkQueue
    (
        Id           uniqueidentifier  NOT NULL CONSTRAINT PK_monitoring_AgentWorkQueue PRIMARY KEY,
        -- Which monkey produced the work: scout | inspector | ...
        SourceMonkey nvarchar(64)      NOT NULL,
        -- investigation-package
        PayloadType  nvarchar(64)      NOT NULL,
        -- Id of the row this points at, as text so the queue never depends on a payload's key type.
        PayloadId    nvarchar(128)     NOT NULL,
        -- Natural key of the underlying work (for a Scout package, its CaseId). Uniquely indexed.
        DedupKey     nvarchar(512)     NOT NULL,
        -- Higher is claimed first; ties break on EnqueuedAt.
        Priority     int               NOT NULL CONSTRAINT DF_monitoring_AgentWorkQueue_Priority DEFAULT 0,
        -- queued | leased | done | failed | abandoned
        Status       nvarchar(32)      NOT NULL CONSTRAINT DF_monitoring_AgentWorkQueue_Status DEFAULT N'queued',
        Attempts     int               NOT NULL CONSTRAINT DF_monitoring_AgentWorkQueue_Attempts DEFAULT 0,
        -- Attempts after which the item is abandoned instead of retried: a poison item must stop costing LLM calls.
        MaxAttempts  int               NOT NULL CONSTRAINT DF_monitoring_AgentWorkQueue_MaxAttempts DEFAULT 3,
        -- NULL when not claimed. In the past means the holder died and the item is claimable again.
        LeasedUntil  datetimeoffset(7) NULL,
        LeasedBy     nvarchar(128)     NULL,
        LastError    nvarchar(1024)    NULL,
        EnqueuedAt   datetimeoffset(7) NOT NULL
            CONSTRAINT DF_monitoring_AgentWorkQueue_EnqueuedAt DEFAULT SYSDATETIMEOFFSET(),
        -- When the last attempt ended (done, failed or abandoned). NULL while the item is in flight.
        CompletedAt  datetimeoffset(7) NULL
    );

    -- The dedup guarantee lives here, not only in C#: the Scout re-reads an overlapping window every
    -- few minutes, so re-enqueuing the same case is the expected path and must be a no-op.
    CREATE UNIQUE INDEX UQ_monitoring_AgentWorkQueue_DedupKey ON monitoring.AgentWorkQueue (DedupKey);
    -- Covers the claim predicate and its ordering, so a growing done/abandoned history does not slow
    -- down the one query every consumer runs on every poll.
    CREATE INDEX IX_monitoring_AgentWorkQueue_Claim ON monitoring.AgentWorkQueue (PayloadType, Status, Priority DESC, EnqueuedAt);
    CREATE INDEX IX_monitoring_AgentWorkQueue_Status ON monitoring.AgentWorkQueue (Status, EnqueuedAt DESC);
END
GO

IF OBJECT_ID(N'monitoring.ForensicCase', N'U') IS NULL
BEGIN
    CREATE TABLE monitoring.ForensicCase
    (
        Id                     uniqueidentifier  NOT NULL CONSTRAINT PK_monitoring_ForensicCase PRIMARY KEY,
        InvestigationPackageId uniqueidentifier  NOT NULL,
        -- Which company this investigation belongs to. Copied from the package the case was opened
        -- from, never re-resolved here: the Scout already decided it, and a second opinion could
        -- disagree with the row this case is an investigation OF.
        ProfileKey             nvarchar(128)     NOT NULL
            CONSTRAINT DF_monitoring_ForensicCase_ProfileKey DEFAULT N'',
        -- Copied from the package so a case is identifiable without a join.
        CaseId                 nvarchar(512)     NOT NULL,
        EntityKey              nvarchar(256)     NOT NULL,
        -- NULL when the club does not resolve - carried over verbatim, never guessed here either.
        BrandName              nvarchar(200)     NULL,
        Vertical               nvarchar(64)      NOT NULL,
        Stage                  nvarchar(128)     NOT NULL,
        -- running | escalated | resolved | closed_no_issue | failed
        Status                 nvarchar(32)      NOT NULL CONSTRAINT DF_monitoring_ForensicCase_Status DEFAULT N'running',
        -- preprocess | scope | agents | debate | synthesis | grounding | verdict. NULL once finished.
        Phase                  nvarchar(32)      NULL,
        StartedAt              datetimeoffset(7) NOT NULL
            CONSTRAINT DF_monitoring_ForensicCase_StartedAt DEFAULT SYSDATETIMEOFFSET(),
        CompletedAt            datetimeoffset(7) NULL,
        DurationMs             bigint            NOT NULL CONSTRAINT DF_monitoring_ForensicCase_Duration DEFAULT 0,
        -- One plain-language line. NULL while running, and when the case failed before producing one.
        Verdict                nvarchar(1024)    NULL,
        -- escalate | resolve | close_no_issue | inconclusive
        Decision               nvarchar(32)      NULL,
        -- The probable root cause as its own column rather than buried in SynthesisJson. An
        -- escalated case can legitimately have one: naming the likely cause and being able to
        -- close the case are different claims.
        ProbableRootCause      nvarchar(max)     NULL,
        Confidence             float             NOT NULL CONSTRAINT DF_monitoring_ForensicCase_Confidence DEFAULT 0,
        -- What was missing to close the case, as an actionable request rather than a low score to interpret.
        BlockedBy              nvarchar(1024)    NULL,
        SynthesisJson          nvarchar(max)     NULL,
        BlackboardJson         nvarchar(max)     NULL,
        RunbookJson            nvarchar(max)     NULL,
        GroundingJson          nvarchar(max)     NULL,
        CitationsTotal         int               NOT NULL CONSTRAINT DF_monitoring_ForensicCase_CitTotal DEFAULT 0,
        -- The one quality number a confident-sounding model cannot fake, which is why it is stored, not derived at render time.
        CitationsVerified      int               NOT NULL CONSTRAINT DF_monitoring_ForensicCase_CitVerified DEFAULT 0,
        LlmCalls               int               NOT NULL CONSTRAINT DF_monitoring_ForensicCase_LlmCalls DEFAULT 0,
        TokensIn               int               NOT NULL CONSTRAINT DF_monitoring_ForensicCase_TokensIn DEFAULT 0,
        TokensOut              int               NOT NULL CONSTRAINT DF_monitoring_ForensicCase_TokensOut DEFAULT 0,
        -- red_alert | amber | informational. Decided by a deterministic policy, never by a model.
        AlertLevel             nvarchar(32)      NOT NULL
            CONSTRAINT DF_monitoring_ForensicCase_AlertLevel DEFAULT N'informational',
        -- Why it landed on that level, populated for the downgrades too: "why is this NOT a Red
        -- Alert" is the question an operator actually asks.
        AlertReason            nvarchar(1024)    NULL,
        -- Populated on failure - an investigation that could not run must be visible, never silently "no findings".
        ErrorMessage           nvarchar(1024)    NULL
    );

    -- One package, one investigation. A second case for the same package would double-count every
    -- figure on the card and make "investigations" stop meaning "packages looked at".
    CREATE UNIQUE INDEX UQ_monitoring_ForensicCase_PackageId ON monitoring.ForensicCase (InvestigationPackageId);
    CREATE INDEX IX_monitoring_ForensicCase_Status ON monitoring.ForensicCase (Status, StartedAt DESC);
    CREATE INDEX IX_monitoring_ForensicCase_StartedAt ON monitoring.ForensicCase (StartedAt DESC);
END
GO

-- IF OBJECT_ID(...) IS NULL CREATE TABLE does not touch a table that already exists, so every
-- column added after the first release needs its own guarded ALTER or it never reaches an
-- environment that was provisioned before it was written.
IF COL_LENGTH(N'monitoring.ForensicCase', N'ProbableRootCause') IS NULL
    ALTER TABLE monitoring.ForensicCase ADD ProbableRootCause nvarchar(max) NULL;
GO

IF COL_LENGTH(N'monitoring.ForensicCase', N'AlertLevel') IS NULL
    ALTER TABLE monitoring.ForensicCase ADD AlertLevel nvarchar(32) NOT NULL
        CONSTRAINT DF_monitoring_ForensicCase_AlertLevel DEFAULT N'informational';
GO

IF COL_LENGTH(N'monitoring.ForensicCase', N'AlertReason') IS NULL
    ALTER TABLE monitoring.ForensicCase ADD AlertReason nvarchar(1024) NULL;
GO

-- The team accountable for the failing service. Separate from BrandName: the club says who is
-- AFFECTED, this says who FIXES IT.
IF COL_LENGTH(N'monitoring.ForensicCase', N'OwnerTeam') IS NULL
    ALTER TABLE monitoring.ForensicCase ADD OwnerTeam nvarchar(200) NULL;
GO

IF COL_LENGTH(N'monitoring.ForensicCase', N'OwnerPrimary') IS NULL
    ALTER TABLE monitoring.ForensicCase ADD OwnerPrimary nvarchar(200) NULL;
GO

IF COL_LENGTH(N'monitoring.ForensicCase', N'OwnerBackup') IS NULL
    ALTER TABLE monitoring.ForensicCase ADD OwnerBackup nvarchar(200) NULL;
GO

IF COL_LENGTH(N'monitoring.ForensicCase', N'OwnerResolvedBy') IS NULL
    ALTER TABLE monitoring.ForensicCase ADD OwnerResolvedBy nvarchar(32) NULL;
GO

-- The ids the rest of the portal filters on. The Scout already resolves and stores them on the
-- package; without them here a case cannot be found by the same club filter as journeys,
-- exceptions and the overview.
IF COL_LENGTH(N'monitoring.ForensicCase', N'ClubId') IS NULL
    ALTER TABLE monitoring.ForensicCase ADD ClubId nvarchar(64) NULL;
GO

IF COL_LENGTH(N'monitoring.ForensicCase', N'IcePartnerId') IS NULL
    ALTER TABLE monitoring.ForensicCase ADD IcePartnerId nvarchar(64) NULL;
GO

IF COL_LENGTH(N'monitoring.ForensicCase', N'ClubResolvedBy') IS NULL
    ALTER TABLE monitoring.ForensicCase ADD ClubResolvedBy nvarchar(32) NULL;
GO

-- The club's own display name (brand.ClubInventory.ClubName) - BrandName above is only the chain.
IF COL_LENGTH(N'monitoring.ForensicCase', N'ClubName') IS NULL
    ALTER TABLE monitoring.ForensicCase ADD ClubName nvarchar(200) NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_monitoring_ForensicCase_Club')
    CREATE INDEX IX_monitoring_ForensicCase_Club ON monitoring.ForensicCase (ClubId, StartedAt DESC);
GO

-- Which company the investigation belongs to. The package already carries it (see
-- CreateMonitoringScout.sql); a case is opened FROM a package, so this is a copy, never a second
-- resolution.
--
-- Why the default is N'' and NOT the incumbent company's key: rows written before this column
-- existed were investigated under whatever profile the deployment happened to be configured with,
-- and nobody recorded which. Stamping them 'arrivia' would turn an assumption into a stored fact.
-- N'' is honest: unattributed. The accepted cost is that a per-company filter will not return them.
-- NOT NULL + DEFAULT because the table has rows: ADD ... NOT NULL without a default fails.
IF COL_LENGTH(N'monitoring.ForensicCase', N'ProfileKey') IS NULL
    ALTER TABLE monitoring.ForensicCase ADD ProfileKey nvarchar(128) NOT NULL
        CONSTRAINT DF_monitoring_ForensicCase_ProfileKey DEFAULT N'';
GO

-- Covers the per-company case list and its ordering, which is the one query the Inspector read side
-- runs once a second company exists.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_monitoring_ForensicCase_Profile' AND object_id = OBJECT_ID(N'monitoring.ForensicCase'))
    CREATE INDEX IX_monitoring_ForensicCase_Profile ON monitoring.ForensicCase (ProfileKey, StartedAt DESC);
GO

IF OBJECT_ID(N'monitoring.AgentTurn', N'U') IS NULL
BEGIN
    CREATE TABLE monitoring.AgentTurn
    (
        Id              uniqueidentifier  NOT NULL CONSTRAINT PK_monitoring_AgentTurn PRIMARY KEY,
        ForensicCaseId  uniqueidentifier  NOT NULL,
        -- The specific author ("LogsAgent", "moa:gpt-4o", "Planner"), not the kind.
        AgentRole       nvarchar(128)     NOT NULL,
        -- specialist | moa | planner | grounding
        Kind            nvarchar(32)      NOT NULL,
        -- Debate round. 0 for everything that is not an MOA debate turn.
        Round           int               NOT NULL CONSTRAINT DF_monitoring_AgentTurn_Round DEFAULT 0,
        -- Help hop within this agent's own loop.
        Hop             int               NOT NULL CONSTRAINT DF_monitoring_AgentTurn_Hop DEFAULT 0,
        -- NULL when the turn made no LLM call; a deterministic phase still records a turn so the transcript has no gaps.
        ModelUsed       nvarchar(128)     NULL,
        -- Set when this turn exists because another agent asked a question - model-written, so unbounded.
        InputContext    nvarchar(max)     NULL,
        PromptText      nvarchar(max)     NOT NULL,
        -- Exactly what came back, before any JSON extraction.
        RawResponseText nvarchar(max)     NOT NULL,
        -- final | needs_help | excluded | error
        OutcomeStatus   nvarchar(32)      NOT NULL,
        -- NULL when the response could not be parsed, in which case RawResponseText is all there is.
        OutcomeJson     nvarchar(max)     NULL,
        TokensIn        int               NOT NULL CONSTRAINT DF_monitoring_AgentTurn_TokensIn DEFAULT 0,
        TokensOut       int               NOT NULL CONSTRAINT DF_monitoring_AgentTurn_TokensOut DEFAULT 0,
        DurationMs      bigint            NOT NULL CONSTRAINT DF_monitoring_AgentTurn_Duration DEFAULT 0,
        ErrorMessage    nvarchar(1024)    NULL,
        CreatedAt       datetimeoffset(7) NOT NULL
            CONSTRAINT DF_monitoring_AgentTurn_CreatedAt DEFAULT SYSDATETIMEOFFSET()
    );

    -- The transcript is always read whole and in order for one case, never across cases.
    CREATE INDEX IX_monitoring_AgentTurn_CaseId ON monitoring.AgentTurn (ForensicCaseId, CreatedAt);
END
GO

IF OBJECT_ID(N'monitoring.AgentToolCall', N'U') IS NULL
BEGIN
    CREATE TABLE monitoring.AgentToolCall
    (
        Id             uniqueidentifier  NOT NULL CONSTRAINT PK_monitoring_AgentToolCall PRIMARY KEY,
        AgentTurnId    uniqueidentifier  NOT NULL,
        ToolName       nvarchar(128)     NOT NULL,
        -- Model-generated. Stored verbatim: what the model asked for is evidence, including when it was wrong.
        ArgumentsJson  nvarchar(max)     NOT NULL,
        ResultJson     nvarchar(max)     NOT NULL,
        -- A partial result must never look complete, so truncation is a flag and not just a marker inside the text.
        Truncated      bit               NOT NULL CONSTRAINT DF_monitoring_AgentToolCall_Truncated DEFAULT 0,
        Success        bit               NOT NULL CONSTRAINT DF_monitoring_AgentToolCall_Success DEFAULT 0,
        ErrorMessage   nvarchar(1024)    NULL,
        ResultRowCount int               NOT NULL CONSTRAINT DF_monitoring_AgentToolCall_RowCount DEFAULT 0,
        DurationMs     bigint            NOT NULL CONSTRAINT DF_monitoring_AgentToolCall_Duration DEFAULT 0,
        CreatedAt      datetimeoffset(7) NOT NULL
            CONSTRAINT DF_monitoring_AgentToolCall_CreatedAt DEFAULT SYSDATETIMEOFFSET()
    );

    -- Also the join path for "every tool call of this case" (AgentTurn -> AgentToolCall).
    CREATE INDEX IX_monitoring_AgentToolCall_TurnId ON monitoring.AgentToolCall (AgentTurnId, CreatedAt);
END
GO

-- Columns added after the tables first shipped go here, each with its own idempotent ALTER:
-- "IF OBJECT_ID ... IS NULL CREATE TABLE" above does nothing on a database where the table already
-- exists, so a column added inside the CREATE silently never reaches an existing environment.
-- (none yet)

-- ---------------------------------------------------------------------------------------------
-- Work queue
-- ---------------------------------------------------------------------------------------------

-- Check-and-insert in ONE statement. A separate "IF NOT EXISTS ... INSERT" would leave a window in
-- which two producers both see nothing and both insert; here the UPDLOCK/HOLDLOCK range lock taken
-- by the NOT EXISTS probe is held for the whole statement, so the second producer waits and then
-- sees the row. The unique index is still the last line of defence, and a violation from it is
-- translated into "0 rows inserted" rather than an exception: a duplicate is the expected outcome of
-- re-reading an overlapping window, not an error worth failing a cycle over.
CREATE OR ALTER PROCEDURE monitoring.usp_EnqueueAgentWork
    @Id uniqueidentifier, @SourceMonkey nvarchar(64), @PayloadType nvarchar(64),
    @PayloadId nvarchar(128), @DedupKey nvarchar(512), @Priority int, @Status nvarchar(32),
    @MaxAttempts int, @EnqueuedAt datetimeoffset(7)
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @Inserted int = 0;

    BEGIN TRY
        INSERT INTO monitoring.AgentWorkQueue
            (Id, SourceMonkey, PayloadType, PayloadId, DedupKey, Priority, Status, Attempts, MaxAttempts, EnqueuedAt)
        SELECT @Id, @SourceMonkey, @PayloadType, @PayloadId, @DedupKey, @Priority,
               ISNULL(@Status, N'queued'), 0, @MaxAttempts, ISNULL(@EnqueuedAt, SYSDATETIMEOFFSET())
        WHERE NOT EXISTS (
            SELECT 1 FROM monitoring.AgentWorkQueue WITH (UPDLOCK, HOLDLOCK) WHERE DedupKey = @DedupKey);

        SET @Inserted = @@ROWCOUNT;
    END TRY
    BEGIN CATCH
        -- 2601/2627: lost the race on UQ_monitoring_AgentWorkQueue_DedupKey. Same meaning as the
        -- NOT EXISTS above returning nothing.
        IF ERROR_NUMBER() IN (2601, 2627)
            SET @Inserted = 0;
        ELSE
            THROW;
    END CATCH

    SELECT @Inserted;
END
GO

-- THE claim. Deliberately a single UPDATE over an ordered, locked TOP (1) - never SELECT then
-- UPDATE. Two consumers polling this table with a read followed by a separate write would both see
-- the same highest-priority row and both run the investigation, which here means paying twice for
-- the same LLM calls and writing two cases for one package.
--
-- Why this shape and not a bare "UPDATE TOP (1) ... WHERE ...": UPDATE TOP has no ORDER BY, so it
-- would claim an arbitrary eligible row rather than the most urgent one. The CTE is the only way to
-- get TOP + ORDER BY + the update in one statement.
--
-- Hints, each load-bearing:
--   UPDLOCK  - take the update lock at read time, so the row cannot be claimed between being chosen and being written.
--   ROWLOCK  - stay at row granularity; a page lock here would serialise unrelated consumers.
--   READPAST - skip rows another consumer is already claiming instead of blocking behind them.
--
-- Eligible means: never attempted, failed with attempts left, or leased to someone who stopped
-- renewing (i.e. died). The attempts guard is repeated on the expired-lease branch on purpose - a
-- consumer that keeps crashing mid-investigation never reaches usp_FailAgentWork, so without it the
-- item would be re-leased forever and keep costing LLM calls, which is exactly what MaxAttempts
-- exists to prevent.
CREATE OR ALTER PROCEDURE monitoring.usp_TryLeaseAgentWork
    @PayloadType nvarchar(64), @LeasedBy nvarchar(128), @LeasedUntil datetimeoffset(7)
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @Now datetimeoffset(7) = SYSDATETIMEOFFSET();

    WITH claim AS
    (
        SELECT TOP (1) *
        FROM monitoring.AgentWorkQueue WITH (READPAST, ROWLOCK, UPDLOCK)
        WHERE PayloadType = @PayloadType
          AND (
                Status = N'queued'
             OR (Status = N'failed' AND Attempts < MaxAttempts)
             OR (Status = N'leased' AND LeasedUntil IS NOT NULL AND LeasedUntil < @Now AND Attempts < MaxAttempts)
          )
        ORDER BY Priority DESC, EnqueuedAt ASC
    )
    UPDATE claim
    SET Status      = N'leased',
        LeasedUntil = @LeasedUntil,
        LeasedBy    = @LeasedBy,
        Attempts    = Attempts + 1,
        -- The previous attempt's end time no longer describes this item: it is in flight again.
        CompletedAt = NULL
    OUTPUT inserted.Id, inserted.SourceMonkey, inserted.PayloadType, inserted.PayloadId,
           inserted.DedupKey, inserted.Priority, inserted.Status, inserted.Attempts,
           inserted.MaxAttempts, inserted.LeasedUntil, inserted.LeasedBy, inserted.LastError,
           inserted.EnqueuedAt, inserted.CompletedAt;
END
GO

-- @LeasedBy is a fence, not decoration: a consumer that stalled past its lease has already had the
-- item re-claimed by someone else, and must not be able to come back and overwrite the state of
-- whoever replaced it. Matching on the lease holder makes that late write a no-op.
CREATE OR ALTER PROCEDURE monitoring.usp_CompleteAgentWork
    @Id uniqueidentifier, @LeasedBy nvarchar(128) = NULL, @CompletedAt datetimeoffset(7) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE monitoring.AgentWorkQueue
    SET Status      = N'done',
        CompletedAt = ISNULL(@CompletedAt, SYSDATETIMEOFFSET()),
        LeasedUntil = NULL,
        LeasedBy    = NULL
    WHERE Id = @Id
      AND (@LeasedBy IS NULL OR LeasedBy = @LeasedBy);
END
GO

-- LastError is overwritten on every attempt but never cleared, so a repeating failure stays visible
-- instead of only the last one. CompletedAt is stamped on both outcomes because it means "when the
-- last attempt ended" - without it, a failed-and-retryable item would have no timestamp at all and
-- the card's FailedToday counter could never be computed. The claim above resets it to NULL.
CREATE OR ALTER PROCEDURE monitoring.usp_FailAgentWork
    @Id uniqueidentifier, @Error nvarchar(1024), @LeasedBy nvarchar(128) = NULL, @CompletedAt datetimeoffset(7) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE monitoring.AgentWorkQueue
    SET LastError   = @Error,
        -- Attempts was already incremented when the lease was taken, so ">=" is the out-of-attempts test.
        Status      = CASE WHEN Attempts >= MaxAttempts THEN N'abandoned' ELSE N'failed' END,
        CompletedAt = ISNULL(@CompletedAt, SYSDATETIMEOFFSET()),
        LeasedUntil = NULL,
        LeasedBy    = NULL
    WHERE Id = @Id
      AND (@LeasedBy IS NULL OR LeasedBy = @LeasedBy);
END
GO

-- Same predicate and ordering as the claim on purpose: this is a preview of what would be picked up
-- next, so it must not disagree with what actually gets picked up.
CREATE OR ALTER PROCEDURE monitoring.usp_GetQueuedAgentWork
    @PayloadType nvarchar(64), @Top int
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @Now datetimeoffset(7) = SYSDATETIMEOFFSET();

    SELECT TOP (@Top) Id, SourceMonkey, PayloadType, PayloadId, DedupKey, Priority, Status,
           Attempts, MaxAttempts, LeasedUntil, LeasedBy, LastError, EnqueuedAt, CompletedAt
    FROM monitoring.AgentWorkQueue
    WHERE PayloadType = @PayloadType
      AND (
            Status = N'queued'
         OR (Status = N'failed' AND Attempts < MaxAttempts)
         OR (Status = N'leased' AND LeasedUntil IS NOT NULL AND LeasedUntil < @Now AND Attempts < MaxAttempts)
      )
    ORDER BY Priority DESC, EnqueuedAt ASC;
END
GO

-- Backlog counters for the card: a heartbeat of past cycles cannot answer "is it falling behind?".
-- An expired lease is counted as queued and not as leased, because nobody is working on it - showing
-- it as in-flight would hide a dead consumer behind a number that looks busy.
CREATE OR ALTER PROCEDURE monitoring.usp_GetAgentWorkQueueStats
    @PayloadType nvarchar(64)
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @Now datetimeoffset(7) = SYSDATETIMEOFFSET();
    DECLARE @Today date = CAST(@Now AS date);

    SELECT
        Queued = ISNULL(SUM(CASE
                    WHEN Status = N'queued' THEN 1
                    WHEN Status = N'failed' AND Attempts < MaxAttempts THEN 1
                    WHEN Status = N'leased' AND LeasedUntil IS NOT NULL AND LeasedUntil < @Now AND Attempts < MaxAttempts THEN 1
                    ELSE 0 END), 0),
        Leased = ISNULL(SUM(CASE
                    WHEN Status = N'leased' AND LeasedUntil IS NOT NULL AND LeasedUntil >= @Now THEN 1
                    ELSE 0 END), 0),
        DoneToday = ISNULL(SUM(CASE
                    WHEN Status = N'done' AND CAST(CompletedAt AS date) = @Today THEN 1
                    ELSE 0 END), 0),
        FailedToday = ISNULL(SUM(CASE
                    WHEN Status = N'failed' AND CAST(CompletedAt AS date) = @Today THEN 1
                    ELSE 0 END), 0),
        AbandonedTotal = ISNULL(SUM(CASE WHEN Status = N'abandoned' THEN 1 ELSE 0 END), 0),
        DiscardedTotal = ISNULL(SUM(CASE WHEN Status = N'discarded' THEN 1 ELSE 0 END), 0),
        OldestQueuedAt = MIN(CASE
                    WHEN Status = N'queued' THEN EnqueuedAt
                    WHEN Status = N'failed' AND Attempts < MaxAttempts THEN EnqueuedAt
                    WHEN Status = N'leased' AND LeasedUntil IS NOT NULL AND LeasedUntil < @Now AND Attempts < MaxAttempts THEN EnqueuedAt
                    END)
    FROM monitoring.AgentWorkQueue
    WHERE PayloadType = @PayloadType;
END
GO

-- Human cleanup of the backlog. Never a DELETE: the row stays as 'discarded' with who asked and why
-- in LastError, so the day's counters still add up and "where did those twelve go?" has an answer.
--
-- The eligibility predicate is the claim's, so this can only remove work that was genuinely waiting.
-- An item with a live lease is excluded on purpose: a worker is mid-investigation on it, the LLM
-- calls are already being paid for, and flipping the row would not stop any of that - it would only
-- make the queue disagree with reality.
--
-- @Ids is a comma-separated list of GUIDs, or NULL to discard everything waiting for the payload
-- type. STRING_SPLIT keeps this a single set-based statement rather than a round trip per item.
CREATE OR ALTER PROCEDURE monitoring.usp_DiscardAgentWork
    @PayloadType nvarchar(64),
    @Ids nvarchar(max) = NULL,
    @Reason nvarchar(1024),
    @DiscardedBy nvarchar(128)
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @Now datetimeoffset(7) = SYSDATETIMEOFFSET();

    UPDATE monitoring.AgentWorkQueue
    SET Status      = N'discarded',
        LastError   = LEFT(CONCAT(N'Discarded by ', @DiscardedBy, N': ', @Reason), 1024),
        CompletedAt = @Now,
        LeasedUntil = NULL,
        LeasedBy    = NULL
    WHERE PayloadType = @PayloadType
      AND (
            Status = N'queued'
         OR (Status = N'failed' AND Attempts < MaxAttempts)
         OR (Status = N'leased' AND LeasedUntil IS NOT NULL AND LeasedUntil < @Now AND Attempts < MaxAttempts)
      )
      AND (@Ids IS NULL OR Id IN (SELECT TRY_CAST(LTRIM(RTRIM(value)) AS uniqueidentifier) FROM STRING_SPLIT(@Ids, ',')));

    SELECT @@ROWCOUNT;
END
GO

-- ---------------------------------------------------------------------------------------------
-- Forensic cases
-- ---------------------------------------------------------------------------------------------

-- Plain INSERT, not idempotent: UQ_monitoring_ForensicCase_PackageId must reject a second case for
-- the same package loudly. Silently returning the existing case would let a re-leased item believe
-- it opened a fresh investigation and then overwrite the first one's verdict.
-- @ProfileKey has no default on purpose, the same rule usp_AddInvestigationPackage states: an
-- unattributed case should fail loudly at the call site, not be quietly filed under N''.
CREATE OR ALTER PROCEDURE monitoring.usp_AddForensicCase
    @Id uniqueidentifier, @InvestigationPackageId uniqueidentifier, @ProfileKey nvarchar(128), @CaseId nvarchar(512),
    @EntityKey nvarchar(256), @BrandName nvarchar(200), @ClubName nvarchar(200), @Vertical nvarchar(64), @Stage nvarchar(128),
    @Status nvarchar(32), @Phase nvarchar(32), @StartedAt datetimeoffset(7),
    @CompletedAt datetimeoffset(7), @DurationMs bigint, @Verdict nvarchar(1024),
    @Decision nvarchar(32), @ProbableRootCause nvarchar(max), @Confidence float,
    @BlockedBy nvarchar(1024),
    @SynthesisJson nvarchar(max), @BlackboardJson nvarchar(max), @RunbookJson nvarchar(max),
    @GroundingJson nvarchar(max), @CitationsTotal int, @CitationsVerified int, @LlmCalls int,
    @TokensIn int, @TokensOut int, @AlertLevel nvarchar(32), @AlertReason nvarchar(1024),
    @OwnerTeam nvarchar(200), @OwnerPrimary nvarchar(200), @OwnerBackup nvarchar(200),
    @OwnerResolvedBy nvarchar(32),
    @ClubId nvarchar(64), @IcePartnerId nvarchar(64), @ClubResolvedBy nvarchar(32),
    @ErrorMessage nvarchar(1024)
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO monitoring.ForensicCase
        (Id, InvestigationPackageId, ProfileKey, CaseId, EntityKey, BrandName, Vertical, Stage, Status, Phase,
         StartedAt, CompletedAt, DurationMs, Verdict, Decision, ProbableRootCause, Confidence, BlockedBy,
         SynthesisJson, BlackboardJson, RunbookJson, GroundingJson,
         CitationsTotal, CitationsVerified, LlmCalls, TokensIn, TokensOut,
         AlertLevel, AlertReason, OwnerTeam, OwnerPrimary, OwnerBackup, OwnerResolvedBy,
         ClubId, IcePartnerId, ClubResolvedBy, ErrorMessage)
    VALUES
        (@Id, @InvestigationPackageId, @ProfileKey, @CaseId, @EntityKey, @BrandName, @Vertical, @Stage, @Status, @Phase,
         @StartedAt, @CompletedAt, @DurationMs, @Verdict, @Decision, @ProbableRootCause, @Confidence, @BlockedBy,
         @SynthesisJson, @BlackboardJson, @RunbookJson, @GroundingJson,
         @CitationsTotal, @CitationsVerified, @LlmCalls, @TokensIn, @TokensOut,
         @AlertLevel, @AlertReason, @OwnerTeam, @OwnerPrimary, @OwnerBackup, @OwnerResolvedBy,
         @ClubId, @IcePartnerId, @ClubResolvedBy, @ErrorMessage);
END
GO

-- Only the fields an investigation can change. Id/InvestigationPackageId/CaseId/StartedAt and the
-- entity identity are set once at Add: a case that moves to another package is a different case.
CREATE OR ALTER PROCEDURE monitoring.usp_UpdateForensicCase
    @Id uniqueidentifier, @Status nvarchar(32), @Phase nvarchar(32),
    @CompletedAt datetimeoffset(7), @DurationMs bigint, @Verdict nvarchar(1024),
    @Decision nvarchar(32), @ProbableRootCause nvarchar(max), @Confidence float,
    @BlockedBy nvarchar(1024),
    @SynthesisJson nvarchar(max), @BlackboardJson nvarchar(max), @RunbookJson nvarchar(max),
    @GroundingJson nvarchar(max), @CitationsTotal int, @CitationsVerified int, @LlmCalls int,
    @TokensIn int, @TokensOut int, @AlertLevel nvarchar(32), @AlertReason nvarchar(1024),
    @OwnerTeam nvarchar(200), @OwnerPrimary nvarchar(200), @OwnerBackup nvarchar(200),
    @OwnerResolvedBy nvarchar(32),
    @ErrorMessage nvarchar(1024)
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE monitoring.ForensicCase
    SET Status            = @Status,
        Phase             = @Phase,
        CompletedAt       = @CompletedAt,
        DurationMs        = @DurationMs,
        Verdict           = @Verdict,
        Decision          = @Decision,
        ProbableRootCause = @ProbableRootCause,
        Confidence        = @Confidence,
        BlockedBy         = @BlockedBy,
        SynthesisJson     = @SynthesisJson,
        BlackboardJson    = @BlackboardJson,
        RunbookJson       = @RunbookJson,
        GroundingJson     = @GroundingJson,
        CitationsTotal    = @CitationsTotal,
        CitationsVerified = @CitationsVerified,
        LlmCalls          = @LlmCalls,
        TokensIn          = @TokensIn,
        TokensOut         = @TokensOut,
        AlertLevel        = @AlertLevel,
        AlertReason       = @AlertReason,
        OwnerTeam         = @OwnerTeam,
        OwnerPrimary      = @OwnerPrimary,
        OwnerBackup       = @OwnerBackup,
        OwnerResolvedBy   = @OwnerResolvedBy,
        ErrorMessage      = @ErrorMessage
    WHERE Id = @Id;
END
GO

-- Deliberately narrow: called once per phase purely to publish progress, so it touches one column
-- and cannot clobber a result written concurrently. Guarded on CompletedAt so a late progress write
-- cannot resurrect a finished case into an in-flight-looking state.
CREATE OR ALTER PROCEDURE monitoring.usp_UpdateForensicCasePhase
    @Id uniqueidentifier, @Phase nvarchar(32)
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE monitoring.ForensicCase
    SET Phase = @Phase
    WHERE Id = @Id AND CompletedAt IS NULL;
END
GO

-- Column order is load-bearing: SqlForensicCaseRepository.ForensicCaseRow is a positional record,
-- and Dapper matches it to this list by position AND name with an exact count. The three case
-- SELECTs below must therefore stay identical to each other, ProfileKey included.
CREATE OR ALTER PROCEDURE monitoring.usp_GetForensicCaseById
    @Id uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    SELECT TOP (1) Id, InvestigationPackageId, ProfileKey, CaseId, EntityKey, BrandName, ClubName, Vertical, Stage,
           Status, Phase, StartedAt, CompletedAt, DurationMs, Verdict, Decision, ProbableRootCause,
           Confidence, BlockedBy, SynthesisJson, BlackboardJson, RunbookJson, GroundingJson,
           CitationsTotal, CitationsVerified, LlmCalls, TokensIn, TokensOut,
           AlertLevel, AlertReason, OwnerTeam, OwnerPrimary, OwnerBackup, OwnerResolvedBy,
           ClubId, IcePartnerId, ClubResolvedBy, ErrorMessage
    FROM monitoring.ForensicCase
    WHERE Id = @Id;
END
GO

CREATE OR ALTER PROCEDURE monitoring.usp_GetForensicCaseByPackageId
    @InvestigationPackageId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    SELECT TOP (1) Id, InvestigationPackageId, ProfileKey, CaseId, EntityKey, BrandName, ClubName, Vertical, Stage,
           Status, Phase, StartedAt, CompletedAt, DurationMs, Verdict, Decision, ProbableRootCause,
           Confidence, BlockedBy, SynthesisJson, BlackboardJson, RunbookJson, GroundingJson,
           CitationsTotal, CitationsVerified, LlmCalls, TokensIn, TokensOut,
           AlertLevel, AlertReason, OwnerTeam, OwnerPrimary, OwnerBackup, OwnerResolvedBy,
           ClubId, IcePartnerId, ClubResolvedBy, ErrorMessage
    FROM monitoring.ForensicCase
    WHERE InvestigationPackageId = @InvestigationPackageId;
END
GO

-- @ProfileKey defaults to NULL and not to N'' because the two mean different things here: NULL is
-- "every company" (the pre-multi-company behaviour, kept so existing callers are unchanged), while
-- N'' is a real stored value - the cases written before the column existed, which belong to nobody.
CREATE OR ALTER PROCEDURE monitoring.usp_GetRecentForensicCases
    @Top int, @Vertical nvarchar(64) = NULL, @ClubId nvarchar(64) = NULL, @ProfileKey nvarchar(128) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SELECT TOP (@Top) Id, InvestigationPackageId, ProfileKey, CaseId, EntityKey, BrandName, ClubName, Vertical, Stage,
           Status, Phase, StartedAt, CompletedAt, DurationMs, Verdict, Decision, ProbableRootCause,
           Confidence, BlockedBy, SynthesisJson, BlackboardJson, RunbookJson, GroundingJson,
           CitationsTotal, CitationsVerified, LlmCalls, TokensIn, TokensOut,
           AlertLevel, AlertReason, OwnerTeam, OwnerPrimary, OwnerBackup, OwnerResolvedBy,
           ClubId, IcePartnerId, ClubResolvedBy, ErrorMessage
    FROM monitoring.ForensicCase
    -- The board writes "Hotels" where the filter sends "hotels"; matched on the de-pluralised form
    -- for the same reason the resolver does.
    WHERE (@Vertical IS NULL OR LOWER(RTRIM(Vertical)) = LOWER(@Vertical)
           OR LOWER(RTRIM(Vertical)) = LOWER(@Vertical) + N's'
           OR LOWER(RTRIM(Vertical)) + N's' = LOWER(@Vertical))
      -- Either id identifies the club: hotels/cars carry ClubId, cruises/air carry the partner id.
      AND (@ClubId IS NULL OR ClubId = @ClubId OR IcePartnerId = @ClubId)
      AND (@ProfileKey IS NULL OR ProfileKey = @ProfileKey)
    ORDER BY StartedAt DESC;
END
GO

-- Cumulative proof of work. Investigations counts every case ever opened, including the running and
-- the failed ones, so the outcome counters summing to less than it is the honest reading - a case
-- that broke must not quietly disappear from the denominator the verification rate is read against.
--
-- @ProfileKey NULL means every company, for the same reason as in usp_GetRecentForensicCases: a
-- card that silently reported one company's numbers under no filter would be worse than wrong.
CREATE OR ALTER PROCEDURE monitoring.usp_GetInspectorStats
    @ProfileKey nvarchar(128) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @Today date = CAST(SYSDATETIMEOFFSET() AS date);

    SELECT
        Investigations      = COUNT(*),
        Resolved            = ISNULL(SUM(CASE WHEN Status = N'resolved' THEN 1 ELSE 0 END), 0),
        Escalated           = ISNULL(SUM(CASE WHEN Status = N'escalated' THEN 1 ELSE 0 END), 0),
        ClosedNoIssue       = ISNULL(SUM(CASE WHEN Status = N'closed_no_issue' THEN 1 ELSE 0 END), 0),
        Failed              = ISNULL(SUM(CASE WHEN Status = N'failed' THEN 1 ELSE 0 END), 0),
        CitationsTotal      = ISNULL(SUM(CitationsTotal), 0),
        CitationsVerified   = ISNULL(SUM(CitationsVerified), 0),
        LlmCalls            = ISNULL(SUM(LlmCalls), 0),
        TokensIn            = ISNULL(SUM(CAST(TokensIn AS bigint)), 0),
        TokensOut           = ISNULL(SUM(CAST(TokensOut AS bigint)), 0),
        InvestigationsToday = ISNULL(SUM(CASE WHEN CAST(StartedAt AS date) = @Today THEN 1 ELSE 0 END), 0),
        FirstCaseAt         = MIN(StartedAt),
        LastCaseAt          = MAX(StartedAt)
    FROM monitoring.ForensicCase
    WHERE (@ProfileKey IS NULL OR ProfileKey = @ProfileKey);
END
GO

-- ---------------------------------------------------------------------------------------------
-- Transcript
-- ---------------------------------------------------------------------------------------------

CREATE OR ALTER PROCEDURE monitoring.usp_AddAgentTurn
    @Id uniqueidentifier, @ForensicCaseId uniqueidentifier, @AgentRole nvarchar(128),
    @Kind nvarchar(32), @Round int, @Hop int, @ModelUsed nvarchar(128),
    @InputContext nvarchar(max), @PromptText nvarchar(max), @RawResponseText nvarchar(max),
    @OutcomeStatus nvarchar(32), @OutcomeJson nvarchar(max), @TokensIn int, @TokensOut int,
    @DurationMs bigint, @ErrorMessage nvarchar(1024), @CreatedAt datetimeoffset(7)
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO monitoring.AgentTurn
        (Id, ForensicCaseId, AgentRole, Kind, Round, Hop, ModelUsed, InputContext, PromptText,
         RawResponseText, OutcomeStatus, OutcomeJson, TokensIn, TokensOut, DurationMs, ErrorMessage, CreatedAt)
    VALUES
        (@Id, @ForensicCaseId, @AgentRole, @Kind, @Round, @Hop, @ModelUsed, @InputContext, @PromptText,
         @RawResponseText, @OutcomeStatus, @OutcomeJson, @TokensIn, @TokensOut, @DurationMs, @ErrorMessage,
         ISNULL(@CreatedAt, SYSDATETIMEOFFSET()));
END
GO

CREATE OR ALTER PROCEDURE monitoring.usp_AddAgentToolCall
    @Id uniqueidentifier, @AgentTurnId uniqueidentifier, @ToolName nvarchar(128),
    @ArgumentsJson nvarchar(max), @ResultJson nvarchar(max), @Truncated bit, @Success bit,
    @ErrorMessage nvarchar(1024), @ResultRowCount int, @DurationMs bigint, @CreatedAt datetimeoffset(7)
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO monitoring.AgentToolCall
        (Id, AgentTurnId, ToolName, ArgumentsJson, ResultJson, Truncated, Success, ErrorMessage,
         ResultRowCount, DurationMs, CreatedAt)
    VALUES
        (@Id, @AgentTurnId, @ToolName, @ArgumentsJson, @ResultJson, @Truncated, @Success, @ErrorMessage,
         @ResultRowCount, @DurationMs, ISNULL(@CreatedAt, SYSDATETIMEOFFSET()));
END
GO

-- Chronological, because the transcript is a narrative: out of order it stops being readable as
-- "what the team did" and becomes a bag of rows.
CREATE OR ALTER PROCEDURE monitoring.usp_GetAgentTurns
    @ForensicCaseId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    SELECT Id, ForensicCaseId, AgentRole, Kind, Round, Hop, ModelUsed, InputContext, PromptText,
           RawResponseText, OutcomeStatus, OutcomeJson, TokensIn, TokensOut, DurationMs, ErrorMessage, CreatedAt
    FROM monitoring.AgentTurn
    WHERE ForensicCaseId = @ForensicCaseId
    ORDER BY CreatedAt ASC, Round ASC, Hop ASC;
END
GO

-- Tool calls hang off a turn, not off the case, so the case filter is a join. Returns the tool call
-- columns only: the caller already has the turns and correlates on AgentTurnId.
CREATE OR ALTER PROCEDURE monitoring.usp_GetAgentToolCalls
    @ForensicCaseId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    SELECT tc.Id, tc.AgentTurnId, tc.ToolName, tc.ArgumentsJson, tc.ResultJson, tc.Truncated,
           tc.Success, tc.ErrorMessage, tc.ResultRowCount, tc.DurationMs, tc.CreatedAt
    FROM monitoring.AgentToolCall tc
    INNER JOIN monitoring.AgentTurn t ON t.Id = tc.AgentTurnId
    WHERE t.ForensicCaseId = @ForensicCaseId
    ORDER BY tc.CreatedAt ASC;
END
GO
