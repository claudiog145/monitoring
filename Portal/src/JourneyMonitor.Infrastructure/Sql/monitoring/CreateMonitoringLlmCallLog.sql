-- Durable log of every LLM call the Doctor Monkey makes (prompt sent + raw response text),
-- separate from monitoring.DoctorReport (which only stores the parsed verdict/category/etc.) so
-- this heavier, rarely-needed debug payload never bloats the GET /api/monkeys list response.
-- 1:1 with DoctorReport today (one LLM call per diagnosis), but kept in its own table (not extra
-- columns on DoctorReport) since it is read separately, on demand, by finding id.

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'monitoring')
    EXEC(N'CREATE SCHEMA monitoring');
GO

IF OBJECT_ID(N'monitoring.LlmCallLog', N'U') IS NULL
BEGIN
    CREATE TABLE monitoring.LlmCallLog
    (
        Id              uniqueidentifier  NOT NULL CONSTRAINT PK_monitoring_LlmCallLog PRIMARY KEY,
        DoctorReportId  uniqueidentifier  NOT NULL,
        FindingId       uniqueidentifier  NOT NULL,
        PromptText      nvarchar(max)     NOT NULL,
        RawResponseText nvarchar(max)     NOT NULL,
        ModelUsed       nvarchar(128)     NOT NULL,
        TokensIn        int               NOT NULL CONSTRAINT DF_monitoring_LlmCallLog_TokensIn DEFAULT 0,
        TokensOut       int               NOT NULL CONSTRAINT DF_monitoring_LlmCallLog_TokensOut DEFAULT 0,
        CreatedAt       datetimeoffset(7) NOT NULL
            CONSTRAINT DF_monitoring_LlmCallLog_CreatedAt DEFAULT SYSDATETIMEOFFSET()
    );

    CREATE UNIQUE INDEX UQ_monitoring_LlmCallLog_DoctorReportId ON monitoring.LlmCallLog (DoctorReportId);
    CREATE INDEX IX_monitoring_LlmCallLog_FindingId ON monitoring.LlmCallLog (FindingId);
END
GO

CREATE OR ALTER PROCEDURE monitoring.usp_AddLlmCallLog
    @Id uniqueidentifier, @DoctorReportId uniqueidentifier, @FindingId uniqueidentifier,
    @PromptText nvarchar(max), @RawResponseText nvarchar(max), @ModelUsed nvarchar(128),
    @TokensIn int, @TokensOut int, @CreatedAt datetimeoffset(7)
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO monitoring.LlmCallLog
        (Id, DoctorReportId, FindingId, PromptText, RawResponseText, ModelUsed, TokensIn, TokensOut, CreatedAt)
    VALUES
        (@Id, @DoctorReportId, @FindingId, @PromptText, @RawResponseText, @ModelUsed, @TokensIn, @TokensOut, @CreatedAt);
END
GO

CREATE OR ALTER PROCEDURE monitoring.usp_GetLlmCallLogByDoctorReportId
    @DoctorReportId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    SELECT Id, DoctorReportId, FindingId, PromptText, RawResponseText, ModelUsed, TokensIn, TokensOut, CreatedAt
    FROM monitoring.LlmCallLog
    WHERE DoctorReportId = @DoctorReportId;
END
GO
