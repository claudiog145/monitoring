-- Contract v6 §3 / §4.1. Generic, per-company journeys built from telemetry with the company profile:
-- one row per journey (an operation, or a session when the profile defines one) and one row per step.
-- Written by the ProfileJourneySync timer (retention 8 days); read by GET /api/profile-journeys.
-- The legacy hotel/air/car/cruise journey tables are not touched.
--
-- Idempotency: applied on every container start by docker/init-sql.sh. Tables are guarded by
-- IF OBJECT_ID(...) IS NULL and indexes by an existence check, so a re-run changes nothing.

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'monitoring')
    EXEC(N'CREATE SCHEMA monitoring');
GO

IF OBJECT_ID(N'monitoring.ProfileJourney', N'U') IS NULL
BEGIN
    CREATE TABLE monitoring.ProfileJourney
    (
        ProfileKey        nvarchar(128)     NOT NULL
            CONSTRAINT DF_monitoring_ProfileJourney_ProfileKey DEFAULT N'arrivia',
        JourneyId         nvarchar(128)     NOT NULL,
        ServiceKey        nvarchar(128)     NOT NULL,
        FlowKey           nvarchar(128)     NULL,
        EntityKey         nvarchar(256)     NULL,
        StartedAt         datetimeoffset(7) NOT NULL,
        EndedAt           datetimeoffset(7) NOT NULL,
        DurationMs        float             NOT NULL
            CONSTRAINT DF_monitoring_ProfileJourney_DurationMs DEFAULT 0,
        Status            nvarchar(64)      NOT NULL,
        LastStepOrder     int               NULL,
        LastStepKey       nvarchar(128)     NULL,
        StepCount         int               NOT NULL
            CONSTRAINT DF_monitoring_ProfileJourney_StepCount DEFAULT 0,
        Failed            bit               NOT NULL
            CONSTRAINT DF_monitoring_ProfileJourney_Failed DEFAULT 0,
        SampleOperationId nvarchar(128)     NULL,
        SyncedAt          datetimeoffset(7) NOT NULL
            CONSTRAINT DF_monitoring_ProfileJourney_SyncedAt DEFAULT SYSDATETIMEOFFSET(),
        CONSTRAINT PK_monitoring_ProfileJourney PRIMARY KEY (ProfileKey, JourneyId),
        CONSTRAINT CK_monitoring_ProfileJourney_StepCount CHECK (StepCount >= 0),
        CONSTRAINT CK_monitoring_ProfileJourney_DurationMs CHECK (DurationMs >= 0)
    );
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'IX_monitoring_ProfileJourney_Read'
      AND object_id = OBJECT_ID(N'monitoring.ProfileJourney'))
    CREATE INDEX IX_monitoring_ProfileJourney_Read
        ON monitoring.ProfileJourney (ProfileKey, StartedAt DESC)
        INCLUDE (ServiceKey, EntityKey, Status, Failed);
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'IX_monitoring_ProfileJourney_Service'
      AND object_id = OBJECT_ID(N'monitoring.ProfileJourney'))
    CREATE INDEX IX_monitoring_ProfileJourney_Service
        ON monitoring.ProfileJourney (ProfileKey, ServiceKey, StartedAt DESC);
GO

IF OBJECT_ID(N'monitoring.ProfileJourneyStep', N'U') IS NULL
BEGIN
    CREATE TABLE monitoring.ProfileJourneyStep
    (
        ProfileKey  nvarchar(128)     NOT NULL
            CONSTRAINT DF_monitoring_ProfileJourneyStep_ProfileKey DEFAULT N'arrivia',
        JourneyId   nvarchar(128)     NOT NULL,
        StepOrder   int               NOT NULL,
        StepKey     nvarchar(128)     NULL,
        Name        nvarchar(512)     NOT NULL,
        [Timestamp] datetimeoffset(7) NOT NULL,
        DurationMs  float             NOT NULL
            CONSTRAINT DF_monitoring_ProfileJourneyStep_DurationMs DEFAULT 0,
        Success     bit               NOT NULL,
        ResultCode  nvarchar(32)      NULL,
        CONSTRAINT PK_monitoring_ProfileJourneyStep PRIMARY KEY (ProfileKey, JourneyId, StepOrder),
        CONSTRAINT FK_monitoring_ProfileJourneyStep_Journey
            FOREIGN KEY (ProfileKey, JourneyId)
            REFERENCES monitoring.ProfileJourney (ProfileKey, JourneyId)
            ON DELETE CASCADE
    );
END
GO
