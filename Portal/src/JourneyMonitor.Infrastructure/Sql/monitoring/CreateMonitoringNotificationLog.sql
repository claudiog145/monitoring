-- Durable log of every outbound notification attempt (toast/Teams/email) for whatever produced it
-- - see INotificationChannel implementations (Infrastructure/Notifications/) and the
-- NotificationService that writes these rows. Answers "was this already notified?" which, before
-- this table existed, only lived in ephemeral Function container logs (not queryable, not visible
-- to a portal user). One row per (source, channel) attempt, and Success=0 rows are kept (not just
-- successes) so a failed send is also visible, not silently lost.
--
-- The 'toast' channel never leaves the process: its row IS the delivery, and the UI polls for it
-- with usp_GetUndeliveredToasts.

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'monitoring')
    EXEC(N'CREATE SCHEMA monitoring');
GO

IF OBJECT_ID(N'monitoring.NotificationLog', N'U') IS NULL
BEGIN
    CREATE TABLE monitoring.NotificationLog
    (
        Id           uniqueidentifier  NOT NULL CONSTRAINT PK_monitoring_NotificationLog PRIMARY KEY,
        -- Kept for the rows written before the log was generalised; see the migration below. New
        -- rows leave it NULL and describe their origin with SourceType/SourceId instead.
        FindingId    uniqueidentifier  NULL,
        SourceType   nvarchar(32)      NOT NULL,
        SourceId     uniqueidentifier  NOT NULL,
        Channel      nvarchar(16)      NOT NULL,
        ProviderUsed nvarchar(32)      NULL,
        Success      bit               NOT NULL,
        ErrorMessage nvarchar(1024)    NULL,
        SentAt       datetimeoffset(7) NOT NULL
            CONSTRAINT DF_monitoring_NotificationLog_SentAt DEFAULT SYSDATETIMEOFFSET(),
        Subject      nvarchar(512)     NULL,
        CONSTRAINT CK_monitoring_NotificationLog_Channel CHECK (Channel IN (N'teams', N'email', N'toast'))
    );
END
GO

-- Migration for databases provisioned before the log was generalised beyond Findings. The
-- CREATE TABLE above does not run there (IF OBJECT_ID ... IS NULL), so every column added later
-- needs its own idempotent ALTER or it never reaches an already-provisioned environment.
IF COL_LENGTH(N'monitoring.NotificationLog', N'SourceType') IS NULL
    ALTER TABLE monitoring.NotificationLog ADD SourceType nvarchar(32) NULL;
GO

IF COL_LENGTH(N'monitoring.NotificationLog', N'SourceId') IS NULL
    ALTER TABLE monitoring.NotificationLog ADD SourceId uniqueidentifier NULL;
GO

IF COL_LENGTH(N'monitoring.NotificationLog', N'Subject') IS NULL
    ALTER TABLE monitoring.NotificationLog ADD Subject nvarchar(512) NULL;
GO

-- Backfill only where it has not happened yet, so re-running the script never overwrites a row
-- that a newer build already wrote with a non-Finding source.
UPDATE monitoring.NotificationLog
SET SourceType = N'finding',
    SourceId   = FindingId
WHERE SourceType IS NULL
  AND FindingId IS NOT NULL;
GO

-- FindingId is kept rather than dropped: these rows are an audit trail, dropping a column in a
-- script that also runs against QA/production is irreversible, and the SQL schema is applied
-- separately from the app deploy (scripts/apply-sql-schema.ps1), so an older build can still be
-- live against the migrated database. It must become nullable, though - new rows no longer write
-- it, and a NOT NULL column with no default would reject every insert. Its index goes first:
-- nothing reads by FindingId any more, and the column is easier to alter without it.
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_monitoring_NotificationLog_FindingId' AND object_id = OBJECT_ID(N'monitoring.NotificationLog', N'U'))
    DROP INDEX IX_monitoring_NotificationLog_FindingId ON monitoring.NotificationLog;
GO

IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID(N'monitoring.NotificationLog', N'U')
             AND name = N'FindingId' AND is_nullable = 0)
    ALTER TABLE monitoring.NotificationLog ALTER COLUMN FindingId uniqueidentifier NULL;
GO

-- Tighten the backfilled columns so a migrated database ends up with the same shape as a freshly
-- created one. Guarded on "nothing left to backfill" because an ALTER that fails aborts the whole
-- init-sql.sh script list, not just this statement.
IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID(N'monitoring.NotificationLog', N'U')
             AND name = N'SourceType' AND is_nullable = 1)
   AND NOT EXISTS (SELECT 1 FROM monitoring.NotificationLog WHERE SourceType IS NULL)
    ALTER TABLE monitoring.NotificationLog ALTER COLUMN SourceType nvarchar(32) NOT NULL;
GO

IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID(N'monitoring.NotificationLog', N'U')
             AND name = N'SourceId' AND is_nullable = 1)
   AND NOT EXISTS (SELECT 1 FROM monitoring.NotificationLog WHERE SourceId IS NULL)
    ALTER TABLE monitoring.NotificationLog ALTER COLUMN SourceId uniqueidentifier NOT NULL;
GO

-- The original constraint only allowed teams/email, so an already-provisioned database would
-- reject every toast row until it is replaced.
IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_monitoring_NotificationLog_Channel')
    ALTER TABLE monitoring.NotificationLog DROP CONSTRAINT CK_monitoring_NotificationLog_Channel;
GO

ALTER TABLE monitoring.NotificationLog WITH NOCHECK
    ADD CONSTRAINT CK_monitoring_NotificationLog_Channel CHECK (Channel IN (N'teams', N'email', N'toast'));
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_monitoring_NotificationLog_Source' AND object_id = OBJECT_ID(N'monitoring.NotificationLog', N'U'))
    CREATE INDEX IX_monitoring_NotificationLog_Source ON monitoring.NotificationLog (SourceType, SourceId, SentAt DESC);
GO

-- The toast poll runs every few seconds; without this it scans an append-only table that only grows.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_monitoring_NotificationLog_Channel_SentAt' AND object_id = OBJECT_ID(N'monitoring.NotificationLog', N'U'))
    CREATE INDEX IX_monitoring_NotificationLog_Channel_SentAt ON monitoring.NotificationLog (Channel, SentAt);
GO

-- Nothing reads by FindingId any more - leaving the proc behind would advertise a lookup that
-- silently misses every row written after the migration.
IF OBJECT_ID(N'monitoring.usp_GetNotificationLogByFindingId', N'P') IS NOT NULL
    DROP PROCEDURE monitoring.usp_GetNotificationLogByFindingId;
GO

CREATE OR ALTER PROCEDURE monitoring.usp_AddNotificationLog
    @Id uniqueidentifier, @SourceType nvarchar(32), @SourceId uniqueidentifier, @Channel nvarchar(16),
    @ProviderUsed nvarchar(32) = NULL, @Success bit, @ErrorMessage nvarchar(1024) = NULL,
    @SentAt datetimeoffset(7), @Subject nvarchar(512) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO monitoring.NotificationLog (Id, SourceType, SourceId, Channel, ProviderUsed, Success, ErrorMessage, SentAt, Subject)
    VALUES (@Id, @SourceType, @SourceId, @Channel, @ProviderUsed, @Success, @ErrorMessage, @SentAt, @Subject);
END
GO

CREATE OR ALTER PROCEDURE monitoring.usp_GetNotificationLogBySource
    @SourceType nvarchar(32), @SourceId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    SELECT Id, SourceType, SourceId, Channel, ProviderUsed, Success, ErrorMessage, SentAt, Subject
    FROM monitoring.NotificationLog
    WHERE SourceType = @SourceType AND SourceId = @SourceId
    ORDER BY SentAt DESC;
END
GO

CREATE OR ALTER PROCEDURE monitoring.usp_GetAllNotificationLogs
    @Top int = 500
AS
BEGIN
    SET NOCOUNT ON;
    SELECT TOP (@Top) Id, SourceType, SourceId, Channel, ProviderUsed, Success, ErrorMessage, SentAt, Subject
    FROM monitoring.NotificationLog
    ORDER BY SentAt DESC;
END
GO

-- Oldest first: the UI renders them in the order they happened and then polls again with the
-- SentAt of the last one it showed.
CREATE OR ALTER PROCEDURE monitoring.usp_GetUndeliveredToasts
    @Since datetimeoffset(7), @Top int = 100
AS
BEGIN
    SET NOCOUNT ON;
    SELECT TOP (@Top) Id, SourceType, SourceId, Channel, ProviderUsed, Success, ErrorMessage, SentAt, Subject
    FROM monitoring.NotificationLog
    WHERE Channel = N'toast' AND Success = 1 AND SentAt > @Since
    ORDER BY SentAt;
END
GO
