-- Per-agent spend limits for the agents that call a model. Grain: one row per MonkeyKey in
-- monitoring.AgentBudgetSetting (current state); AgentBudgetSettingAudit is append-only history,
-- never updated/deleted - same shape as monitoring.AgentPauseSetting(+Audit).
--
-- Why this exists: measured over 32 real investigations, the MOA debate is 64% of every token
-- this system spends, and the rounds after the independent round 0 are 43% of the total on their
-- own. Those dials lived only in environment variables, so changing them meant a redeploy - and
-- the one time an agent ran unattended it burned 1.6M input tokens in 35 minutes and hit the
-- provider's rate limit. A spend ceiling that needs a deploy to move is not a control.
--
-- Deliberately NOT here:
-- - The model and provider. Those already live in monitoring.LlmClientSetting, per agent. A second
--   place to set the same thing is a second place for them to disagree.
-- - An on/off switch. monitoring.AgentPauseSetting is already exactly that.
-- - MaxTokensPerCall / MaxToolRounds. Those are read inside the specialists, which this does not
--   reach yet. A column nobody applies is a dial that lies to whoever moves it.
--
-- EVERY value column is NULLable and NULL means "inherit the configured default". That is what
-- makes a row safe to create: writing one field does not silently pin the other five to whatever
-- the defaults happened to be on the day the row was written. Clearing a field back to NULL is
-- how you revert to configuration.
--
-- No CHECK constraint on MonkeyKey, for the same reason AgentPauseSetting has none: the roster
-- grows, and a constraint here would mean a schema migration each time. The known-key validation
-- lives in the write endpoint, which already has to 404 an unknown agent.

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'monitoring')
    EXEC(N'CREATE SCHEMA monitoring');
GO

IF OBJECT_ID(N'monitoring.AgentBudgetSetting', N'U') IS NULL
BEGIN
    CREATE TABLE monitoring.AgentBudgetSetting
    (
        -- MonkeyKey as used everywhere else (JourneyMonitor.Core/Domain/Monitoring.cs).
        MonkeyKey                       nvarchar(64)      NOT NULL
            CONSTRAINT PK_monitoring_AgentBudgetSetting PRIMARY KEY,

        -- Applies to every agent that calls a model.
        MaxRunsPerDay                   int               NULL,

        -- Inspector only. Left NULL on every other row; the resolver only reads them for the
        -- Inspector, so a stray value elsewhere is inert rather than surprising.
        DebateModels                    nvarchar(400)     NULL,
        DebateRounds                    int               NULL,
        MaxHelpHops                     int               NULL,
        SkipDebateWhenContributorsBelow int               NULL,

        UpdatedAt                       datetimeoffset(7) NOT NULL
            CONSTRAINT DF_monitoring_AgentBudgetSetting_UpdatedAt DEFAULT SYSDATETIMEOFFSET(),
        UpdatedBy                       nvarchar(128)     NOT NULL
            CONSTRAINT DF_monitoring_AgentBudgetSetting_UpdatedBy DEFAULT N''
    );
END
GO

IF OBJECT_ID(N'monitoring.AgentBudgetSettingAudit', N'U') IS NULL
BEGIN
    CREATE TABLE monitoring.AgentBudgetSettingAudit
    (
        AuditId                         bigint            NOT NULL IDENTITY(1, 1)
            CONSTRAINT PK_monitoring_AgentBudgetSettingAudit PRIMARY KEY,
        MonkeyKey                       nvarchar(64)      NOT NULL,
        MaxRunsPerDay                   int               NULL,
        DebateModels                    nvarchar(400)     NULL,
        DebateRounds                    int               NULL,
        MaxHelpHops                     int               NULL,
        SkipDebateWhenContributorsBelow int               NULL,
        ChangedAt                       datetimeoffset(7) NOT NULL
            CONSTRAINT DF_monitoring_AgentBudgetSettingAudit_ChangedAt DEFAULT SYSDATETIMEOFFSET(),
        ChangedBy                       nvarchar(128)     NOT NULL
            CONSTRAINT DF_monitoring_AgentBudgetSettingAudit_ChangedBy DEFAULT N''
    );

    -- "Who raised this agent's budget, and when?" is the only question this history is read for.
    CREATE INDEX IX_monitoring_AgentBudgetSettingAudit_Read
        ON monitoring.AgentBudgetSettingAudit (MonkeyKey, ChangedAt DESC);
END
GO

CREATE OR ALTER PROCEDURE monitoring.usp_GetAllAgentBudgets
AS
BEGIN
    SET NOCOUNT ON;

    -- Agents nobody has tuned are absent rather than rows of NULLs: the resolver treats "no row"
    -- and "row of all NULLs" identically, and an absent row is the honest representation of
    -- "this agent still runs on its configured defaults".
    SELECT MonkeyKey, MaxRunsPerDay, DebateModels, DebateRounds,
           MaxHelpHops, SkipDebateWhenContributorsBelow, UpdatedAt, UpdatedBy
    FROM monitoring.AgentBudgetSetting
    ORDER BY MonkeyKey;
END
GO

CREATE OR ALTER PROCEDURE monitoring.usp_GetAgentBudgetAudit
    @MonkeyKey nvarchar(64),
    @Top       int = 50
AS
BEGIN
    SET NOCOUNT ON;

    SELECT TOP (@Top)
           AuditId, MonkeyKey, MaxRunsPerDay, DebateModels, DebateRounds,
           MaxHelpHops, SkipDebateWhenContributorsBelow, ChangedAt, ChangedBy
    FROM monitoring.AgentBudgetSettingAudit
    WHERE MonkeyKey = @MonkeyKey
    ORDER BY ChangedAt DESC, AuditId DESC;
END
GO

CREATE OR ALTER PROCEDURE monitoring.usp_UpsertAgentBudget
    @MonkeyKey                       nvarchar(64),
    @MaxRunsPerDay                   int = NULL,
    @DebateModels                    nvarchar(400) = NULL,
    @DebateRounds                    int = NULL,
    @MaxHelpHops                     int = NULL,
    @SkipDebateWhenContributorsBelow int = NULL,
    @ChangedBy                       nvarchar(128)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now datetimeoffset(7) = SYSDATETIMEOFFSET();

    BEGIN TRANSACTION;

    -- The write is a whole-row replace, NOT a per-field merge: the caller always sends the
    -- complete set, so clearing a field to NULL is how it goes back to the configured default.
    -- A COALESCE here would make "revert this one dial" impossible to express.
    --
    -- HOLDLOCK (serializable) on the MERGE target: without it two concurrent writers can both
    -- miss the row in the match step and both INSERT, failing on the primary key. Same pattern as
    -- monitoring.usp_UpsertAgentPause.
    MERGE monitoring.AgentBudgetSetting WITH (HOLDLOCK) AS target
    USING (SELECT @MonkeyKey AS MonkeyKey) AS incoming
        ON target.MonkeyKey = incoming.MonkeyKey
    WHEN MATCHED THEN
        UPDATE SET
            MaxRunsPerDay = @MaxRunsPerDay,
            DebateModels = @DebateModels,
            DebateRounds = @DebateRounds,
            MaxHelpHops = @MaxHelpHops,
            SkipDebateWhenContributorsBelow = @SkipDebateWhenContributorsBelow,
            UpdatedAt = @Now,
            UpdatedBy = @ChangedBy
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (MonkeyKey, MaxRunsPerDay, DebateModels, DebateRounds,
                MaxHelpHops, SkipDebateWhenContributorsBelow, UpdatedAt, UpdatedBy)
        VALUES (@MonkeyKey, @MaxRunsPerDay, @DebateModels, @DebateRounds,
                @MaxHelpHops, @SkipDebateWhenContributorsBelow, @Now, @ChangedBy);

    -- Same transaction as the upsert: a spend limit that changed without appearing in the history
    -- would make the history actively misleading rather than merely incomplete.
    INSERT INTO monitoring.AgentBudgetSettingAudit
        (MonkeyKey, MaxRunsPerDay, DebateModels, DebateRounds,
         MaxHelpHops, SkipDebateWhenContributorsBelow, ChangedAt, ChangedBy)
    VALUES
        (@MonkeyKey, @MaxRunsPerDay, @DebateModels, @DebateRounds,
         @MaxHelpHops, @SkipDebateWhenContributorsBelow, @Now, @ChangedBy);

    COMMIT TRANSACTION;

    SELECT MonkeyKey, MaxRunsPerDay, DebateModels, DebateRounds,
           MaxHelpHops, SkipDebateWhenContributorsBelow, UpdatedAt, UpdatedBy
    FROM monitoring.AgentBudgetSetting
    WHERE MonkeyKey = @MonkeyKey;
END
GO
