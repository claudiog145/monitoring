-- Durable pause switch for any agent in the Simian Army. Grain: one row per MonkeyKey in
-- monitoring.AgentPauseSetting (the current state); AgentPauseSettingAudit is append-only
-- history, never updated/deleted - same shape as monitoring.LatencySpikeSetting(+Audit).
--
-- Why a table at all: "Pause Agent" in the portal used to flip a field in the browser's own
-- state object, so the toast said "paused" while the timer trigger kept running, kept spending
-- LLM calls and kept reporting status "active" through /api/monkeys. The pause has to outlive
-- the browser tab AND the Function host restart, which is what makes it a row and not a flag.
--
-- Deliberately NO CHECK constraint on MonkeyKey (unlike LatencySpikeSetting's domain list): the
-- roster has grown five times in a year, and a constraint here would mean a schema migration
-- every time an agent is added. The known-key validation lives in PauseMonkeyFunction, which is
-- the only writer and already has to 404 an unknown id.

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'monitoring')
    EXEC(N'CREATE SCHEMA monitoring');
GO

IF OBJECT_ID(N'monitoring.AgentPauseSetting', N'U') IS NULL
BEGIN
    CREATE TABLE monitoring.AgentPauseSetting
    (
        -- MonkeyKey as used everywhere else in the system (JourneyMonitor.Core/Domain/Monitoring.cs).
        MonkeyKey  nvarchar(64)      NOT NULL
            CONSTRAINT PK_monitoring_AgentPauseSetting PRIMARY KEY,
        IsPaused   bit               NOT NULL
            CONSTRAINT DF_monitoring_AgentPauseSetting_IsPaused DEFAULT 0,
        UpdatedAt  datetimeoffset(7) NOT NULL
            CONSTRAINT DF_monitoring_AgentPauseSetting_UpdatedAt DEFAULT SYSDATETIMEOFFSET(),
        UpdatedBy  nvarchar(128)     NOT NULL
            CONSTRAINT DF_monitoring_AgentPauseSetting_UpdatedBy DEFAULT N''
    );
END
GO

IF OBJECT_ID(N'monitoring.AgentPauseSettingAudit', N'U') IS NULL
BEGIN
    CREATE TABLE monitoring.AgentPauseSettingAudit
    (
        AuditId    bigint            NOT NULL IDENTITY(1, 1)
            CONSTRAINT PK_monitoring_AgentPauseSettingAudit PRIMARY KEY,
        MonkeyKey  nvarchar(64)      NOT NULL,
        IsPaused   bit               NOT NULL,
        ChangedAt  datetimeoffset(7) NOT NULL
            CONSTRAINT DF_monitoring_AgentPauseSettingAudit_ChangedAt DEFAULT SYSDATETIMEOFFSET(),
        ChangedBy  nvarchar(128)     NOT NULL
            CONSTRAINT DF_monitoring_AgentPauseSettingAudit_ChangedBy DEFAULT N''
    );

    -- "Who silenced this agent, and when?" is the only question this history is read for.
    CREATE INDEX IX_monitoring_AgentPauseSettingAudit_Read
        ON monitoring.AgentPauseSettingAudit (MonkeyKey, ChangedAt DESC);
END
GO

CREATE OR ALTER PROCEDURE monitoring.usp_GetAllAgentPauses
AS
BEGIN
    SET NOCOUNT ON;

    -- Agents never paused are absent, not rows with IsPaused = 0: the resolver treats "no row"
    -- and "row saying false" identically, and an absent row is the honest representation of
    -- "nobody has ever touched this agent's switch".
    SELECT MonkeyKey, IsPaused, UpdatedAt, UpdatedBy
    FROM monitoring.AgentPauseSetting
    ORDER BY MonkeyKey;
END
GO

CREATE OR ALTER PROCEDURE monitoring.usp_GetAgentPause
    @MonkeyKey nvarchar(64)
AS
BEGIN
    SET NOCOUNT ON;

    SELECT MonkeyKey, IsPaused, UpdatedAt, UpdatedBy
    FROM monitoring.AgentPauseSetting
    WHERE MonkeyKey = @MonkeyKey;
END
GO

CREATE OR ALTER PROCEDURE monitoring.usp_UpsertAgentPause
    @MonkeyKey nvarchar(64),
    @IsPaused  bit,
    @ChangedBy nvarchar(128)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now datetimeoffset(7) = SYSDATETIMEOFFSET();

    BEGIN TRANSACTION;

    -- HOLDLOCK (serializable) on the MERGE target: without it, two concurrent pause clicks can
    -- both miss the row in the match step and both INSERT, failing on the primary key. Same
    -- pattern as monitoring.usp_UpsertLatencySpikeSetting.
    MERGE monitoring.AgentPauseSetting WITH (HOLDLOCK) AS target
    USING (SELECT @MonkeyKey AS MonkeyKey) AS incoming
        ON target.MonkeyKey = incoming.MonkeyKey
    WHEN MATCHED THEN
        UPDATE SET
            IsPaused = @IsPaused,
            UpdatedAt = @Now,
            UpdatedBy = @ChangedBy
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (MonkeyKey, IsPaused, UpdatedAt, UpdatedBy)
        VALUES (@MonkeyKey, @IsPaused, @Now, @ChangedBy);

    -- Same transaction as the upsert: a state change that is not in the history would make the
    -- history actively misleading rather than merely incomplete.
    INSERT INTO monitoring.AgentPauseSettingAudit (MonkeyKey, IsPaused, ChangedAt, ChangedBy)
    VALUES (@MonkeyKey, @IsPaused, @Now, @ChangedBy);

    COMMIT TRANSACTION;

    SELECT MonkeyKey, IsPaused, UpdatedAt, UpdatedBy
    FROM monitoring.AgentPauseSetting
    WHERE MonkeyKey = @MonkeyKey;
END
GO
