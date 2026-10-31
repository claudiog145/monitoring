-- Shared dashboard metric store. Domain is hotels/cruises/cars/air so Cars and Air can
-- be ingested later without a new table. Chart ids are step_latency_p95 and exceptions_by_step.
-- Grain: one row per (Domain, Chart, ClubId, Environment, Series, BinStart). Re-ingest MERGEs those keys.

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'dashboard')
    EXEC(N'CREATE SCHEMA dashboard');
GO

IF OBJECT_ID(N'dashboard.MetricPoint', N'U') IS NULL
BEGIN
    CREATE TABLE dashboard.MetricPoint
    (
        MetricPointId bigint            NOT NULL IDENTITY(1, 1)
            CONSTRAINT PK_dashboard_MetricPoint PRIMARY KEY,
        Domain        nvarchar(32)      NOT NULL,
        Chart         nvarchar(64)      NOT NULL,
        Series        nvarchar(128)     NOT NULL,
        BinStart      datetimeoffset(7) NOT NULL,
        Value         decimal(18, 2)    NOT NULL,
        Window        nvarchar(16)      NOT NULL,
        Environment   nvarchar(32)      NULL,
        FetchedAt     datetimeoffset(7) NOT NULL
            CONSTRAINT DF_dashboard_MetricPoint_FetchedAt DEFAULT SYSDATETIMEOFFSET(),
        ClubId        nvarchar(32)      NOT NULL
            CONSTRAINT DF_dashboard_MetricPoint_ClubId DEFAULT N'',
        CONSTRAINT UQ_dashboard_MetricPoint UNIQUE (Domain, Chart, ClubId, Environment, Series, BinStart),
        CONSTRAINT CK_dashboard_MetricPoint_Domain CHECK (Domain IN (N'hotels', N'cruises', N'cars', N'air'))
    );

    CREATE INDEX IX_dashboard_MetricPoint_Read
        ON dashboard.MetricPoint (Domain, Chart, BinStart)
        INCLUDE (Series, Value);
END
GO

IF COL_LENGTH(N'dashboard.MetricPoint', N'ClubId') IS NULL
    ALTER TABLE dashboard.MetricPoint ADD ClubId nvarchar(32) NOT NULL
        CONSTRAINT DF_dashboard_MetricPoint_ClubId DEFAULT N'';
GO

IF EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'UQ_dashboard_MetricPoint'
      AND object_id = OBJECT_ID(N'dashboard.MetricPoint'))
    ALTER TABLE dashboard.MetricPoint DROP CONSTRAINT UQ_dashboard_MetricPoint;
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'UQ_dashboard_MetricPoint'
      AND object_id = OBJECT_ID(N'dashboard.MetricPoint'))
    ALTER TABLE dashboard.MetricPoint ADD CONSTRAINT UQ_dashboard_MetricPoint
        UNIQUE (Domain, Chart, ClubId, Environment, Series, BinStart);
GO

CREATE OR ALTER PROCEDURE dashboard.usp_ReplaceChartPoints
    @Domain      nvarchar(32),
    @Chart       nvarchar(64),
    @Window      nvarchar(16),
    @Environment nvarchar(32) = NULL,
    @ClubId      nvarchar(32) = NULL,
    @RowsJson    nvarchar(max)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @RowsJson IS NULL OR ISJSON(@RowsJson) <> 1
        THROW 50011, N'@RowsJson must be a JSON array of metric points.', 1;

    DECLARE @Now datetimeoffset(7) = SYSDATETIMEOFFSET();
    DECLARE @Club nvarchar(32) = ISNULL(@ClubId, N'');
    DECLARE @Env nvarchar(32) = @Environment;

    BEGIN TRANSACTION;

    ;WITH src AS
    (
        SELECT
            BinStart,
            Series,
            Value,
            ROW_NUMBER() OVER (PARTITION BY BinStart, Series ORDER BY BinStart) AS rn
        FROM OPENJSON(@RowsJson)
        WITH
        (
            BinStart datetimeoffset(7) N'$.binStart',
            Series   nvarchar(128)     N'$.series',
            Value    decimal(18, 2)    N'$.value'
        )
        WHERE Series IS NOT NULL
          AND BinStart IS NOT NULL
          AND Value IS NOT NULL
    )
    MERGE dashboard.MetricPoint WITH (HOLDLOCK) AS target
    USING (SELECT BinStart, Series, Value FROM src WHERE rn = 1) AS incoming
        ON target.Domain = @Domain
       AND target.Chart = @Chart
       AND ISNULL(target.ClubId, N'') = @Club
       AND ISNULL(target.Environment, N'') = ISNULL(@Env, N'')
       AND target.Series = incoming.Series
       AND target.BinStart = incoming.BinStart
    WHEN MATCHED THEN
        UPDATE SET
            Value = incoming.Value,
            Window = @Window,
            Environment = @Env,
            ClubId = @Club,
            FetchedAt = @Now
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (Domain, Chart, Series, BinStart, Value, Window, Environment, ClubId, FetchedAt)
        VALUES (@Domain, @Chart, incoming.Series, incoming.BinStart, incoming.Value, @Window, @Env, @Club, @Now);

    -- Older bins stay. A later 15-minute sync must not erase the hours already stored.
    COMMIT TRANSACTION;
    SELECT @@ROWCOUNT AS Affected;
END
GO

CREATE OR ALTER PROCEDURE dashboard.usp_GetChartPoints
    @Domain      nvarchar(32) = NULL,
    @Chart       nvarchar(64) = NULL,
    @Since       datetimeoffset(7) = NULL,
    @Environment nvarchar(32) = NULL,
    @ClubId      nvarchar(32) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    SELECT
        Domain,
        Chart,
        Series,
        BinStart,
        Value,
        Window,
        Environment,
        FetchedAt
    FROM dashboard.MetricPoint
    WHERE (
            (@Domain IS NULL AND Domain IN (N'hotels', N'cruises'))
            OR Domain = @Domain
          )
      AND (@Chart IS NULL OR Chart = @Chart)
      AND (@Since IS NULL OR BinStart >= @Since)
      AND (@Environment IS NULL OR Environment = @Environment)
      AND (@ClubId IS NULL OR ClubId = @ClubId)
    ORDER BY Domain, Chart, BinStart, Series;
END
GO
