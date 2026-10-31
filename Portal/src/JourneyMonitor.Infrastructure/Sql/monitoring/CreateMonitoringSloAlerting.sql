-- SLO-based burn-rate alerting (Latency + Exception signals) + notification channels.
-- Implements the Feature from docs/product/wi-238036-threshold-research-and-wi-package-2026-09-29.md
-- (WI 1.1-1.14): replaces the flat per-domain latency threshold with an explicit SLO per
-- (domain, stage), evaluated with Google SRE's multiwindow multi-burn-rate method, covering both
-- a Latency signal (stage duration from JourneyStep) and an ExceptionRate signal (incident.Incident
-- occurrences) - see JourneyMonitor.Core.Monitoring.BurnRateDetector.
--
-- GoldenSignalSample: one row per (Domain, Stage, SignalType, BucketStart) - a pre-aggregated
-- good/bad/total count for a short time bucket (usually 5 minutes), written by
-- GoldenSignalCollector every monkey cycle and summed over arbitrary windows in C# (not SQL) so
-- the burn-rate math stays in Core, fully unit-testable with synthetic samples - see
-- ISloEvaluator/BurnRateDetector. Stage is NULL for the domain-level ExceptionRate signal (an
-- exception isn't attributed to a single journey stage the way a latency measurement is).
--
-- SloDefinition: the operator-configured target per (Domain, Stage, SignalType) - Settings tab,
-- same override pattern as monitoring.LatencySpikeSetting. TargetThresholdMs is only meaningful
-- for SignalType='latency' (what counts as "bad" for a single sample); TargetPercentage/WindowDays
-- feed the error-budget calculation (e.g. 99% of samples good over a 28-day window).
--
-- AlertChannelSetting: single row (Id=1), one flag+config block per channel/provider so more than
-- one can be enabled at once (decision: Email supports 3 selectable providers with a fallback
-- chain - Smtp, then Azure Communication Services, then SendGrid, trying each ENABLED+configured
-- one in that order until one succeeds - see Infrastructure/Notifications/EmailAlertNotifier).
-- Every secret (SmtpPassword/AcsConnectionString/SendGridApiKey/TeamsWebhookUrl) is encrypted at
-- rest with IDataProtector before reaching this table, same pattern as
-- monitoring.LlmClientSetting.GitHubToken - see SqlAlertChannelSettingsStore. The audit table
-- records ONLY whether a secret changed, never its value, past or present - same reasoning as
-- monitoring.LlmClientSettingAudit.

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'monitoring')
    EXEC(N'CREATE SCHEMA monitoring');
GO

IF OBJECT_ID(N'monitoring.GoldenSignalSample', N'U') IS NULL
BEGIN
    CREATE TABLE monitoring.GoldenSignalSample
    (
        Id             bigint            NOT NULL IDENTITY(1, 1)
            CONSTRAINT PK_monitoring_GoldenSignalSample PRIMARY KEY,
        Domain         nvarchar(32)      NOT NULL,
        Stage          nvarchar(128)     NULL,
        StageKey       AS (ISNULL(Stage, N'')) PERSISTED,
        SignalType     nvarchar(32)      NOT NULL,
        BucketStart    datetimeoffset(7) NOT NULL,
        BucketMinutes  int               NOT NULL,
        GoodCount      int               NOT NULL,
        BadCount       int               NOT NULL,
        TotalCount     int               NOT NULL,
        CreatedAt      datetimeoffset(7) NOT NULL
            CONSTRAINT DF_monitoring_GoldenSignalSample_CreatedAt DEFAULT SYSDATETIMEOFFSET(),
        CONSTRAINT CK_monitoring_GoldenSignalSample_SignalType CHECK (SignalType IN (N'latency', N'exception_rate')),
        CONSTRAINT CK_monitoring_GoldenSignalSample_Counts CHECK (GoodCount >= 0 AND BadCount >= 0 AND TotalCount >= 0),
        CONSTRAINT UQ_monitoring_GoldenSignalSample UNIQUE (Domain, StageKey, SignalType, BucketStart)
    );

    CREATE INDEX IX_monitoring_GoldenSignalSample_Read
        ON monitoring.GoldenSignalSample (Domain, SignalType, StageKey, BucketStart DESC)
        INCLUDE (Stage, GoodCount, BadCount, TotalCount, BucketMinutes);
END
GO

IF OBJECT_ID(N'monitoring.SloDefinition', N'U') IS NULL
BEGIN
    CREATE TABLE monitoring.SloDefinition
    (
        Id                bigint            NOT NULL IDENTITY(1, 1)
            CONSTRAINT PK_monitoring_SloDefinition PRIMARY KEY,
        Domain            nvarchar(32)      NOT NULL,
        Stage             nvarchar(128)     NULL,
        StageKey          AS (ISNULL(Stage, N'')) PERSISTED,
        SignalType        nvarchar(32)      NOT NULL,
        TargetThresholdMs int               NULL,
        TargetPercentage  decimal(5, 2)     NOT NULL
            CONSTRAINT DF_monitoring_SloDefinition_TargetPercentage DEFAULT 99.00,
        WindowDays        int               NOT NULL
            CONSTRAINT DF_monitoring_SloDefinition_WindowDays DEFAULT 28,
        Enabled           bit               NOT NULL
            CONSTRAINT DF_monitoring_SloDefinition_Enabled DEFAULT 1,
        UpdatedAt         datetimeoffset(7) NOT NULL
            CONSTRAINT DF_monitoring_SloDefinition_UpdatedAt DEFAULT SYSDATETIMEOFFSET(),
        UpdatedBy         nvarchar(128)     NOT NULL
            CONSTRAINT DF_monitoring_SloDefinition_UpdatedBy DEFAULT N'',
        CONSTRAINT CK_monitoring_SloDefinition_SignalType CHECK (SignalType IN (N'latency', N'exception_rate')),
        CONSTRAINT CK_monitoring_SloDefinition_TargetPercentage CHECK (TargetPercentage > 0 AND TargetPercentage <= 100),
        CONSTRAINT CK_monitoring_SloDefinition_WindowDays CHECK (WindowDays > 0 AND WindowDays <= 90),
        CONSTRAINT UQ_monitoring_SloDefinition UNIQUE (Domain, StageKey, SignalType)
    );
END
GO

IF OBJECT_ID(N'monitoring.SloDefinitionAudit', N'U') IS NULL
BEGIN
    CREATE TABLE monitoring.SloDefinitionAudit
    (
        AuditId           bigint            NOT NULL IDENTITY(1, 1)
            CONSTRAINT PK_monitoring_SloDefinitionAudit PRIMARY KEY,
        Domain            nvarchar(32)      NOT NULL,
        Stage             nvarchar(128)     NULL,
        SignalType        nvarchar(32)      NOT NULL,
        TargetThresholdMs int               NULL,
        TargetPercentage  decimal(5, 2)     NOT NULL,
        WindowDays        int               NOT NULL,
        Enabled           bit               NOT NULL,
        ChangedAt         datetimeoffset(7) NOT NULL
            CONSTRAINT DF_monitoring_SloDefinitionAudit_ChangedAt DEFAULT SYSDATETIMEOFFSET(),
        ChangedBy         nvarchar(128)     NOT NULL
            CONSTRAINT DF_monitoring_SloDefinitionAudit_ChangedBy DEFAULT N''
    );

    CREATE INDEX IX_monitoring_SloDefinitionAudit_Read
        ON monitoring.SloDefinitionAudit (ChangedAt DESC);
END
GO

IF OBJECT_ID(N'monitoring.AlertChannelSetting', N'U') IS NULL
BEGIN
    CREATE TABLE monitoring.AlertChannelSetting
    (
        Id                          tinyint           NOT NULL
            CONSTRAINT PK_monitoring_AlertChannelSetting PRIMARY KEY
            CONSTRAINT CK_monitoring_AlertChannelSetting_SingleRow CHECK (Id = 1),
        EmailEnabled                bit               NOT NULL CONSTRAINT DF_mon_ACS_EmailEnabled DEFAULT 0,
        EmailRecipientsCsv          nvarchar(2000)    NULL,
        SmtpEnabled                 bit               NOT NULL CONSTRAINT DF_mon_ACS_SmtpEnabled DEFAULT 0,
        SmtpHost                    nvarchar(256)     NULL,
        SmtpPort                    int               NULL,
        SmtpUsername                nvarchar(256)     NULL,
        SmtpPasswordProtected       nvarchar(max)     NULL,
        SmtpUseSsl                  bit               NOT NULL CONSTRAINT DF_mon_ACS_SmtpUseSsl DEFAULT 1,
        SmtpFromAddress             nvarchar(256)     NULL,
        AcsEnabled                  bit               NOT NULL CONSTRAINT DF_mon_ACS_AcsEnabled DEFAULT 0,
        AcsConnectionStringProtected nvarchar(max)    NULL,
        AcsFromAddress              nvarchar(256)     NULL,
        SendGridEnabled             bit               NOT NULL CONSTRAINT DF_mon_ACS_SendGridEnabled DEFAULT 0,
        SendGridApiKeyProtected     nvarchar(max)     NULL,
        SendGridFromAddress         nvarchar(256)     NULL,
        TeamsEnabled                bit               NOT NULL CONSTRAINT DF_mon_ACS_TeamsEnabled DEFAULT 0,
        TeamsWebhookUrlProtected    nvarchar(max)     NULL,
        UpdatedAt                   datetimeoffset(7) NOT NULL CONSTRAINT DF_mon_ACS_UpdatedAt DEFAULT SYSDATETIMEOFFSET(),
        UpdatedBy                   nvarchar(128)     NOT NULL CONSTRAINT DF_mon_ACS_UpdatedBy DEFAULT N''
    );
END
GO

IF OBJECT_ID(N'monitoring.AlertChannelSettingAudit', N'U') IS NULL
BEGIN
    CREATE TABLE monitoring.AlertChannelSettingAudit
    (
        AuditId                   bigint            NOT NULL IDENTITY(1, 1)
            CONSTRAINT PK_monitoring_AlertChannelSettingAudit PRIMARY KEY,
        EmailEnabled              bit               NOT NULL,
        SmtpEnabled               bit               NOT NULL,
        SmtpPasswordChanged       bit               NOT NULL,
        AcsEnabled                bit               NOT NULL,
        AcsConnectionStringChanged bit              NOT NULL,
        SendGridEnabled           bit               NOT NULL,
        SendGridApiKeyChanged     bit               NOT NULL,
        TeamsEnabled              bit               NOT NULL,
        TeamsWebhookChanged       bit               NOT NULL,
        ChangedAt                 datetimeoffset(7) NOT NULL
            CONSTRAINT DF_monitoring_AlertChannelSettingAudit_ChangedAt DEFAULT SYSDATETIMEOFFSET(),
        ChangedBy                 nvarchar(128)     NOT NULL
            CONSTRAINT DF_monitoring_AlertChannelSettingAudit_ChangedBy DEFAULT N''
    );

    CREATE INDEX IX_monitoring_AlertChannelSettingAudit_Read
        ON monitoring.AlertChannelSettingAudit (ChangedAt DESC);
END
GO

CREATE OR ALTER PROCEDURE monitoring.usp_UpsertGoldenSignalSample
    @Domain        nvarchar(32),
    @Stage         nvarchar(128) = NULL,
    @SignalType    nvarchar(32),
    @BucketStart   datetimeoffset(7),
    @BucketMinutes int,
    @GoodCount     int,
    @BadCount      int,
    @TotalCount    int
AS
BEGIN
    SET NOCOUNT ON;

    MERGE monitoring.GoldenSignalSample WITH (HOLDLOCK) AS target
    USING (SELECT @Domain AS Domain, ISNULL(@Stage, N'') AS StageKey, @SignalType AS SignalType, @BucketStart AS BucketStart) AS incoming
        ON target.Domain = incoming.Domain AND target.StageKey = incoming.StageKey
           AND target.SignalType = incoming.SignalType AND target.BucketStart = incoming.BucketStart
    WHEN MATCHED THEN
        UPDATE SET BucketMinutes = @BucketMinutes, GoodCount = @GoodCount, BadCount = @BadCount, TotalCount = @TotalCount
    WHEN NOT MATCHED THEN
        INSERT (Domain, Stage, SignalType, BucketStart, BucketMinutes, GoodCount, BadCount, TotalCount)
        VALUES (@Domain, @Stage, @SignalType, @BucketStart, @BucketMinutes, @GoodCount, @BadCount, @TotalCount);
END
GO

CREATE OR ALTER PROCEDURE monitoring.usp_GetGoldenSignalSamplesSince
    @Domain     nvarchar(32),
    @SignalType nvarchar(32),
    @Stage      nvarchar(128) = NULL,
    @Since      datetimeoffset(7)
AS
BEGIN
    SET NOCOUNT ON;

    SELECT Stage, BucketStart, BucketMinutes, GoodCount, BadCount, TotalCount
    FROM monitoring.GoldenSignalSample
    WHERE Domain = @Domain AND SignalType = @SignalType AND BucketStart >= @Since
        AND (@Stage IS NULL OR Stage = @Stage)
    ORDER BY BucketStart;
END
GO

CREATE OR ALTER PROCEDURE monitoring.usp_GetAllSloDefinitions
AS
BEGIN
    SET NOCOUNT ON;

    SELECT Domain, Stage, SignalType, TargetThresholdMs, TargetPercentage, WindowDays, Enabled, UpdatedAt, UpdatedBy
    FROM monitoring.SloDefinition
    ORDER BY Domain, SignalType, StageKey;
END
GO

CREATE OR ALTER PROCEDURE monitoring.usp_UpsertSloDefinition
    @Domain            nvarchar(32),
    @Stage             nvarchar(128) = NULL,
    @SignalType        nvarchar(32),
    @TargetThresholdMs int = NULL,
    @TargetPercentage  decimal(5, 2),
    @WindowDays        int,
    @Enabled           bit,
    @ChangedBy         nvarchar(128)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now datetimeoffset(7) = SYSDATETIMEOFFSET();

    BEGIN TRANSACTION;

    MERGE monitoring.SloDefinition WITH (HOLDLOCK) AS target
    USING (SELECT @Domain AS Domain, ISNULL(@Stage, N'') AS StageKey, @SignalType AS SignalType) AS incoming
        ON target.Domain = incoming.Domain AND target.StageKey = incoming.StageKey AND target.SignalType = incoming.SignalType
    WHEN MATCHED THEN
        UPDATE SET
            TargetThresholdMs = @TargetThresholdMs,
            TargetPercentage  = @TargetPercentage,
            WindowDays        = @WindowDays,
            Enabled           = @Enabled,
            UpdatedAt         = @Now,
            UpdatedBy         = @ChangedBy
    WHEN NOT MATCHED THEN
        INSERT (Domain, Stage, SignalType, TargetThresholdMs, TargetPercentage, WindowDays, Enabled, UpdatedAt, UpdatedBy)
        VALUES (@Domain, @Stage, @SignalType, @TargetThresholdMs, @TargetPercentage, @WindowDays, @Enabled, @Now, @ChangedBy);

    INSERT INTO monitoring.SloDefinitionAudit (Domain, Stage, SignalType, TargetThresholdMs, TargetPercentage, WindowDays, Enabled, ChangedAt, ChangedBy)
    VALUES (@Domain, @Stage, @SignalType, @TargetThresholdMs, @TargetPercentage, @WindowDays, @Enabled, @Now, @ChangedBy);

    COMMIT TRANSACTION;
END
GO

CREATE OR ALTER PROCEDURE monitoring.usp_GetSloDefinitionAudit
    @Top int = 200
AS
BEGIN
    SET NOCOUNT ON;

    SELECT TOP (@Top) Domain, Stage, SignalType, TargetThresholdMs, TargetPercentage, WindowDays, Enabled, ChangedAt, ChangedBy
    FROM monitoring.SloDefinitionAudit
    ORDER BY ChangedAt DESC;
END
GO

CREATE OR ALTER PROCEDURE monitoring.usp_GetAlertChannelSetting
AS
BEGIN
    SET NOCOUNT ON;

    SELECT EmailEnabled, EmailRecipientsCsv,
           SmtpEnabled, SmtpHost, SmtpPort, SmtpUsername, SmtpPasswordProtected, SmtpUseSsl, SmtpFromAddress,
           AcsEnabled, AcsConnectionStringProtected, AcsFromAddress,
           SendGridEnabled, SendGridApiKeyProtected, SendGridFromAddress,
           TeamsEnabled, TeamsWebhookUrlProtected,
           UpdatedAt, UpdatedBy
    FROM monitoring.AlertChannelSetting
    WHERE Id = 1;
END
GO

-- @Should* flags follow the same "null means leave unchanged, explicit value means set/clear"
-- pattern as monitoring.usp_UpsertLlmClientSetting - each secret is only ever overwritten when its
-- own flag is 1, so saving e.g. just the email recipient list never touches the Teams webhook.
CREATE OR ALTER PROCEDURE monitoring.usp_UpsertAlertChannelSetting
    @EmailEnabled                bit,
    @EmailRecipientsCsv          nvarchar(2000) = NULL,
    @SmtpEnabled                 bit,
    @SmtpHost                    nvarchar(256) = NULL,
    @SmtpPort                    int = NULL,
    @SmtpUsername                nvarchar(256) = NULL,
    @ShouldUpdateSmtpPassword    bit,
    @SmtpPasswordProtected       nvarchar(max) = NULL,
    @SmtpUseSsl                  bit,
    @SmtpFromAddress             nvarchar(256) = NULL,
    @AcsEnabled                  bit,
    @ShouldUpdateAcsConnection   bit,
    @AcsConnectionStringProtected nvarchar(max) = NULL,
    @AcsFromAddress              nvarchar(256) = NULL,
    @SendGridEnabled             bit,
    @ShouldUpdateSendGridApiKey  bit,
    @SendGridApiKeyProtected     nvarchar(max) = NULL,
    @SendGridFromAddress         nvarchar(256) = NULL,
    @TeamsEnabled                bit,
    @ShouldUpdateTeamsWebhook    bit,
    @TeamsWebhookUrlProtected    nvarchar(max) = NULL,
    @ChangedBy                   nvarchar(128)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now datetimeoffset(7) = SYSDATETIMEOFFSET();

    BEGIN TRANSACTION;

    MERGE monitoring.AlertChannelSetting WITH (HOLDLOCK) AS target
    USING (SELECT CAST(1 AS tinyint) AS Id) AS incoming
        ON target.Id = incoming.Id
    WHEN MATCHED THEN
        UPDATE SET
            EmailEnabled                 = @EmailEnabled,
            EmailRecipientsCsv           = @EmailRecipientsCsv,
            SmtpEnabled                  = @SmtpEnabled,
            SmtpHost                     = @SmtpHost,
            SmtpPort                     = @SmtpPort,
            SmtpUsername                 = @SmtpUsername,
            SmtpPasswordProtected        = CASE WHEN @ShouldUpdateSmtpPassword = 1 THEN @SmtpPasswordProtected ELSE target.SmtpPasswordProtected END,
            SmtpUseSsl                   = @SmtpUseSsl,
            SmtpFromAddress              = @SmtpFromAddress,
            AcsEnabled                   = @AcsEnabled,
            AcsConnectionStringProtected = CASE WHEN @ShouldUpdateAcsConnection = 1 THEN @AcsConnectionStringProtected ELSE target.AcsConnectionStringProtected END,
            AcsFromAddress               = @AcsFromAddress,
            SendGridEnabled              = @SendGridEnabled,
            SendGridApiKeyProtected      = CASE WHEN @ShouldUpdateSendGridApiKey = 1 THEN @SendGridApiKeyProtected ELSE target.SendGridApiKeyProtected END,
            SendGridFromAddress          = @SendGridFromAddress,
            TeamsEnabled                 = @TeamsEnabled,
            TeamsWebhookUrlProtected     = CASE WHEN @ShouldUpdateTeamsWebhook = 1 THEN @TeamsWebhookUrlProtected ELSE target.TeamsWebhookUrlProtected END,
            UpdatedAt                    = @Now,
            UpdatedBy                    = @ChangedBy
    WHEN NOT MATCHED THEN
        INSERT (Id, EmailEnabled, EmailRecipientsCsv, SmtpEnabled, SmtpHost, SmtpPort, SmtpUsername, SmtpPasswordProtected, SmtpUseSsl, SmtpFromAddress,
                AcsEnabled, AcsConnectionStringProtected, AcsFromAddress, SendGridEnabled, SendGridApiKeyProtected, SendGridFromAddress,
                TeamsEnabled, TeamsWebhookUrlProtected, UpdatedAt, UpdatedBy)
        VALUES (1, @EmailEnabled, @EmailRecipientsCsv, @SmtpEnabled, @SmtpHost, @SmtpPort, @SmtpUsername,
                CASE WHEN @ShouldUpdateSmtpPassword = 1 THEN @SmtpPasswordProtected ELSE NULL END, @SmtpUseSsl, @SmtpFromAddress,
                @AcsEnabled, CASE WHEN @ShouldUpdateAcsConnection = 1 THEN @AcsConnectionStringProtected ELSE NULL END, @AcsFromAddress,
                @SendGridEnabled, CASE WHEN @ShouldUpdateSendGridApiKey = 1 THEN @SendGridApiKeyProtected ELSE NULL END, @SendGridFromAddress,
                @TeamsEnabled, CASE WHEN @ShouldUpdateTeamsWebhook = 1 THEN @TeamsWebhookUrlProtected ELSE NULL END, @Now, @ChangedBy);

    INSERT INTO monitoring.AlertChannelSettingAudit
        (EmailEnabled, SmtpEnabled, SmtpPasswordChanged, AcsEnabled, AcsConnectionStringChanged, SendGridEnabled, SendGridApiKeyChanged, TeamsEnabled, TeamsWebhookChanged, ChangedAt, ChangedBy)
    VALUES
        (@EmailEnabled, @SmtpEnabled, @ShouldUpdateSmtpPassword, @AcsEnabled, @ShouldUpdateAcsConnection, @SendGridEnabled, @ShouldUpdateSendGridApiKey, @TeamsEnabled, @ShouldUpdateTeamsWebhook, @Now, @ChangedBy);

    COMMIT TRANSACTION;
END
GO

CREATE OR ALTER PROCEDURE monitoring.usp_GetAlertChannelSettingAudit
    @Top int = 200
AS
BEGIN
    SET NOCOUNT ON;

    SELECT TOP (@Top) EmailEnabled, SmtpEnabled, SmtpPasswordChanged, AcsEnabled, AcsConnectionStringChanged,
           SendGridEnabled, SendGridApiKeyChanged, TeamsEnabled, TeamsWebhookChanged, ChangedAt, ChangedBy
    FROM monitoring.AlertChannelSettingAudit
    ORDER BY ChangedAt DESC;
END
GO

-- Exception-rate signal input: total exception occurrences for a domain since a point in time
-- (see GoldenSignalCollector). Cross-schema reference to incident.* - safe regardless of script
-- execution order (deferred name resolution), but this script still runs after
-- incident/CreateIncidentSchema.sql in docker/init-sql.sh for clarity.
CREATE OR ALTER PROCEDURE monitoring.usp_GetExceptionOccurrenceCount
    @Domain nvarchar(32),
    @Since  datetimeoffset(7),
    @Until  datetimeoffset(7)
AS
BEGIN
    SET NOCOUNT ON;

    SELECT COUNT(*) AS OccurrenceCount
    FROM incident.Occurrence o
    JOIN incident.Incident i ON i.IncidentId = o.IncidentId
    WHERE i.Domain = @Domain AND o.OccurredAt >= @Since AND o.OccurredAt < @Until;
END
GO
