-- RCA Agent output (2026-10-15). One composed, publishable RCA per finished investigation.
--
-- Grain: one row per ForensicCase. A re-composition REPLACES the row rather than appending a
-- version - two leadership documents about the same incident with no way to tell which is current
-- is worse than one that can be rewritten. GeneratedAt says when the current text was produced.
--
-- The structured parts (sections, the 5 Whys chain, precedents) are stored as JSON rather than
-- child tables: nothing queries inside them, they are always read whole with the report, and the
-- shape belongs to the document template rather than to the database.

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'monitoring')
    EXEC(N'CREATE SCHEMA monitoring');
GO

IF OBJECT_ID(N'monitoring.RcaReport', N'U') IS NULL
BEGIN
    CREATE TABLE monitoring.RcaReport
    (
        Id              uniqueidentifier  NOT NULL CONSTRAINT PK_monitoring_RcaReport PRIMARY KEY,
        ForensicCaseId  uniqueidentifier  NOT NULL,
        Title           nvarchar(512)     NOT NULL,
        Domain          nvarchar(64)      NULL,
        Stage           nvarchar(128)     NULL,
        SectionsJson    nvarchar(max)     NOT NULL,
        FiveWhysJson    nvarchar(max)     NOT NULL,
        PrecedentsJson  nvarchar(max)     NOT NULL,
        RequiresHumanJson nvarchar(max)   NOT NULL,
        LlmCalls        int               NOT NULL CONSTRAINT DF_monitoring_RcaReport_LlmCalls DEFAULT 0,
        TokensIn        int               NOT NULL CONSTRAINT DF_monitoring_RcaReport_TokensIn DEFAULT 0,
        TokensOut       int               NOT NULL CONSTRAINT DF_monitoring_RcaReport_TokensOut DEFAULT 0,
        ModelUsed       nvarchar(128)     NOT NULL CONSTRAINT DF_monitoring_RcaReport_ModelUsed DEFAULT N'none',
        ErrorMessage    nvarchar(max)     NULL,
        GeneratedAt     datetimeoffset(7) NOT NULL
            CONSTRAINT DF_monitoring_RcaReport_GeneratedAt DEFAULT SYSDATETIMEOFFSET()
    );

    -- One document per investigation; the upsert below depends on this.
    CREATE UNIQUE INDEX UQ_monitoring_RcaReport_ForensicCaseId
        ON monitoring.RcaReport (ForensicCaseId);

    CREATE INDEX IX_monitoring_RcaReport_Recent
        ON monitoring.RcaReport (GeneratedAt DESC);
END
GO

CREATE OR ALTER PROCEDURE monitoring.usp_SaveRcaReport
    @Id uniqueidentifier,
    @ForensicCaseId uniqueidentifier,
    @Title nvarchar(512),
    @Domain nvarchar(64) = NULL,
    @Stage nvarchar(128) = NULL,
    @SectionsJson nvarchar(max),
    @FiveWhysJson nvarchar(max),
    @PrecedentsJson nvarchar(max),
    @RequiresHumanJson nvarchar(max),
    @LlmCalls int,
    @TokensIn int,
    @TokensOut int,
    @ModelUsed nvarchar(128),
    @ErrorMessage nvarchar(max) = NULL,
    @GeneratedAt datetimeoffset(7)
AS
BEGIN
    SET NOCOUNT ON;

    -- MERGE ... WITH (HOLDLOCK), same reason as the settings upserts: two people composing the
    -- same case at once would otherwise race, one losing on the unique index with a 500.
    MERGE monitoring.RcaReport WITH (HOLDLOCK) AS target
    USING (SELECT @ForensicCaseId AS ForensicCaseId) AS incoming
        ON target.ForensicCaseId = incoming.ForensicCaseId
    WHEN MATCHED THEN
        UPDATE SET
            Title = @Title, Domain = @Domain, Stage = @Stage,
            SectionsJson = @SectionsJson, FiveWhysJson = @FiveWhysJson,
            PrecedentsJson = @PrecedentsJson, RequiresHumanJson = @RequiresHumanJson,
            LlmCalls = @LlmCalls, TokensIn = @TokensIn, TokensOut = @TokensOut,
            ModelUsed = @ModelUsed, ErrorMessage = @ErrorMessage, GeneratedAt = @GeneratedAt
    WHEN NOT MATCHED THEN
        INSERT (Id, ForensicCaseId, Title, Domain, Stage, SectionsJson, FiveWhysJson,
                PrecedentsJson, RequiresHumanJson, LlmCalls, TokensIn, TokensOut, ModelUsed,
                ErrorMessage, GeneratedAt)
        VALUES (@Id, @ForensicCaseId, @Title, @Domain, @Stage, @SectionsJson, @FiveWhysJson,
                @PrecedentsJson, @RequiresHumanJson, @LlmCalls, @TokensIn, @TokensOut, @ModelUsed,
                @ErrorMessage, @GeneratedAt);
END
GO

CREATE OR ALTER PROCEDURE monitoring.usp_GetRcaReportByForensicCaseId
    @ForensicCaseId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    SELECT Id, ForensicCaseId, Title, Domain, Stage, SectionsJson, FiveWhysJson, PrecedentsJson,
           RequiresHumanJson, LlmCalls, TokensIn, TokensOut, ModelUsed, ErrorMessage, GeneratedAt
    FROM monitoring.RcaReport
    WHERE ForensicCaseId = @ForensicCaseId;
END
GO

CREATE OR ALTER PROCEDURE monitoring.usp_GetRecentRcaReports
    @Top int = 25
AS
BEGIN
    SET NOCOUNT ON;
    SELECT TOP (@Top) Id, ForensicCaseId, Title, Domain, Stage, SectionsJson, FiveWhysJson, PrecedentsJson,
           RequiresHumanJson, LlmCalls, TokensIn, TokensOut, ModelUsed, ErrorMessage, GeneratedAt
    FROM monitoring.RcaReport
    ORDER BY GeneratedAt DESC;
END
GO
