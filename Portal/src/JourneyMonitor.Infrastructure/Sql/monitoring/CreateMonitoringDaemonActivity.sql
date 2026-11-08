IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'monitoring')
    EXEC(N'CREATE SCHEMA monitoring');
GO

IF OBJECT_ID(N'monitoring.DaemonActivity', N'U') IS NULL
BEGIN
    CREATE TABLE monitoring.DaemonActivity
    (
        ActivityId bigint            NOT NULL IDENTITY(1, 1) CONSTRAINT PK_monitoring_DaemonActivity PRIMARY KEY,
        DaemonKey  nvarchar(64)      NOT NULL,
        RunId      uniqueidentifier  NOT NULL,
        [At]       datetimeoffset(7) NOT NULL,
        Level      nvarchar(16)      NOT NULL,
        Kind       nvarchar(32)      NOT NULL,
        Message    nvarchar(1000)    NOT NULL,
        DataJson   nvarchar(max)     NULL,
        CONSTRAINT CK_monitoring_DaemonActivity_Level CHECK (Level IN (N'Info', N'Warning', N'Error')),
        CONSTRAINT CK_monitoring_DaemonActivity_Data CHECK (DataJson IS NULL OR ISJSON(DataJson) = 1)
    );
    CREATE INDEX IX_monitoring_DaemonActivity_Daemon ON monitoring.DaemonActivity (DaemonKey, ActivityId);
    CREATE INDEX IX_monitoring_DaemonActivity_At ON monitoring.DaemonActivity ([At]);
END
GO

-- @EntriesJson: [{daemonKey, runId, at, level, kind, message, dataJson}]. Rows are inserted in array order so
-- ActivityId follows the order the daemon wrote them (INSERT ... SELECT ... ORDER BY fixes identity assignment).
CREATE OR ALTER PROCEDURE monitoring.usp_AddDaemonActivity
    @EntriesJson nvarchar(max)
AS
BEGIN
    SET NOCOUNT ON;

    INSERT INTO monitoring.DaemonActivity (DaemonKey, RunId, [At], Level, Kind, Message, DataJson)
    SELECT e.DaemonKey, e.RunId, e.[At], e.Level, e.Kind, LEFT(ISNULL(e.Message, N''), 1000), e.DataJson
    FROM OPENJSON(@EntriesJson) AS a
    CROSS APPLY OPENJSON(a.[value])
    WITH
    (
        DaemonKey nvarchar(64)      '$.daemonKey',
        RunId     uniqueidentifier  '$.runId',
        [At]      datetimeoffset(7) '$.at',
        Level     nvarchar(16)      '$.level',
        Kind      nvarchar(32)      '$.kind',
        Message   nvarchar(max)     '$.message',
        DataJson  nvarchar(max)     '$.dataJson'
    ) AS e
    ORDER BY CAST(a.[key] AS int);
END
GO

CREATE OR ALTER PROCEDURE monitoring.usp_GetDaemonActivity
    @AfterId   bigint       = 0,
    @DaemonKey nvarchar(64) = NULL,
    @MinLevel  nvarchar(16) = NULL,
    @Top       int          = 200
AS
BEGIN
    SET NOCOUNT ON;

    IF @AfterId > 0
    BEGIN
        SELECT TOP (@Top) ActivityId, DaemonKey, RunId, [At], Level, Kind, Message, DataJson
        FROM monitoring.DaemonActivity
        WHERE ActivityId > @AfterId
          AND (@DaemonKey IS NULL OR DaemonKey = @DaemonKey)
          AND (@MinLevel IS NULL OR @MinLevel = N'Info'
               OR (@MinLevel = N'Warning' AND Level IN (N'Warning', N'Error'))
               OR (@MinLevel = N'Error' AND Level = N'Error'))
        ORDER BY ActivityId;
        RETURN;
    END

    SELECT ActivityId, DaemonKey, RunId, [At], Level, Kind, Message, DataJson
    FROM
    (
        SELECT TOP (@Top) ActivityId, DaemonKey, RunId, [At], Level, Kind, Message, DataJson
        FROM monitoring.DaemonActivity
        WHERE (@DaemonKey IS NULL OR DaemonKey = @DaemonKey)
          AND (@MinLevel IS NULL OR @MinLevel = N'Info'
               OR (@MinLevel = N'Warning' AND Level IN (N'Warning', N'Error'))
               OR (@MinLevel = N'Error' AND Level = N'Error'))
        ORDER BY ActivityId DESC
    ) AS latest
    ORDER BY ActivityId;
END
GO

CREATE OR ALTER PROCEDURE monitoring.usp_PurgeDaemonActivity
    @Cutoff datetimeoffset(7)
AS
BEGIN
    SET NOCOUNT ON;

    DELETE FROM monitoring.DaemonActivity WHERE [At] < @Cutoff;
    SELECT @@ROWCOUNT AS Deleted;
END
GO
