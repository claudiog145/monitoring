-- One row each time a monkey executes an action or a person approves one.
-- The Executive Overview tile counts these rows. Ingestion does not write them.
-- Latency Spike and Doctor observe only, so this table stays empty until a monkey acts.

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'monitoring')
    EXEC(N'CREATE SCHEMA monitoring');
GO

IF OBJECT_ID(N'monitoring.MonkeyAction', N'U') IS NULL
BEGIN
    CREATE TABLE monitoring.MonkeyAction
    (
        ActionId   bigint            NOT NULL IDENTITY(1, 1)
            CONSTRAINT PK_monitoring_MonkeyAction PRIMARY KEY,
        MonkeyKey  nvarchar(64)      NOT NULL,
        ActionKind nvarchar(32)      NOT NULL,
        At         datetimeoffset(7) NOT NULL
            CONSTRAINT DF_monitoring_MonkeyAction_At DEFAULT SYSDATETIMEOFFSET(),
        Actor      nvarchar(128)     NOT NULL,
        Note       nvarchar(1024)    NULL,
        CONSTRAINT CK_monitoring_MonkeyAction_Kind CHECK (ActionKind IN (N'autonomous', N'approved'))
    );

    CREATE INDEX IX_monitoring_MonkeyAction_At
        ON monitoring.MonkeyAction (At DESC);
END
GO
