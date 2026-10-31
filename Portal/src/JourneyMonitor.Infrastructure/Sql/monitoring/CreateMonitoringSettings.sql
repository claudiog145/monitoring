-- Per-domain Latency Spike Monkey threshold overrides + full audit trail of every change.
-- Grain: one row per Domain in LatencySpikeSetting (current value); LatencySpikeSettingAudit is
-- append-only history, never updated/deleted. Any user can change a setting (see Settings tab);
-- ChangedBy/UpdatedBy just records who, no authorization check happens here.

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'monitoring')
    EXEC(N'CREATE SCHEMA monitoring');
GO

IF OBJECT_ID(N'monitoring.LatencySpikeSetting', N'U') IS NULL
BEGIN
    CREATE TABLE monitoring.LatencySpikeSetting
    (
        Domain                    nvarchar(32)      NOT NULL
            CONSTRAINT PK_monitoring_LatencySpikeSetting PRIMARY KEY,
        SpikeThresholdMs          int               NULL,
        SpikeOccurrenceThreshold  int               NULL,
        SpikeWindowMinutes        int               NULL,
        UpdatedAt                 datetimeoffset(7) NOT NULL
            CONSTRAINT DF_monitoring_LatencySpikeSetting_UpdatedAt DEFAULT SYSDATETIMEOFFSET(),
        UpdatedBy                 nvarchar(128)     NOT NULL
            CONSTRAINT DF_monitoring_LatencySpikeSetting_UpdatedBy DEFAULT N'',
        CONSTRAINT CK_monitoring_LatencySpikeSetting_Domain CHECK (Domain IN (N'hotels', N'cruises', N'cars', N'air'))
    );
END
GO

IF OBJECT_ID(N'monitoring.LatencySpikeSettingAudit', N'U') IS NULL
BEGIN
    CREATE TABLE monitoring.LatencySpikeSettingAudit
    (
        AuditId                   bigint            NOT NULL IDENTITY(1, 1)
            CONSTRAINT PK_monitoring_LatencySpikeSettingAudit PRIMARY KEY,
        Domain                    nvarchar(32)      NOT NULL,
        SpikeThresholdMs          int               NULL,
        SpikeOccurrenceThreshold  int               NULL,
        SpikeWindowMinutes        int               NULL,
        ChangedAt                 datetimeoffset(7) NOT NULL
            CONSTRAINT DF_monitoring_LatencySpikeSettingAudit_ChangedAt DEFAULT SYSDATETIMEOFFSET(),
        ChangedBy                 nvarchar(128)     NOT NULL
            CONSTRAINT DF_monitoring_LatencySpikeSettingAudit_ChangedBy DEFAULT N''
    );

    CREATE INDEX IX_monitoring_LatencySpikeSettingAudit_Read
        ON monitoring.LatencySpikeSettingAudit (Domain, ChangedAt DESC);
END
GO

CREATE OR ALTER PROCEDURE monitoring.usp_GetLatencySpikeSettings
AS
BEGIN
    SET NOCOUNT ON;

    SELECT Domain, SpikeThresholdMs, SpikeOccurrenceThreshold, SpikeWindowMinutes, UpdatedAt, UpdatedBy
    FROM monitoring.LatencySpikeSetting
    ORDER BY Domain;
END
GO

CREATE OR ALTER PROCEDURE monitoring.usp_UpsertLatencySpikeSetting
    @Domain                   nvarchar(32),
    @SpikeThresholdMs         int = NULL,
    @SpikeOccurrenceThreshold int = NULL,
    @SpikeWindowMinutes       int = NULL,
    @ChangedBy                nvarchar(128)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now datetimeoffset(7) = SYSDATETIMEOFFSET();

    BEGIN TRANSACTION;

    MERGE monitoring.LatencySpikeSetting WITH (HOLDLOCK) AS target
    USING (SELECT @Domain AS Domain) AS incoming
        ON target.Domain = incoming.Domain
    WHEN MATCHED THEN
        UPDATE SET
            SpikeThresholdMs = @SpikeThresholdMs,
            SpikeOccurrenceThreshold = @SpikeOccurrenceThreshold,
            SpikeWindowMinutes = @SpikeWindowMinutes,
            UpdatedAt = @Now,
            UpdatedBy = @ChangedBy
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (Domain, SpikeThresholdMs, SpikeOccurrenceThreshold, SpikeWindowMinutes, UpdatedAt, UpdatedBy)
        VALUES (@Domain, @SpikeThresholdMs, @SpikeOccurrenceThreshold, @SpikeWindowMinutes, @Now, @ChangedBy);

    INSERT INTO monitoring.LatencySpikeSettingAudit
        (Domain, SpikeThresholdMs, SpikeOccurrenceThreshold, SpikeWindowMinutes, ChangedAt, ChangedBy)
    VALUES
        (@Domain, @SpikeThresholdMs, @SpikeOccurrenceThreshold, @SpikeWindowMinutes, @Now, @ChangedBy);

    COMMIT TRANSACTION;
END
GO

CREATE OR ALTER PROCEDURE monitoring.usp_GetLatencySpikeSettingAudit
    @Domain nvarchar(32) = NULL,
    @Top    int = 200
AS
BEGIN
    SET NOCOUNT ON;

    SELECT TOP (@Top) Domain, SpikeThresholdMs, SpikeOccurrenceThreshold, SpikeWindowMinutes, ChangedAt, ChangedBy
    FROM monitoring.LatencySpikeSettingAudit
    WHERE (@Domain IS NULL OR Domain = @Domain)
    ORDER BY ChangedAt DESC;
END
GO
