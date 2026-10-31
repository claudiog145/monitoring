-- Doctor Monkey's LLM client (GitHub Copilot token + model), editable from the Settings tab so a
-- new environment (docker-compose, QA Container App, ...) does not need Ai:GitHubToken configured
-- by hand via az cli/Azure Portal - see JourneyMonitor.Core.Monitoring.LlmClientSettingsResolver
-- for the DB-override-then-env-var precedence. Grain: a single row (Id = 1) in LlmClientSetting
-- holds the CURRENT value; LlmClientSettingAudit is append-only history, never updated/deleted.
-- Any user can change it (see Settings tab); ChangedBy just records who, no authorization check
-- happens here - same product decision already made for monitoring.LatencySpikeSetting.
--
-- LlmClientSettingAudit deliberately has NO token column, past or present: this table exists so
-- a compromised database does not also hand over a history of every credential this setting ever
-- held - only whether a change touched the token (TokenChanged) is recorded, never the value.
--
-- GitHubToken is encrypted at rest with ASP.NET Core Data Protection before it ever reaches this
-- table (see SqlLlmClientSettingsStore) - a compromised database/backup alone is not enough to
-- recover a usable credential, only the process(es) holding the matching key ring can. Column is
-- sized for the encrypted payload (base64, larger than the raw token), not the raw token itself.

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'monitoring')
    EXEC(N'CREATE SCHEMA monitoring');
GO

IF OBJECT_ID(N'monitoring.LlmClientSetting', N'U') IS NULL
BEGIN
    CREATE TABLE monitoring.LlmClientSetting
    (
        Id           tinyint           NOT NULL
            CONSTRAINT PK_monitoring_LlmClientSetting PRIMARY KEY
            CONSTRAINT CK_monitoring_LlmClientSetting_SingleRow CHECK (Id = 1),
        GitHubToken  nvarchar(1000)    NULL,
        Model        nvarchar(64)      NULL,
        UpdatedAt    datetimeoffset(7) NOT NULL
            CONSTRAINT DF_monitoring_LlmClientSetting_UpdatedAt DEFAULT SYSDATETIMEOFFSET(),
        UpdatedBy    nvarchar(128)     NOT NULL
            CONSTRAINT DF_monitoring_LlmClientSetting_UpdatedBy DEFAULT N''
    );
END
GO

-- Widens an already-existing table's GitHubToken column (created before it held an encrypted,
-- base64 Data-Protection payload instead of the raw token) - idempotent, safe to run every time.
-- Only touches a genuinely narrower legacy column: max_length is -1 for nvarchar(max) (must be
-- excluded, ALTERing it down to 1000 would truncate/fail on longer existing data) and already
-- 2000 for a column this same script already widened. This ALTER only changes column CAPACITY -
-- it does not transform any already-stored value; a legacy plaintext gho_ token already saved
-- before encryption was added is migrated in place on next read instead, see
-- SqlLlmClientSettingsStore.UnprotectAsync.
IF EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'monitoring.LlmClientSetting') AND name = N'GitHubToken'
        AND max_length > 0 AND max_length < 2000
)
    ALTER TABLE monitoring.LlmClientSetting ALTER COLUMN GitHubToken nvarchar(1000) NULL;
GO

IF OBJECT_ID(N'monitoring.LlmClientSettingAudit', N'U') IS NULL
BEGIN
    CREATE TABLE monitoring.LlmClientSettingAudit
    (
        AuditId      bigint            NOT NULL IDENTITY(1, 1)
            CONSTRAINT PK_monitoring_LlmClientSettingAudit PRIMARY KEY,
        TokenChanged bit               NOT NULL,
        Model        nvarchar(64)      NULL,
        ChangedAt    datetimeoffset(7) NOT NULL
            CONSTRAINT DF_monitoring_LlmClientSettingAudit_ChangedAt DEFAULT SYSDATETIMEOFFSET(),
        ChangedBy    nvarchar(128)     NOT NULL
            CONSTRAINT DF_monitoring_LlmClientSettingAudit_ChangedBy DEFAULT N''
    );

    CREATE INDEX IX_monitoring_LlmClientSettingAudit_Read
        ON monitoring.LlmClientSettingAudit (ChangedAt DESC);
END
GO

-- Provider selection (2026-10-15). Added via ALTER rather than inside the CREATE TABLE above
-- because "IF OBJECT_ID ... IS NULL CREATE TABLE" never touches a table that already exists, so a
-- column only declared there would never reach an already-provisioned environment. Keeping the
-- additions here means there is exactly one place that defines them, for new and existing databases.
--
-- Provider is the global default; the three per-agent columns are NULL when that agent inherits it.
-- NULL is deliberately distinct from "set to the same value as the global": the two behave
-- differently the next time somebody changes the global one.
IF COL_LENGTH(N'monitoring.LlmClientSetting', N'Provider') IS NULL
    ALTER TABLE monitoring.LlmClientSetting ADD Provider nvarchar(32) NULL;
GO
IF COL_LENGTH(N'monitoring.LlmClientSetting', N'DoctorProvider') IS NULL
    ALTER TABLE monitoring.LlmClientSetting ADD DoctorProvider nvarchar(32) NULL;
GO
IF COL_LENGTH(N'monitoring.LlmClientSetting', N'ScoutProvider') IS NULL
    ALTER TABLE monitoring.LlmClientSetting ADD ScoutProvider nvarchar(32) NULL;
GO
IF COL_LENGTH(N'monitoring.LlmClientSetting', N'InspectorProvider') IS NULL
    ALTER TABLE monitoring.LlmClientSetting ADD InspectorProvider nvarchar(32) NULL;
GO
IF COL_LENGTH(N'monitoring.LlmClientSetting', N'FoundryEndpoint') IS NULL
    ALTER TABLE monitoring.LlmClientSetting ADD FoundryEndpoint nvarchar(400) NULL;
GO
IF COL_LENGTH(N'monitoring.LlmClientSetting', N'FoundryAuthMode') IS NULL
    ALTER TABLE monitoring.LlmClientSetting ADD FoundryAuthMode nvarchar(32) NULL;
GO
-- Encrypted at rest by SqlLlmClientSettingsStore under its OWN Data Protection purpose, separate
-- from the GitHub token's - sized for the encrypted payload, not the raw key. Stays NULL entirely
-- when the auth mode is managed identity, which is the point of that mode.
IF COL_LENGTH(N'monitoring.LlmClientSetting', N'FoundryApiKey') IS NULL
    ALTER TABLE monitoring.LlmClientSetting ADD FoundryApiKey nvarchar(1000) NULL;
GO
IF COL_LENGTH(N'monitoring.LlmClientSetting', N'FoundryChatDeployment') IS NULL
    ALTER TABLE monitoring.LlmClientSetting ADD FoundryChatDeployment nvarchar(128) NULL;
GO
-- Separate from the chat deployment on purpose: changing the embedding model invalidates every
-- vector already in monitoring.KnowledgeChunk, so it has to be changeable - and auditable - alone.
IF COL_LENGTH(N'monitoring.LlmClientSetting', N'FoundryEmbeddingDeployment') IS NULL
    ALTER TABLE monitoring.LlmClientSetting ADD FoundryEmbeddingDeployment nvarchar(128) NULL;
GO

-- Audit columns. The second secret gets a flag only, never a value - same rule as TokenChanged.
-- NOT NULL needs a DEFAULT here because the table may already hold rows from before this column.
IF COL_LENGTH(N'monitoring.LlmClientSettingAudit', N'FoundryApiKeyChanged') IS NULL
    ALTER TABLE monitoring.LlmClientSettingAudit ADD FoundryApiKeyChanged bit NOT NULL
        CONSTRAINT DF_monitoring_LlmClientSettingAudit_FoundryApiKeyChanged DEFAULT 0;
GO
IF COL_LENGTH(N'monitoring.LlmClientSettingAudit', N'Provider') IS NULL
    ALTER TABLE monitoring.LlmClientSettingAudit ADD Provider nvarchar(32) NULL;
GO
IF COL_LENGTH(N'monitoring.LlmClientSettingAudit', N'DoctorProvider') IS NULL
    ALTER TABLE monitoring.LlmClientSettingAudit ADD DoctorProvider nvarchar(32) NULL;
GO
IF COL_LENGTH(N'monitoring.LlmClientSettingAudit', N'ScoutProvider') IS NULL
    ALTER TABLE monitoring.LlmClientSettingAudit ADD ScoutProvider nvarchar(32) NULL;
GO
IF COL_LENGTH(N'monitoring.LlmClientSettingAudit', N'InspectorProvider') IS NULL
    ALTER TABLE monitoring.LlmClientSettingAudit ADD InspectorProvider nvarchar(32) NULL;
GO
IF COL_LENGTH(N'monitoring.LlmClientSettingAudit', N'FoundryEndpoint') IS NULL
    ALTER TABLE monitoring.LlmClientSettingAudit ADD FoundryEndpoint nvarchar(400) NULL;
GO
IF COL_LENGTH(N'monitoring.LlmClientSettingAudit', N'FoundryAuthMode') IS NULL
    ALTER TABLE monitoring.LlmClientSettingAudit ADD FoundryAuthMode nvarchar(32) NULL;
GO
IF COL_LENGTH(N'monitoring.LlmClientSettingAudit', N'FoundryChatDeployment') IS NULL
    ALTER TABLE monitoring.LlmClientSettingAudit ADD FoundryChatDeployment nvarchar(128) NULL;
GO
IF COL_LENGTH(N'monitoring.LlmClientSettingAudit', N'FoundryEmbeddingDeployment') IS NULL
    ALTER TABLE monitoring.LlmClientSettingAudit ADD FoundryEmbeddingDeployment nvarchar(128) NULL;
GO

CREATE OR ALTER PROCEDURE monitoring.usp_GetLlmClientSetting
AS
BEGIN
    SET NOCOUNT ON;

    SELECT GitHubToken, Model, Provider, DoctorProvider, ScoutProvider, InspectorProvider,
           FoundryEndpoint, FoundryAuthMode, FoundryApiKey, FoundryChatDeployment, FoundryEmbeddingDeployment,
           UpdatedAt, UpdatedBy
    FROM monitoring.LlmClientSetting
    WHERE Id = 1;
END
GO

CREATE OR ALTER PROCEDURE monitoring.usp_UpsertLlmClientSetting
    @ShouldUpdateToken          bit,
    @GitHubToken                nvarchar(1000) = NULL,
    @ShouldUpdateModel          bit,
    @Model                      nvarchar(64)   = NULL,
    @ShouldUpdateProviders      bit            = 0,
    @Provider                   nvarchar(32)   = NULL,
    @DoctorProvider             nvarchar(32)   = NULL,
    @ScoutProvider              nvarchar(32)   = NULL,
    @InspectorProvider          nvarchar(32)   = NULL,
    @ShouldUpdateFoundry        bit            = 0,
    @FoundryEndpoint            nvarchar(400)  = NULL,
    @FoundryAuthMode            nvarchar(32)   = NULL,
    @FoundryChatDeployment      nvarchar(128)  = NULL,
    @FoundryEmbeddingDeployment nvarchar(128)  = NULL,
    @ShouldUpdateFoundryApiKey  bit            = 0,
    @FoundryApiKey              nvarchar(1000) = NULL,
    @ChangedBy                  nvarchar(128)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now datetimeoffset(7) = SYSDATETIMEOFFSET();

    BEGIN TRANSACTION;

    -- MERGE ... WITH (HOLDLOCK), same pattern as usp_UpsertLatencySpikeSetting: two concurrent
    -- first-time saves both passing an "IF NOT EXISTS" check would otherwise race, one losing
    -- with a primary-key violation and the request returning 500.
    --
    -- Each group of fields has its own ShouldUpdate flag: saving only a token must not blank the
    -- provider selection, and saving only the provider must not blank either credential.
    MERGE monitoring.LlmClientSetting WITH (HOLDLOCK) AS target
    USING (SELECT CAST(1 AS tinyint) AS Id) AS incoming
        ON target.Id = incoming.Id
    WHEN MATCHED THEN
        UPDATE SET
            GitHubToken                = CASE WHEN @ShouldUpdateToken = 1 THEN @GitHubToken ELSE target.GitHubToken END,
            Model                      = CASE WHEN @ShouldUpdateModel = 1 THEN @Model ELSE target.Model END,
            Provider                   = CASE WHEN @ShouldUpdateProviders = 1 THEN @Provider ELSE target.Provider END,
            DoctorProvider             = CASE WHEN @ShouldUpdateProviders = 1 THEN @DoctorProvider ELSE target.DoctorProvider END,
            ScoutProvider              = CASE WHEN @ShouldUpdateProviders = 1 THEN @ScoutProvider ELSE target.ScoutProvider END,
            InspectorProvider          = CASE WHEN @ShouldUpdateProviders = 1 THEN @InspectorProvider ELSE target.InspectorProvider END,
            FoundryEndpoint            = CASE WHEN @ShouldUpdateFoundry = 1 THEN @FoundryEndpoint ELSE target.FoundryEndpoint END,
            FoundryAuthMode            = CASE WHEN @ShouldUpdateFoundry = 1 THEN @FoundryAuthMode ELSE target.FoundryAuthMode END,
            FoundryChatDeployment      = CASE WHEN @ShouldUpdateFoundry = 1 THEN @FoundryChatDeployment ELSE target.FoundryChatDeployment END,
            FoundryEmbeddingDeployment = CASE WHEN @ShouldUpdateFoundry = 1 THEN @FoundryEmbeddingDeployment ELSE target.FoundryEmbeddingDeployment END,
            FoundryApiKey              = CASE WHEN @ShouldUpdateFoundryApiKey = 1 THEN @FoundryApiKey ELSE target.FoundryApiKey END,
            UpdatedAt                  = @Now,
            UpdatedBy                  = @ChangedBy
    WHEN NOT MATCHED THEN
        INSERT (Id, GitHubToken, Model, Provider, DoctorProvider, ScoutProvider, InspectorProvider,
                FoundryEndpoint, FoundryAuthMode, FoundryChatDeployment, FoundryEmbeddingDeployment,
                FoundryApiKey, UpdatedAt, UpdatedBy)
        VALUES (
            1,
            CASE WHEN @ShouldUpdateToken = 1 THEN @GitHubToken ELSE NULL END,
            CASE WHEN @ShouldUpdateModel = 1 THEN @Model ELSE NULL END,
            CASE WHEN @ShouldUpdateProviders = 1 THEN @Provider ELSE NULL END,
            CASE WHEN @ShouldUpdateProviders = 1 THEN @DoctorProvider ELSE NULL END,
            CASE WHEN @ShouldUpdateProviders = 1 THEN @ScoutProvider ELSE NULL END,
            CASE WHEN @ShouldUpdateProviders = 1 THEN @InspectorProvider ELSE NULL END,
            CASE WHEN @ShouldUpdateFoundry = 1 THEN @FoundryEndpoint ELSE NULL END,
            CASE WHEN @ShouldUpdateFoundry = 1 THEN @FoundryAuthMode ELSE NULL END,
            CASE WHEN @ShouldUpdateFoundry = 1 THEN @FoundryChatDeployment ELSE NULL END,
            CASE WHEN @ShouldUpdateFoundry = 1 THEN @FoundryEmbeddingDeployment ELSE NULL END,
            CASE WHEN @ShouldUpdateFoundryApiKey = 1 THEN @FoundryApiKey ELSE NULL END,
            @Now,
            @ChangedBy);

    -- Non-secret fields are recorded as values ("who pointed production at which model" is the
    -- question this table answers); the two secrets only ever as a changed/not-changed flag.
    INSERT INTO monitoring.LlmClientSettingAudit
        (TokenChanged, FoundryApiKeyChanged, Model, Provider, DoctorProvider, ScoutProvider, InspectorProvider,
         FoundryEndpoint, FoundryAuthMode, FoundryChatDeployment, FoundryEmbeddingDeployment, ChangedAt, ChangedBy)
    SELECT @ShouldUpdateToken, @ShouldUpdateFoundryApiKey, Model, Provider, DoctorProvider, ScoutProvider, InspectorProvider,
           FoundryEndpoint, FoundryAuthMode, FoundryChatDeployment, FoundryEmbeddingDeployment, @Now, @ChangedBy
    FROM monitoring.LlmClientSetting
    WHERE Id = 1;

    COMMIT TRANSACTION;
END
GO

CREATE OR ALTER PROCEDURE monitoring.usp_GetLlmClientSettingAudit
    @Top int = 200
AS
BEGIN
    SET NOCOUNT ON;

    SELECT TOP (@Top) TokenChanged, FoundryApiKeyChanged, Model, Provider, DoctorProvider, ScoutProvider,
           InspectorProvider, FoundryEndpoint, FoundryAuthMode, FoundryChatDeployment, FoundryEmbeddingDeployment,
           ChangedAt, ChangedBy
    FROM monitoring.LlmClientSettingAudit
    ORDER BY ChangedAt DESC;
END
GO
