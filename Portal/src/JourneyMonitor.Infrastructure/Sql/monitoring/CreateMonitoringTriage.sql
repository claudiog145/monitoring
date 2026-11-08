-- Specification 1000 - triage investigations opened by a KPI daemon or a person. One row per
-- investigation; at most one open (Queued/Running) row per DedupKey, enforced by a filtered unique
-- index. Idempotent: tables are created only when missing, procedures use CREATE OR ALTER.

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'monitoring')
    EXEC(N'CREATE SCHEMA monitoring');
GO

IF OBJECT_ID(N'monitoring.TriageInvestigation', N'U') IS NULL
BEGIN
    CREATE TABLE monitoring.TriageInvestigation
    (
        InvestigationId uniqueidentifier  NOT NULL CONSTRAINT PK_monitoring_TriageInvestigation PRIMARY KEY,
        DedupKey        nvarchar(256)     NOT NULL,
        AnomalyId       bigint            NULL,
        SignalJson      nvarchar(max)     NOT NULL,
        Source          nvarchar(16)      NOT NULL,
        Status          nvarchar(16)      NOT NULL,
        ResultJson      nvarchar(max)     NULL,
        AlertLevel      nvarchar(16)      NULL,
        Notified        bit               NOT NULL CONSTRAINT DF_monitoring_TriageInvestigation_Notified DEFAULT 0,
        CreatedAt       datetimeoffset(7) NOT NULL CONSTRAINT DF_monitoring_TriageInvestigation_CreatedAt DEFAULT SYSDATETIMEOFFSET(),
        CompletedAt     datetimeoffset(7) NULL,
        Error           nvarchar(2000)    NULL,
        ProfileKey      nvarchar(128)     NOT NULL CONSTRAINT DF_monitoring_TriageInvestigation_ProfileKey DEFAULT N'arrivia',
        CONSTRAINT CK_monitoring_TriageInvestigation_Source CHECK (Source IN (N'daemon', N'manual')),
        CONSTRAINT CK_monitoring_TriageInvestigation_Status CHECK (Status IN (N'Queued', N'Running', N'Completed', N'Failed', N'Discarded')),
        CONSTRAINT CK_monitoring_TriageInvestigation_AlertLevel CHECK (AlertLevel IS NULL OR AlertLevel IN (N'red_alert', N'amber', N'informational')),
        CONSTRAINT CK_monitoring_TriageInvestigation_SignalJson CHECK (ISJSON(SignalJson) = 1),
        CONSTRAINT CK_monitoring_TriageInvestigation_ResultJson CHECK (ResultJson IS NULL OR ISJSON(ResultJson) = 1)
    );
    CREATE UNIQUE INDEX UX_monitoring_TriageInvestigation_OpenDedup
        ON monitoring.TriageInvestigation (DedupKey) WHERE Status IN (N'Queued', N'Running');
    CREATE INDEX IX_monitoring_TriageInvestigation_Recent ON monitoring.TriageInvestigation (CreatedAt DESC);
END
GO

-- Contract v3: company profile of the investigation (= Signal.ProfileKey). Rows created before it belong to the incumbent.
IF COL_LENGTH(N'monitoring.TriageInvestigation', N'ProfileKey') IS NULL
    ALTER TABLE monitoring.TriageInvestigation
        ADD ProfileKey nvarchar(128) NOT NULL CONSTRAINT DF_monitoring_TriageInvestigation_ProfileKey DEFAULT N'arrivia';
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE object_id = OBJECT_ID(N'monitoring.TriageInvestigation') AND name = N'IX_monitoring_TriageInvestigation_Profile')
    CREATE INDEX IX_monitoring_TriageInvestigation_Profile ON monitoring.TriageInvestigation (ProfileKey, CreatedAt DESC);
GO

CREATE OR ALTER PROCEDURE monitoring.usp_CreateTriageInvestigation
    @InvestigationId uniqueidentifier,
    @DedupKey        nvarchar(256),
    @AnomalyId       bigint = NULL,
    @SignalJson      nvarchar(max),
    @Source          nvarchar(16),
    @ProfileKey      nvarchar(128) = N'arrivia'
AS
BEGIN
    SET NOCOUNT ON;

    INSERT INTO monitoring.TriageInvestigation (InvestigationId, DedupKey, AnomalyId, SignalJson, Source, Status, ProfileKey)
    VALUES (@InvestigationId, @DedupKey, @AnomalyId, @SignalJson, @Source, N'Queued', @ProfileKey);
END
GO

CREATE OR ALTER PROCEDURE monitoring.usp_GetTriageInvestigation
    @InvestigationId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;

    SELECT InvestigationId, DedupKey, AnomalyId, SignalJson, Source, Status, ResultJson, AlertLevel,
           Notified, CreatedAt, CompletedAt, Error, ProfileKey
    FROM monitoring.TriageInvestigation
    WHERE InvestigationId = @InvestigationId;
END
GO

CREATE OR ALTER PROCEDURE monitoring.usp_FindOpenTriageInvestigationByDedupKey
    @DedupKey nvarchar(256)
AS
BEGIN
    SET NOCOUNT ON;

    SELECT TOP (1) InvestigationId, DedupKey, AnomalyId, SignalJson, Source, Status, ResultJson, AlertLevel,
           Notified, CreatedAt, CompletedAt, Error, ProfileKey
    FROM monitoring.TriageInvestigation
    WHERE DedupKey = @DedupKey
      AND Status IN (N'Queued', N'Running')
    ORDER BY CreatedAt DESC;
END
GO

CREATE OR ALTER PROCEDURE monitoring.usp_UpdateTriageInvestigationStatus
    @InvestigationId uniqueidentifier,
    @Status          nvarchar(16),
    @Error           nvarchar(2000) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    UPDATE monitoring.TriageInvestigation
    SET Status = @Status,
        Error = @Error,
        CompletedAt = CASE WHEN @Status IN (N'Failed', N'Discarded') THEN SYSDATETIMEOFFSET() ELSE CompletedAt END
    WHERE InvestigationId = @InvestigationId;
END
GO

CREATE OR ALTER PROCEDURE monitoring.usp_CompleteTriageInvestigation
    @InvestigationId uniqueidentifier,
    @ResultJson      nvarchar(max),
    @AlertLevel      nvarchar(16),
    @Notified        bit
AS
BEGIN
    SET NOCOUNT ON;

    UPDATE monitoring.TriageInvestigation
    SET Status = N'Completed',
        ResultJson = @ResultJson,
        AlertLevel = @AlertLevel,
        Notified = @Notified,
        CompletedAt = SYSDATETIMEOFFSET()
    WHERE InvestigationId = @InvestigationId;
END
GO

CREATE OR ALTER PROCEDURE monitoring.usp_GetRecentTriageInvestigations
    @Top        int = 50,
    @ProfileKey nvarchar(128) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    SELECT TOP (@Top) InvestigationId, DedupKey, AnomalyId, SignalJson, Source, Status, ResultJson, AlertLevel,
           Notified, CreatedAt, CompletedAt, Error, ProfileKey
    FROM monitoring.TriageInvestigation
    WHERE (@ProfileKey IS NULL OR ProfileKey = @ProfileKey)
    ORDER BY CreatedAt DESC;
END
GO
